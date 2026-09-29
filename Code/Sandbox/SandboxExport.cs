using System.Collections;
using System.Globalization;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Combat.History.Entries;
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
    private static int RelicCounter(object? relic) => Id(relic) switch
    {
        "SHURIKEN" or "KUNAI" or "ORNAMENTAL_FAN" => Number(relic, "_attacksPlayedThisTurn") % 3,
        "LETTER_OPENER" => Number(relic, "_skillsPlayedThisTurn") % 3,
        "NUNCHAKU" or "PEN_NIB" => Number(relic, "AttacksPlayed") % 10,
        "HAPPY_FLOWER" => Number(relic, "TurnsSeen"),
        _ => Number(relic, "DisplayAmount")
    };
    private static int Number(object? owner, string member) => Convert.ToInt32(Required(owner, member), CultureInfo.InvariantCulture);
    private static string Id(object? model) => Required(Required(model, "Id"), "Entry").ToString()!;
    private static object?[] Items(object? value) => value is IEnumerable items ? items.Cast<object?>().ToArray()
        : throw new InvalidOperationException("Missing sandbox collection");
    private static string Snake(string name) => string.Concat(name.Select((ch, index) => index > 0 && char.IsUpper(ch) ? "_" + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString()));

    private static string? DamageIncrease(object? card)
    {
        if (Id(card) == "SOVEREIGN_BLADE") return (Convert.ToDecimal(Required(Required(Required(card, "DynamicVars"), "Damage"), "BaseValue"), CultureInfo.InvariantCulture) - 10m).ToString("G29", CultureInfo.InvariantCulture);
        var member = Id(card) switch { "RAMPAGE" => "_extraDamageFromPlays", "THRASH" or "KINGLY_PUNCH" => "_extraDamage", _ => null };
        return member is null ? null : Convert.ToDecimal(Required(card, member), CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture);
    }

    private static bool AddedSly(object? card) => Items(Required(card, "Keywords")).Any(keyword => keyword?.ToString() == "Sly")
        && !Items(Required(Required(card, "CanonicalInstance"), "Keywords")).Any(keyword => keyword?.ToString() == "Sly");

    private static bool TemporaryKeyword(object? card, string member) => (bool)(typeof(MegaCrit.Sts2.Core.Models.CardModel)
        .GetProperty(member, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(card)
        ?? throw new InvalidOperationException($"Missing card keyword state {member}"));

    private static bool? ShivRetained(object? card) => Id(card) == "SHIV" ? Items(Required(card, "Keywords")).Any(keyword => keyword?.ToString() == "Retain") : null;

    private static object? CardHistory(object? card)
    {
        if (Id(card) is not ("BOLAS" or "THRUMMING_HATCHET")) return null;
        var owner = Required(card, "Owner");
        var turn = Number(Required(owner, "PlayerCombatState"), "TurnNumber");
        var field = typeof(MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry).GetField("_playerTurnNumbers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var turns = new HashSet<int>();
        foreach (var entry in CombatManager.Instance.History.CardPlaysFinished.Where(value => ReferenceEquals(value.CardPlay.Card, card)))
        {
            var captured = (IDictionary)field.GetValue(entry)!;
            turns.Add(Convert.ToInt32(captured[Required(owner, "NetId")] ?? throw new InvalidOperationException("Card history is missing its owner"), CultureInfo.InvariantCulture));
        }
        return new { current_turn = turns.Contains(turn), previous_turn = turns.Contains(turn - 1) };
    }

    private static int? PowerCardsLeft(object? power) => Id(power) is "AUTOMATION_POWER" or "PANACHE_POWER" ? Number(power, "DisplayAmount") : null;

    private static bool? PaleActivated(object? power)
    {
        if (Id(power) != "PALE_BLUE_DOT_POWER") return null;
        var field = typeof(MegaCrit.Sts2.Core.Models.PowerModel).GetField("_internalData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return Reflect.GetMember(field.GetValue(power), "alreadyActivatedThisTurn") as bool? ?? false;
    }

    private static int? VoidManualPlays(object? power)
    {
        if (Id(power) != "VOID_FORM_POWER") return null;
        var field = typeof(MegaCrit.Sts2.Core.Models.PowerModel).GetField("_internalData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return Number(field.GetValue(power), "cardsPlayedThisTurn");
    }

    private static bool? PanacheActive(object? power)
    {
        if (Id(power) != "PANACHE_POWER") return null;
        var field = typeof(MegaCrit.Sts2.Core.Models.PowerModel).GetField("_internalData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (bool)Required(field.GetValue(power), "alreadyApplied");
    }

    private static int? ShellDamageReceived(object? power)
    {
        if (Id(power) != "HARDENED_SHELL_POWER") return null;
        var field = typeof(MegaCrit.Sts2.Core.Models.PowerModel).GetField("_internalData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return Number(field.GetValue(power), "damageReceivedThisTurn");
    }

    private static int? PowerDamage(object? power)
    {
        if (Id(power) != "THE_BOMB_POWER") return null;
        return Number(Required(Required(power, "DynamicVars"), "Damage"), "BaseValue");
    }

    private static int? SelfDamage(object? power)
    {
        if (Id(power) is not ("INFERNO_POWER" or "CRIMSON_MANTLE_POWER")) return null;
        var vars = Required(power, "DynamicVars");
        return Number(vars.GetType().GetProperty("Item")!.GetValue(vars, ["SelfDamage"]), "BaseValue");
    }

    private static int? SlowCount(object? power)
    {
        if (Id(power) != "SLOW_POWER") return null;
        var vars = Required(power, "DynamicVars");
        return Number(vars.GetType().GetProperty("Item")!.GetValue(vars, ["SlowAmount"]), "BaseValue");
    }

    private static int? PowerTarget(object? power, object?[] players)
    {
        if (Id(power) is not ("THIEVERY_POWER" or "HEIST_POWER")) return null;
        var target = Required(power, "Target");
        var slot = Array.FindIndex(players, player => ReferenceEquals(Required(player, "Creature"), target));
        if (slot < 0) throw new InvalidOperationException("Power target is outside the party");
        return slot;
    }

    private static int? StolenGold(object? power)
    {
        if (Id(power) != "THIEVERY_POWER") return null;
        return Number(Required(Required(power, "DynamicVars"), "Gold"), "BaseValue");
    }

    private static string? PowerSource(object? power, object?[] players, object?[] enemies)
    {
        if (Id(power) is not ("SHRINK_POWER" or "CONSTRICT_POWER" or "STRANGLE_POWER")) return null;
        var applier = Reflect.GetMember(power, "Applier");
        if (applier is null) return "none";
        var player = Array.FindIndex(players, value => ReferenceEquals(Required(value, "Creature"), applier));
        if (player >= 0) return $"player:{player}";
        var enemy = Array.FindIndex(enemies, value => ReferenceEquals(value, applier));
        if (enemy >= 0) return $"enemy:{enemy}";
        throw new InvalidOperationException("Power source is outside combat");
    }

    private static void NormalizeCard(System.Text.Json.Nodes.JsonNode card)
    {
        if (card!["affliction"] is null) card.AsObject().Remove("affliction");
        if (card["star_cost"] is null) card.AsObject().Remove("star_cost");
        if (card!["exhaust_on_next_play"]?.GetValue<bool>() == false) card.AsObject().Remove("exhaust_on_next_play");
        if (card!["base_replay_count"]!.GetValue<int>() == 0) card.AsObject().Remove("base_replay_count");
        if (card!["retained"] is null) card.AsObject().Remove("retained");
        foreach (var keyword in new[] { "added_sly", "single_turn_sly", "single_turn_retain" })
            if (card![keyword]!.GetValue<bool>() == false) card.AsObject().Remove(keyword);
        if (card!["play_history"] is null) card.AsObject().Remove("play_history");
        if (card!["extra_damage"] is null) card.AsObject().Remove("extra_damage");
    }

    private static object? StarCost(object? card)
    {
        var x = (bool)Required(card, "HasStarCostX");
        if (Number(card, "CanonicalStarCost") < 0 && !x) return null;
        var field = typeof(MegaCrit.Sts2.Core.Models.CardModel).GetField("_temporaryStarCosts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var modifiers = Items(field.GetValue(card));
        return new { base_cost = Number(card, "BaseStarCost"), cost = Convert.ToInt32(Reflect.Call(card!, "GetStarCostWithModifiers")), costs_x = x, last_spent = Number(card, "LastStarsSpent"), modifiers = modifiers.Select(modifier => new { amount = Number(modifier, "Cost"), expiration = ((bool)Required(modifier, "ClearsWhenTurnEnds") ? 2 : 0) | ((bool)Required(modifier, "ClearsWhenCardIsPlayed") ? 4 : 0) }).ToArray() };
    }

    private static int CardIdentity(object card)
    {
        if (!cardIds.TryGetValue(card, out var identity))
        {
            identity = cardIds.Count;
            cardIds.Add(card, identity);
        }
        return identity;
    }

    private static object CardValue(object? card)
    {
        var instanceId = CardIdentity(card!);
        var energy = Required(card, "EnergyCost");
        var cost = Reflect.Call(energy, "GetAmountToSpend") ?? throw new InvalidOperationException("Card cost unavailable");
        var enchantment = Reflect.GetMember(card, "Enchantment");
        var affliction = Reflect.GetMember(card, "Affliction");
        return (object)new { instance_id = instanceId, id = Id(card), star_cost = StarCost(card), extra_damage = DamageIncrease(card), play_history = CardHistory(card), retained = ShivRetained(card), added_sly = AddedSly(card), single_turn_sly = TemporaryKeyword(card, "HasSingleTurnSly"), single_turn_retain = TemporaryKeyword(card, "HasSingleTurnRetain"), base_replay_count = Number(card, "BaseReplayCount"), exhaust_on_next_play = (bool)Required(card, "ExhaustOnNextPlay"), upgraded = (bool)Required(card, "IsUpgraded"), cost = Convert.ToInt32(cost), costs_x = (bool)Required(energy, "CostsX"), energy_cost = new {
            base_cost = Number(energy, "_base"), captured_x = (bool)Required(energy, "CostsX") ? (int?)Number(energy, "CapturedXValue") : null,
            modifiers = Items(Required(energy, "_localModifiers")).Select(modifier => new { amount = Number(modifier, "Amount"), type = Required(modifier, "Type").ToString()!.ToLowerInvariant(), expiration = Number(modifier, "Expiration"), reduce_only = (bool)Required(modifier, "IsReduceOnly") }).ToArray()
        }, affliction = affliction is null ? null : new { id = Id(affliction), amount = Number(affliction, "Amount") }, enchantment = enchantment is null ? null : Id(enchantment) };
    }

    private static object? NightmareCard(object? power)
    {
        if (Id(power) != "NIGHTMARE_POWER") return null;
        var field = typeof(MegaCrit.Sts2.Core.Models.PowerModel).GetField("_internalData", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return CardValue(Required(field.GetValue(power), "selectedCard"));
    }

    private static object[] Powers(object creature, object?[] players, object?[] enemies) => Items(Required(creature, "Powers"))
        .Select(power => (object)new { id = Id(power), amount = Number(power, "Amount"), energy_left = Id(power) == "ORBIT_POWER" ? (int?)Number(power, "DisplayAmount") : null, pale_activated = PaleActivated(power), manual_plays_this_turn = VoidManualPlays(power), strength_gain = Id(power) == "MONOLOGUE_POWER" ? (int?)Number(Required(Required(power, "DynamicVars"), "Strength"), "BaseValue") : null, strength_applied = Id(power) == "MONOLOGUE_POWER" ? (int?)Number(power, "DisplayAmount") : null, poison_count = Id(power) == "OUTBREAK_POWER" ? (int?)Number(power, "DisplayAmount") : null, selected_card = NightmareCard(power), self_damage = SelfDamage(power), cards_left = PowerCardsLeft(power), panache_active = PanacheActive(power), damage = PowerDamage(power), slow_count = SlowCount(power), skittish_used = Id(power) == "SKITTISH_POWER" ? (bool?)Required(power, "HasGainedBlockThisTurn") : null, shell_damage_received = ShellDamageReceived(power), shell_remaining = Id(power) == "HARDENED_SHELL_POWER" ? (int?)Number(power, "DisplayAmount") : null, target_player = PowerTarget(power, players), stolen_gold = StolenGold(power), ritual_just_applied = Id(power) == "RITUAL_POWER" ? (bool?)Required(power, "WasJustAppliedByEnemy") : null, applier = PowerSource(power, players, enemies), skip_next_duration_tick = (Id(power) is "WEAK_POWER" or "VULNERABLE_POWER" or "FRAIL_POWER") && (bool)Required(power, "SkipNextDurationTick") }).ToArray();

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
        foreach (var card in Items(Required(combat, "_allCards")))
            if (!cardIds.ContainsKey(card!)) cardIds.Add(card!, cardIds.Count);
        object[] Cards(object pcs, string pile) => Items(Required(Required(pcs, pile), "Cards")).Select(CardValue).ToArray();
        var enemyCreatures = Items(Required(room, "Enemies"));
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
                cards_exhausted_this_turn = manager.History.Entries.OfType<CardExhaustedEntry>().Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature)),
                hp_loss_this_turn = manager.History.Entries.OfType<DamageReceivedEntry>().Any(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Receiver, creature) && entry.Result.UnblockedDamage > 0),
                skills_played_this_turn = manager.History.CardPlaysStarted.Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature) && entry.CardPlay.Card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill),
                cards_played_this_turn = manager.History.CardPlaysStarted.Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature)),
                shivs_played_this_turn = manager.History.CardPlaysFinished.Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature) && entry.CardPlay.Card.Id.Entry == "SHIV"),
                finished_attacks_this_turn = manager.History.CardPlaysFinished.Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature) && entry.CardPlay.Card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack),
                finished_skills_this_turn = manager.History.CardPlaysFinished.Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature) && entry.CardPlay.Card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Skill),
                attack_hits_this_turn = enemyCreatures.Select((enemy, index) => new { index, count = manager.History.Entries.OfType<DamageReceivedEntry>().Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Dealer, creature) && ReferenceEquals(entry.Receiver, enemy) && entry.Result.Props.IsPoweredAttack()) }).Where(value => value.count > 0).ToDictionary(value => value.index.ToString(CultureInfo.InvariantCulture), value => value.count),
                finished_cards_this_turn = manager.History.CardPlaysFinished.Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature)),
                previous_turn_finished_cards = manager.History.CardPlaysFinished.Count(entry => entry.HappenedLastPlayerTurn((MegaCrit.Sts2.Core.Entities.Players.Player)player!) && ReferenceEquals(entry.Actor, creature)),
                stars_gained_this_turn = manager.History.Entries.OfType<StarsModifiedEntry>().Where(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature) && entry.Amount > 0).Sum(entry => entry.Amount),
                cards_generated_in_combat = manager.History.Entries.OfType<CardGeneratedEntry>().Count(entry => ReferenceEquals(entry.Creator, player)),
                cards_discarded_this_turn = manager.History.Entries.OfType<CardDiscardedEntry>().Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature)),
                cards_drawn_in_combat = manager.History.Entries.OfType<CardDrawnEntry>().Count(entry => ReferenceEquals(entry.Actor, creature)),
                attacks_played_this_turn = manager.History.CardPlaysStarted.Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature) && entry.CardPlay.Card.Type == MegaCrit.Sts2.Core.Entities.Cards.CardType.Attack),
                powered_block_gains_this_turn = manager.History.Entries.OfType<BlockGainedEntry>().Count(entry => entry.HappenedThisTurn((ICombatState)combat) && ReferenceEquals(entry.Actor, creature) && entry.Props.IsCardOrMonsterMove()),
                hp_loss_count = manager.History.Entries.OfType<DamageReceivedEntry>().Count(entry => ReferenceEquals(entry.Receiver, creature) && entry.Result.UnblockedDamage > 0),
                energy = Number(pcs, "Energy"), stars = Number(pcs, "Stars"), max_energy = Number(pcs, "MaxEnergy"), gold = Number(player, "Gold"),
                hand = Cards(pcs, "Hand"), draw_pile = Cards(pcs, "DrawPile"), discard_pile = Cards(pcs, "DiscardPile"), exhaust_pile = Cards(pcs, "ExhaustPile"), powers = Powers(creature, players, enemyCreatures),
                relics = Items(Required(player, "Relics")).Select(relic => new { id = Id(relic), counter = RelicCounter(relic), should_trigger = Id(relic) == "ORICHALCUM" ? (bool?)Required(relic, "_shouldTrigger") : null, attack_to_double = Id(relic) == "PEN_NIB" && Reflect.GetMember(relic, "_attackToDouble") is { } doubled ? (int?)CardIdentity(doubled) : null, melted = (bool)Required(relic, "IsMelted"), cards_played_last_turn = Id(relic) == "POCKETWATCH" ? (int?)Number(relic, "_cardsPlayedLastTurn") : null, attacked_this_turn = Id(relic) == "ART_OF_WAR" ? (bool?)Required(relic, "_anyAttacksPlayedThisTurn") : null, attacked_last_turn = Id(relic) == "ART_OF_WAR" ? (bool?)Required(relic, "_anyAttacksPlayedLastTurn") : null, triggered_this_turn = Id(relic) == "DEMON_TONGUE" ? (bool?)Required(relic, "_triggeredThisTurn") : null, strength_applied = Id(relic) == "RED_SKULL" ? (bool?)Required(relic, "_strengthApplied") : null, used_this_combat = Id(relic) == "RUINED_HELMET" ? (bool?)Required(relic, "_usedThisCombat") : null }).ToArray(),
                potions = Items(Required(player, "PotionSlots")).Select(potion => potion is null ? null : new { id = Id(potion) }).ToArray()
            };
        }).ToArray();
        var enemies = enemyCreatures.Select((creature, slot) =>
        {
            var monster = Required(creature, "Monster");
            var move = Required(monster, "NextMove");
            return new
            {
                slot, slot_name = (Id(monster) is "WRIGGLER" or "TWO_TAILED_RAT" or "LIVING_FOG" or "GAS_BOMB" or "GREMLIN_MERC" or "SNEAKY_GREMLIN" or "FAT_GREMLIN") ? Required(creature, "SlotName")?.ToString() : null, id = Id(monster), pressure_gun_damage = Id(monster) == "WATERFALL_GIANT" ? (int?)Number(monster, "CurrentPressureGunDamage") : null, steam_eruption_damage = Id(monster) == "WATERFALL_GIANT" ? (int?)Number(monster, "SteamEruptionDamage") : null, summon_turns = Id(monster) == "TWO_TAILED_RAT" ? (int?)Number(monster, "TurnsUntilSummonable") : null, summon_count = Id(monster) == "TWO_TAILED_RAT" ? (int?)Number(monster, "CallForBackupCount") : null, current_hp = Number(creature, "CurrentHp"), max_hp = Number(creature, "MaxHp"), block = Number(creature, "Block"), powers = Powers(creature!, players, enemyCreatures),
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
            schema = "sandbox_position/3", build_id = Sts2Version.Current.Split('+')[0], seed = Required(Required(run, "Rng"), "StringSeed").ToString(), ascension = Number(run, "AscensionLevel"),
            act = Number(run, "CurrentActIndex") + 1, total_floor = Number(run, "TotalFloor"), turn = Number(combat, "RoundNumber"), turn_side = "player", active_player = active,
            players = exportedPlayers, enemies, counters = streams, next_card_instance_id = cardIds.Count, cards_played_in_combat = manager.History.CardPlaysFinished.Count()
        }, new JsonSerializerOptions { WriteIndented = true })!.AsObject();
        foreach (var player in node["players"]!.AsArray())
            foreach (var pile in new[] { "hand", "draw_pile", "discard_pile", "exhaust_pile", "play_pile" })
                if (player![pile] is System.Text.Json.Nodes.JsonArray cards)
                    foreach (var card in cards)
                        NormalizeCard(card!);
        foreach (var enemy in node["enemies"]!.AsArray())
        {
            if (enemy!["slot_name"] is null) enemy.AsObject().Remove("slot_name");
            if (enemy["pressure_gun_damage"] is null) enemy.AsObject().Remove("pressure_gun_damage");
            if (enemy["steam_eruption_damage"] is null) enemy.AsObject().Remove("steam_eruption_damage");
            if (enemy["summon_turns"] is null) enemy.AsObject().Remove("summon_turns");
            if (enemy["summon_count"] is null) enemy.AsObject().Remove("summon_count");
        }
        foreach (var player in node["players"]!.AsArray())
            foreach (var relic in player!["relics"]!.AsArray())
            {
                if (!relic!["melted"]!.GetValue<bool>()) relic.AsObject().Remove("melted");
                foreach (var field in new[] { "should_trigger", "triggered_this_turn", "strength_applied", "used_this_combat", "cards_played_last_turn", "attacked_this_turn", "attacked_last_turn", "attack_to_double" })
                    if (relic[field] is null) relic.AsObject().Remove(field);
            }
        foreach (var creature in node["players"]!.AsArray().Concat(node["enemies"]!.AsArray()))
            foreach (var power in creature!["powers"]!.AsArray())
            {
                if (!power!["skip_next_duration_tick"]!.GetValue<bool>()) power.AsObject().Remove("skip_next_duration_tick");
                if (power["target_player"] is null) power.AsObject().Remove("target_player");
                if (power["stolen_gold"] is null) power.AsObject().Remove("stolen_gold");
                if (power["ritual_just_applied"] is null) power.AsObject().Remove("ritual_just_applied");
                if (power["skittish_used"] is null) power.AsObject().Remove("skittish_used");
                if (power["shell_damage_received"] is null) power.AsObject().Remove("shell_damage_received");
                if (power["shell_remaining"] is null) power.AsObject().Remove("shell_remaining");
                if (power["cards_left"] is null) power.AsObject().Remove("cards_left");
                if (power["poison_count"] is null) power.AsObject().Remove("poison_count");
                if (power["energy_left"] is null) power.AsObject().Remove("energy_left");
                if (power["pale_activated"] is null) power.AsObject().Remove("pale_activated");
                if (power["manual_plays_this_turn"] is null) power.AsObject().Remove("manual_plays_this_turn");
                if (power["strength_gain"] is null) power.AsObject().Remove("strength_gain");
                if (power["strength_applied"] is null) power.AsObject().Remove("strength_applied");
                if (power["selected_card"] is System.Text.Json.Nodes.JsonNode selectedCard) NormalizeCard(selectedCard);
                else power.AsObject().Remove("selected_card");
                if (power["panache_active"] is null) power.AsObject().Remove("panache_active");
                if (power["damage"] is null) power.AsObject().Remove("damage");
                if (power["slow_count"] is null) power.AsObject().Remove("slow_count");
                if (power["self_damage"] is null) power.AsObject().Remove("self_damage");
                if (power["applier"] is null) power.AsObject().Remove("applier");
            }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
