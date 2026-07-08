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
    // used...) fires a beat as soon as the MinGap debounce allows; the fixed cadences
    // cover non-event changes (enemy HP, intents, block) in combat and the slow drift
    // between rooms. The combat floor is the visible latency ceiling for a spectator (HP
    // bars / intents only refresh that often), so it's kept tight; idle is looser since
    // most out-of-combat changes are event-driven (act/event/buy fire their own beat).
    // Net traffic is still lower than a fixed fast timer because idle moments send nothing
    // extra. Cost of a tighter combat floor: ~2.5x the combat beats per player in a run.
    private const int IdleIntervalSeconds = 8;
    private const int CombatIntervalSeconds = 2;
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
        string? lastScreen = null; // screen of the last delivered beat, for the change nudge
        while (true)
        {
            try
            {
                var s = LiveStateProducer.Latest;
                var interval = s?.Combat != null ? CombatIntervalSeconds : IdleIntervalSeconds;
                var gap = (DateTimeOffset.UtcNow - lastBeat).TotalSeconds;
                // Entering a shop/rest/treasure fires no ticker event, so without this those
                // panels wait out the idle floor to first appear. Nudge a beat the moment the
                // screen changes (debounced by MinGap), so transitions show up in ~1-2s.
                var screenChanged = s is { Status: "ok", InRun: true, IsGameOver: false }
                                    && s.Screen != lastScreen;
                if (gap >= interval
                    || (gap >= MinGapSeconds && (Core.RunEvents.Pending > 0 || screenChanged)))
                {
                    await TickAsync(client).ConfigureAwait(false);
                    lastBeat = DateTimeOffset.UtcNow;
                    lastScreen = s?.Screen;
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
            // Clear our entry once; if the ping fails the TTL cleans up anyway. A death rides the
            // ended ping: the heartbeat is gated off once IsGameOver, so this is its carrier.
            if (_published)
            {
                var end = s?.Death is { } de
                    ? JsonSerializer.Serialize(new { ended = true, death = new { by = de.By, line = de.Line } })
                    : "{\"ended\":true}";
                if (await client.PostPresenceAsync(end).ConfigureAwait(false)) _published = false;
            }
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
            block = s.Block,
            energy = s.Combat != null ? s.Energy : (int?)null,
            max_energy = s.MaxEnergy,
            gold = s.Gold,
            ascension = s.Ascension,
            seed = s.Seed,
            run_time = s.RunTime,
            // Active run modifiers (daily/custom mutators); null on a standard run.
            modifiers = s.Modifiers.Count > 0 ? s.Modifiers.ToArray() : null,
            screen = s.Screen,
            player_count = s.PlayerCount,
            // Per-player vitals for a future co-op live view. Only in co-op (2+ players); null
            // solo, where the flattened top-level fields already cover the one player. Not
            // consumed by the site yet, just plumbed through.
            players = s.PlayerCount > 1 ? s.Players.Select(pl => new
            {
                character = pl.Character, hp = pl.CurrentHp, max_hp = pl.MaxHp, block = pl.Block,
                alive = pl.IsAlive, gold = pl.Gold, energy = pl.Energy, deck_size = pl.DeckSize,
                relic_count = pl.RelicCount, potion_count = pl.PotionCount, is_me = pl.IsLocal,
                // Co-op turn indicator: true once this player has ended their turn this round.
                ended_turn = pl.EndedTurn,
            }).ToArray() : null,
            sts2_version = s.Sts2Version,
            username = Config.Username,
            turn = s.Combat?.Turn,
            turn_side = s.Combat?.TurnSide, // "player" / "enemy", whose turn it is
            // Live damage for the spectator combat view (null when not fighting).
            damage_dealt = s.Combat?.DamageDealt,
            damage_dealt_this_turn = s.Combat?.DamageDealtThisTurn,
            damage_taken = s.Combat?.DamageTaken,
            biggest_hit = s.Combat?.BiggestHit,
            // The player's current hand (combat only), ids in hand order, `+` marks upgraded.
            hand = s.Combat?.Hand.Select(h => h.Upgraded ? h.Id + "+" : h.Id).ToArray(),
            draw_count = s.Combat?.DrawCount,
            discard_count = s.Combat?.DiscardCount,
            exhaust_count = s.Combat?.ExhaustCount,
            // Full pile contents for spectator hover (combat only); `+` marks upgraded.
            draw_pile = s.Combat?.DrawPile.Select(c => c.Upgraded ? c.Id + "+" : c.Id).ToArray(),
            discard_pile = s.Combat?.DiscardPile.Select(c => c.Upgraded ? c.Id + "+" : c.Id).ToArray(),
            exhaust_pile = s.Combat?.ExhaustPile.Select(c => c.Upgraded ? c.Id + "+" : c.Id).ToArray(),
            // The player's own buffs/debuffs this fight (id + stacks); null when not fighting.
            player_powers = s.Combat?.PlayerPowers.Select(p => new { id = p.Id, amount = p.Amount }).ToArray(),
            // Channeled orbs (Regent), in slot order, with passive/evoke values; orb_slots = capacity.
            orbs = s.Combat?.Orbs.Select(o => new { id = o.Id, passive = o.Passive, evoke = o.Evoke }).ToArray(),
            orb_slots = s.Combat?.OrbSlots,
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
                // Enemy buffs/debuffs (id + stacks) so the spectator sees Vulnerable/Strength/etc.
                powers = e.Powers.Select(p => new { id = p.Id, amount = p.Amount }).ToArray(),
                intents = e.Intents.Select(i => new { type = i.Type, dmg = i.Damage, hits = i.Hits }).ToArray(),
            }).ToArray(),
            // Friendly pets (Necrobinder's Osty etc.), with HP and which player owns them; null when
            // nobody has one out. owner indexes into players[].
            pets = s.Combat != null && s.Combat.Pets.Count > 0 ? s.Combat.Pets.Select(p => new
            {
                id = p.Id, name = p.Name, hp = p.CurrentHp, max_hp = p.MaxHp,
                block = p.Block, alive = p.IsAlive, owner = p.Owner,
            }).ToArray() : null,
            events = events.Select(e => new { k = e.Kind, v = e.Value, turn = e.Turn, t = e.At }).ToArray(),
            deck = s.Deck.Select(d => d.Upgraded ? d.Id + "+" : d.Id).ToArray(),
            relics = s.Relics.Select(r => r.Id).ToArray(),
            potions = s.Potions.Select(p => p.Id).ToArray(),
            // The loot/rewards screen contents (gold, offered cards, relics, potions); null off it.
            loot = s.Loot == null ? null : new
            {
                gold = s.Loot.Gold,
                cards = s.Loot.Cards.ToArray(),
                relics = s.Loot.Relics.ToArray(),
                potions = s.Loot.Potions.ToArray(),
                card_removal = s.Loot.CardRemoval,
                // ScrollBoxes bundle groups (each a list of bare ids); null off a bundle screen. When
                // present the flat `cards` list is empty, so consumers render packs instead.
                packs = s.Loot.Packs?.Select(p => p.ToArray()).ToArray(),
            },
            path = graph?.Path,
            pos = graph?.Pos,
            // Per-visited-node reveals [col, row, room_type, encounter_id]: what each circle
            // actually was (resolves "?" nodes). Rides every beat like path; grows as walked.
            reveals = graph?.Reveals,
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
                    key = o.Key, text = o.Text, desc = o.Desc, card = o.Card, relic = o.Relic,
                    locked = o.Locked, proceed = o.Proceed, chosen = o.Chosen,
                }).ToArray(),
            } : null,
            shop = s.Shop is { } sh ? new
            {
                cards = sh.Cards.Select(ShopItem).ToArray(),
                relics = sh.Relics.Select(ShopItem).ToArray(),
                potions = sh.Potions.Select(ShopItem).ToArray(),
                removal = sh.Removal is { } r ? new { cost = r.Cost, stocked = r.Stocked } : null,
            } : null,
            // Campfire options (Rest/Smith/Dig/...) so the spectator's rest panel shows real
            // buttons; null off a rest site.
            rest = s.Rest is { } rs ? new
            {
                options = rs.Options.Select(o => new { id = o.Id, title = o.Title, enabled = o.Enabled }).ToArray(),
            } : null,
            // Death detail for the death screen (killer + loss quote); null until the run ends in
            // death. Also sent on the ended ping above, which is the reliable carrier.
            death = s.Death is { } d ? new { by = d.By, line = d.Line } : null,
            // The act's specific upcoming fights/events, for the spectator route panel.
            route = s.Route is { } rt ? new
            {
                boss = RouteRef(rt.Boss),
                ancient = RouteRef(rt.Ancient),
                monsters = rt.Monsters.Select(RouteRef).ToArray(),
                elites = rt.Elites.Select(RouteRef).ToArray(),
                events = rt.Events.Select(RouteRef).ToArray(),
            } : null,
            // Per-cleared-floor summaries (the game's "previous floor" hover): room/enemy, turns,
            // damage/heal, HP+gold snapshot, and rewards taken vs skipped. Null until a floor is
            // cleared. Grows across the run; rides every beat like reveals/route.
            floor_history = s.FloorHistory.Count > 0 ? s.FloorHistory.Select(fl => new
            {
                floor = fl.Floor,
                act = fl.Act,
                type = fl.Type,
                encounter_id = fl.EncounterId,
                hp = fl.Hp,
                max_hp = fl.MaxHp,
                gold = fl.Gold,
                turns = fl.Turns,
                damage_taken = fl.DamageTaken,
                healed = fl.Healed,
                gold_spent = fl.GoldSpent,
                gold_gained = fl.GoldGained,
                rewards = fl.Rewards.Select(r => new { kind = r.Kind, id = r.Id }).ToArray(),
                skipped = fl.Skipped.Select(r => new { kind = r.Kind, id = r.Id }).ToArray(),
            }).ToArray() : null,
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
