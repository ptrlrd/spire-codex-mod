using System.Collections;
using System.Globalization;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using SpireCodex.Core;

namespace SpireCodex.Sandbox;

public partial class SandboxExport : Node
{
    private static SandboxExport? instance;
    private static object? capturedCombat;
    private static readonly Dictionary<object, int> cardIds = new(ReferenceEqualityComparer.Instance);

    public static void Start()
    {
        if (instance is not null || Engine.GetMainLoop() is not SceneTree tree) return;
        instance = new SandboxExport { Name = "SpireCodexSandboxExport" };
        tree.Root.CallDeferred(MethodName.AddChild, instance);
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (input is not InputEventKey { Pressed: true, Echo: false } key) return;
        var configured = SpireCodexConfig.SandboxKeycode;
        if (configured == Key.None || (key.Keycode != configured && key.PhysicalKeycode != configured)) return;
        try
        {
            var json = Capture();
            var folder = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SpireCodex", "sandbox");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, DateTime.UtcNow.ToString("yyyyMMddTHHmmss.fffffffZ", CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(path, json);
            DisplayServer.ClipboardSet(json);
            MainFile.Logger.Info($"sandbox position saved to {path} and copied to clipboard");
            GetViewport().SetInputAsHandled();
        }
        catch (Exception error)
        {
            MainFile.Logger.Warn($"sandbox export unavailable: {error.Message}");
        }
    }

    private static object Required(object? owner, string member) => Reflect.GetMember(owner, member)
        ?? throw new InvalidOperationException($"Missing sandbox member {member}");
    private static int Number(object? owner, string member) => Convert.ToInt32(Required(owner, member), CultureInfo.InvariantCulture);
    private static string Id(object? model) => Required(Required(model, "Id"), "Entry").ToString()!;
    private static object?[] Items(object? value) => value is IEnumerable items ? items.Cast<object?>().ToArray()
        : throw new InvalidOperationException("Missing sandbox collection");
    private static string Snake(string name) => string.Concat(name.Select((ch, index) => index > 0 && char.IsUpper(ch) ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));

    private static object[] Powers(object creature) => Items(Required(creature, "Powers"))
        .Select(power => (object)new { id = Id(power), amount = Number(power, "Amount"), skip_next_duration_tick = (Id(power) is "WEAK_POWER" or "VULNERABLE_POWER" or "FRAIL_POWER") && (bool)Required(power, "SkipNextDurationTick") }).ToArray();

    public static string Capture()
    {
        var run = Sts2Access.LiveRunState ?? throw new InvalidOperationException("No active run");
        var room = Required(run, "CurrentRoom");
        var combat = Required(room, "CombatState");
        var manager = CombatManager.Instance;
        if (!manager.IsInProgress || manager.PlayerActionsDisabled || Required(combat, "CurrentSide").ToString() != "Player")
            throw new InvalidOperationException("Export at an idle player turn");
        var players = Items(Required(run, "Players"));
        var streams = new SortedDictionary<string, int>();
        void Collect(object set, string prefix)
        {
            var saved = Reflect.Call(set, "ToSerializable") ?? throw new InvalidOperationException("RNG state unavailable");
            if (Reflect.GetMember(saved, "Counters") is IDictionary counters)
            {
                foreach (DictionaryEntry entry in counters)
                    streams.Add(prefix + Snake(entry.Key.ToString()!), Convert.ToInt32(entry.Value));
            }
            else if (Reflect.GetMember(saved, "Rngs") is IDictionary rngs)
            {
                foreach (DictionaryEntry entry in rngs)
                    streams.Add(prefix + Snake(entry.Key.ToString()!), Number(entry.Value, "counter"));
            }
            else throw new InvalidOperationException("RNG set unavailable");
        }
        Collect(Required(run, "Rng"), "");
        if (!ReferenceEquals(capturedCombat, combat))
        {
            capturedCombat = combat;
            cardIds.Clear();
        }
        if (cardIds.Count > 0)
            foreach (var card in Items(Required(combat, "_allCards")))
                if (!cardIds.ContainsKey(card!)) cardIds.Add(card!, cardIds.Count);
        object[] Cards(object pcs, string pile) => Items(Required(Required(pcs, pile), "Cards")).Select(card =>
        {
            if (!cardIds.TryGetValue(card!, out var instanceId))
            {
                instanceId = cardIds.Count;
                cardIds.Add(card!, instanceId);
            }
            var energy = Required(card, "EnergyCost");
            var cost = Reflect.Call(energy, "GetAmountToSpend") ?? throw new InvalidOperationException("Card cost unavailable");
            var enchantment = Reflect.GetMember(card, "Enchantment");
            return (object)new { instance_id = instanceId, id = Id(card), upgraded = (bool)Required(card, "IsUpgraded"), cost = Convert.ToInt32(cost), costs_x = (bool)Required(energy, "CostsX"), enchantment = enchantment is null ? null : Id(enchantment) };
        }).ToArray();
        var exportedPlayers = players.Select((player, slot) =>
        {
            if (Reflect.CallWith(manager, "IsExecutingCardOrPotionEffect", player) is not false)
                throw new InvalidOperationException("Wait until the current action finishes");
            var pcs = Required(player, "PlayerCombatState");
            if (Items(Required(Required(pcs, "PlayPile"), "Cards")).Length != 0)
                throw new InvalidOperationException("Wait until played cards resolve");
            var creature = Required(player, "Creature");
            Collect(Required(player, "PlayerRng"), $"p{slot}.");
            return new
            {
                character = Id(Required(player, "Character")), current_hp = Number(creature, "CurrentHp"), max_hp = Number(creature, "MaxHp"), block = Number(creature, "Block"),
                energy = Number(pcs, "Energy"), max_energy = Number(pcs, "MaxEnergy"), gold = Number(player, "Gold"),
                hand = Cards(pcs, "Hand"), draw_pile = Cards(pcs, "DrawPile"), discard_pile = Cards(pcs, "DiscardPile"), exhaust_pile = Cards(pcs, "ExhaustPile"), powers = Powers(creature),
                relics = Items(Required(player, "Relics")).Select(relic => new { id = Id(relic), counter = Number(relic, "DisplayAmount") }).ToArray(),
                potions = Items(Required(player, "PotionSlots")).Select(potion => potion is null ? null : new { id = Id(potion) }).ToArray()
            };
        }).ToArray();
        var enemies = Items(Required(room, "Enemies")).Select((creature, slot) =>
        {
            var monster = Required(creature, "Monster");
            var move = Required(monster, "NextMove");
            return new
            {
                slot, id = Id(monster), current_hp = Number(creature, "CurrentHp"), max_hp = Number(creature, "MaxHp"), block = Number(creature, "Block"), powers = Powers(creature!),
                move_id = Required(move, "Id").ToString(), move_history = Items(Required(Required(monster, "MoveStateMachine"), "StateLog")).Select(state => Required(state, "Id").ToString()).ToArray(),
                intents = Items(Required(move, "Intents")).Select(intent =>
                {
                    var damage = Reflect.GetMember(intent, "DamageCalc") is Delegate calculate ? (int?)Convert.ToInt32(calculate.DynamicInvoke()) : null;
                    return new { type = Required(intent, "IntentType").ToString()!.ToLowerInvariant(), damage, hits = damage.HasValue ? Math.Max(1, Number(intent, "Repeats")) : (int?)null };
                }).ToArray()
            };
        }).ToArray();
        if (streams.Count != 12 + players.Length * 3) throw new InvalidOperationException("Incomplete RNG stream set");
        var active = Array.FindIndex(players, player => ReferenceEquals(player, Sts2Access.LivePlayer));
        if (active < 0) throw new InvalidOperationException("Local player unavailable");
        var node = JsonSerializer.SerializeToNode(new
        {
            schema = "sandbox_position/1", build_id = Sts2Version.Current.Split('+')[0], seed = Required(Required(run, "Rng"), "StringSeed").ToString(), ascension = Number(run, "AscensionLevel"),
            act = Number(run, "CurrentActIndex") + 1, total_floor = Number(run, "TotalFloor"), turn = Number(combat, "RoundNumber"), turn_side = "player", active_player = active,
            players = exportedPlayers, enemies, counters = streams, next_card_instance_id = cardIds.Count
        }, new JsonSerializerOptions { WriteIndented = true })!.AsObject();
        foreach (var creature in node["players"]!.AsArray().Concat(node["enemies"]!.AsArray()))
            foreach (var power in creature!["powers"]!.AsArray())
                if (!power!["skip_next_duration_tick"]!.GetValue<bool>()) power.AsObject().Remove("skip_next_duration_tick");
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
