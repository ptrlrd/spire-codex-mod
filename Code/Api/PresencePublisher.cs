using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SpireCodex.Producer;

namespace SpireCodex.Api;

// Publishes a compact live-run heartbeat to POST /api/presence every ~30s, feeding the
// site's "who is in a run right now" view. Triple-gated: upload consent (the same gate as
// run uploads), the ShareLiveStatus toggle, and a Steam sign-in (the server requires the
// Bearer token, so nobody can publish presence as someone else). When the run ends or a
// gate closes, one ended ping clears the server entry; a crash just falls off the list
// via the server's 90s TTL.
public static class PresencePublisher
{
    // Event-driven with a cadence floor: a fresh ticker event (card played, potion
    // used...) fires a beat as soon as the 2s debounce allows, so plays reach the site
    // in ~2-3s; the fixed cadences cover non-event changes (enemy HP, intents) in
    // combat and the slow drift between rooms. Net traffic is lower than a fixed fast
    // timer because idle moments send nothing extra.
    private const int IdleIntervalSeconds = 15;
    private const int CombatIntervalSeconds = 5;
    private const int MinGapSeconds = 2;

    private static bool _started;
    private static bool _published; // we believe a live entry exists server-side
    private static bool _diagged;
    private static string? _mapKeySent; // seed|act of the last DELIVERED map graph

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _ = LoopAsync();
    }

    private static async Task LoopAsync()
    {
        var client = new SpireCodexClient();
        var lastBeat = DateTimeOffset.MinValue;
        while (true)
        {
            try
            {
                var interval = LiveStateProducer.Latest?.Combat != null
                    ? CombatIntervalSeconds : IdleIntervalSeconds;
                var gap = (DateTimeOffset.UtcNow - lastBeat).TotalSeconds;
                if (gap >= interval || (gap >= MinGapSeconds && Core.RunEvents.Pending > 0))
                {
                    await TickAsync(client).ConfigureAwait(false);
                    lastBeat = DateTimeOffset.UtcNow;
                }
            }
            catch { /* never let the loop die */ }
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }

    private static async Task TickAsync(SpireCodexClient client)
    {
        var s = LiveStateProducer.Latest;
        var live = s is { Status: "ok", InRun: true, IsGameOver: false }
                   && SpireCodexConfig.ShareLiveStatus
                   && Config.UploadRuns
                   && Consent.Granted;

        if (!live)
        {
            // A dead run's tail must not leak into the next run's ticker, and the next
            // run must resend its map graph.
            Core.RunEvents.Clear();
            _mapKeySent = null;
            // Clear our entry once; if the ping fails the TTL cleans up anyway.
            if (_published && await client.PostPresenceAsync("{\"ended\":true}").ConfigureAwait(false))
                _published = false;
            return;
        }

        // Ticker events since the last delivered beat (committed only on success, so a
        // failed POST retries them next tick).
        var events = Core.RunEvents.Peek(40);

        // The act map for the spectator view: path + position ride every beat (small),
        // the static node/edge graph only when the act changes or the last send failed.
        var graph = Core.MapExport.Latest;
        var sendMap = graph != null && graph.Key != _mapKeySent;

        var payload = JsonSerializer.Serialize(new
        {
            character = s!.Character,
            act = s.Act,
            act_name = s.ActName,
            act_floor = s.ActFloor,
            total_floor = s.TotalFloor,
            hp = s.CurrentHp,
            max_hp = s.MaxHp,
            gold = s.Gold,
            ascension = s.Ascension,
            seed = s.Seed,
            screen = s.Screen,
            player_count = s.PlayerCount,
            sts2_version = s.Sts2Version,
            username = Config.Username,
            turn = s.Combat?.Turn,
            // Live damage for the spectator combat view (null when not fighting).
            damage_dealt = s.Combat?.DamageDealt,
            damage_dealt_this_turn = s.Combat?.DamageDealtThisTurn,
            damage_taken = s.Combat?.DamageTaken,
            biggest_hit = s.Combat?.BiggestHit,
            fighting = s.Combat?.Enemies.Where(e => e.IsAlive).Select(e => e.Id).Take(8).ToArray(),
            // Rich enemy detail for the spectator combat view: HP, block, and the upcoming
            // intent(s) so viewers see what's coming ("16 x2 incoming"). Excluded from the
            // roster server-side; null when not fighting.
            enemies = s.Combat?.Enemies.Where(e => e.IsAlive).Take(8).Select(e => new
            {
                id = e.Id,
                name = e.Name,
                hp = e.CurrentHp,
                max_hp = e.MaxHp,
                block = e.Block,
                intents = e.Intents.Select(i => new { type = i.Type, dmg = i.Damage, hits = i.Hits }).ToArray(),
            }).ToArray(),
            events = events.Select(e => new { k = e.Kind, v = e.Value, turn = e.Turn, t = e.At }).ToArray(),
            deck = s.Deck.Select(d => d.Upgraded ? d.Id + "+" : d.Id).ToArray(),
            relics = s.Relics.Select(r => r.Id).ToArray(),
            potions = s.Potions.Select(p => p.Id).ToArray(),
            path = graph?.Path,
            pos = graph?.Pos,
            map = sendMap ? new { act = graph!.Act, nodes = graph.Nodes, edges = graph.Edges } : null,
            // Current-screen detail for the spectator view: present only in an event/shop,
            // null otherwise (the backend clears the stored copy when it sees the null).
            @event = s.Event is { } ev ? new
            {
                id = ev.Id,
                title = ev.Title,
                prompt = ev.Prompt,
                options = ev.Options.Select(o => new
                {
                    key = o.Key, text = o.Text, locked = o.Locked, proceed = o.Proceed, chosen = o.Chosen,
                }).ToArray(),
            } : null,
            shop = s.Shop is { } sh ? new
            {
                cards = sh.Cards.Select(ShopItem).ToArray(),
                relics = sh.Relics.Select(ShopItem).ToArray(),
                potions = sh.Potions.Select(ShopItem).ToArray(),
                removal = sh.Removal is { } r ? new { cost = r.Cost, stocked = r.Stocked } : null,
            } : null,
            // The act's specific upcoming fights/events, for the spectator route panel.
            route = s.Route is { } rt ? new
            {
                boss = RouteRef(rt.Boss),
                ancient = RouteRef(rt.Ancient),
                monsters = rt.Monsters.Select(RouteRef).ToArray(),
                elites = rt.Elites.Select(RouteRef).ToArray(),
                events = rt.Events.Select(RouteRef).ToArray(),
            } : null,
        });

        if (await client.PostPresenceAsync(payload).ConfigureAwait(false))
        {
            _published = true;
            Core.RunEvents.Commit(events.Count);
            if (sendMap) _mapKeySent = graph!.Key;
        }
        else if (!_diagged)
        {
            _diagged = true;
            MainFile.Logger.Info("presence heartbeat not accepted " +
                           "(not signed in, or endpoint not deployed); retrying quietly");
        }
    }

    // One shop slot on the wire. snake_case to match the rest of the presence payload.
    private static object ShopItem(ShopItemInfo i) => new
    {
        id = i.Id, cost = i.Cost, stocked = i.Stocked, on_sale = i.OnSale, slot = i.Slot,
    };

    // One encounter/event reference on the wire (id resolves name/image via the codex).
    private static object? RouteRef(EncounterRef? r)
        => r is null ? null : new { id = r.Id, name = r.Name, room_type = r.RoomType };
}
