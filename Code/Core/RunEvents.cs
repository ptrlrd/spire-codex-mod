using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace SpireCodex.Core;

// One play-by-play moment for the live ticker: Kind is a short verb ("card", "potion",
// "combat", "victory", "buy", "relic", "ancient", "loot", "rest", "upgrade", "choice",
// "death", "act", "event", "remove"), Value the bare entity id (or, for "choice", the option
// label / "Skip" for a skipped reward; for "combat", the encounter id), Turn the combat round,
// At unix seconds.
public sealed record RunEvent(string Kind, string? Value, int? Turn, long At);

// Captures play-by-play events for the presence ticker by prefixing a handful of the
// game's own hook points (MegaCrit.Sts2.Core.Hooks.Hook — first-party static methods
// invoked for every gameplay beat). Capture only: PresencePublisher drains the buffer
// onto the next heartbeat. Degrades silently: a hook renamed by a game patch just stops
// producing its event kind, and every prefix swallows its own exceptions.
public static class RunEvents
{
    private const int BufferCap = 60;

    private static readonly object Gate = new();
    private static readonly List<RunEvent> Buffer = new();

    public static void Apply(Harmony harmony)
    {
        var hook = FindType("MegaCrit.Sts2.Core.Hooks.Hook");
        if (hook == null)
        {
            MainFile.Logger.Info("run-events: Hook type not found; ticker disabled");
            return;
        }

        var patched = 0;
        patched += Patch(harmony, hook, "AfterCardPlayed", nameof(CardPlayedPrefix));
        patched += Patch(harmony, hook, "BeforeCombatStart", nameof(CombatStartPrefix));
        patched += Patch(harmony, hook, "AfterCombatVictory", nameof(VictoryPrefix));
        patched += Patch(harmony, hook, "AfterPotionUsed", nameof(PotionUsedPrefix));
        patched += Patch(harmony, hook, "AfterItemPurchased", nameof(PurchasePrefix));
        patched += Patch(harmony, hook, "AfterDeath", nameof(DeathPrefix));
        patched += Patch(harmony, hook, "AfterActEntered", nameof(ActPrefix));
        patched += Patch(harmony, hook, "AfterRoomEntered", nameof(RoomEnteredPrefix));
        patched += Patch(harmony, hook, "BeforeCardRemoved", nameof(CardRemovedPrefix));
        patched += Patch(harmony, hook, "AfterRewardTaken", nameof(RewardTakenPrefix));
        patched += Patch(harmony, hook, "AfterCardChangedPiles", nameof(CardObtainedPrefix));
        // Relic obtains funnel through RelicCmd.Obtain (not a Hook), including the ancient
        // 3-relic pick; patch the 3-arg overload (the generic Obtain<T> delegates to it).
        patched += PatchOn(harmony, FindType("MegaCrit.Sts2.Core.Commands.RelicCmd"),
                           "Obtain", nameof(RelicObtainPrefix), paramCount: 3);
        // Card upgrades funnel through CardCmd.Upgrade(CardModel, ...); patch that single-card
        // overload (not CardModel.UpgradeInternal, which also fires on upgrade previews).
        patched += PatchOn(harmony, FindType("MegaCrit.Sts2.Core.Commands.CardCmd"),
                           "Upgrade", nameof(UpgradePrefix), paramCount: 2, firstParamType: "CardModel");
        // Rest (heal) at a campfire; the smith option's upgrade is captured by UpgradePrefix.
        patched += PatchOn(harmony, FindType("MegaCrit.Sts2.Core.Entities.RestSite.HealRestSiteOption"),
                           "OnSelect", nameof(RestPrefix), paramCount: 0);
        // Event option chosen (EventOption.Chosen, instance method).
        patched += PatchOn(harmony, FindType("MegaCrit.Sts2.Core.Events.EventOption"),
                           "Chosen", nameof(EventChoicePrefix), paramCount: 0);
        // Skipping a card reward (declining the draft).
        patched += PatchOn(harmony, FindType("MegaCrit.Sts2.Core.Rewards.CardReward"),
                           "OnSkipped", nameof(SkipPrefix), paramCount: 0);
        MainFile.Logger.Info($"run-events: {patched}/16 hooks patched");
    }

    // --- buffer access (PresencePublisher) -------------------------------------------

    // Undelivered event count; the publisher uses it to fire an early heartbeat the
    // moment something happened instead of waiting out the fixed cadence.
    public static int Pending
    {
        get { lock (Gate) return Buffer.Count; }
    }

    public static List<RunEvent> Peek(int max)
    {
        lock (Gate)
        {
            var n = Math.Min(max, Buffer.Count);
            return Buffer.GetRange(0, n);
        }
    }

    // Remove the first `count` events after they were delivered on a heartbeat.
    public static void Commit(int count)
    {
        lock (Gate) Buffer.RemoveRange(0, Math.Min(count, Buffer.Count));
    }

    // Drop everything (run ended / gates closed) so a dead run's tail can't leak into
    // the next run's ticker.
    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
    }

    // --- capture ----------------------------------------------------------------------

    private static void Add(string kind, string? value, object? combatState)
    {
        var turn = Reflect.GetMember(combatState, "RoundNumber") is int r ? r : (int?)null;
        var e = new RunEvent(kind, value, turn, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        lock (Gate)
        {
            Buffer.Add(e);
            if (Buffer.Count > BufferCap) Buffer.RemoveRange(0, Buffer.Count - BufferCap);
        }
    }

    // Hook.AfterCardPlayed(CombatState, PlayerChoiceContext, CardPlay)
    private static void CardPlayedPrefix(object __0, object __2)
    {
        try
        {
            var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(__2, "Card"), "Id"));
            if (id != null) Add("card", id, __0);
        }
        catch { /* capture must never break the game */ }
    }

    // Hook.BeforeCombatStart(IRunState, CombatState?) — the encounter is already populated, so
    // tag the fight with its encounter id (e.g. CORPSE_SLUGS_WEAK) for the EnemyCircle.
    private static void CombatStartPrefix(object __1)
    {
        try
        {
            var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(__1, "Encounter"), "Id"));
            Add("combat", id, __1);
        }
        catch { }
    }

    // Hook.AfterCombatVictory(IRunState, CombatState?, CombatRoom)
    private static void VictoryPrefix(object __1)
    {
        try { Add("victory", null, __1); } catch { }
    }

    // Hook.AfterPotionUsed(IRunState, CombatState?, PotionModel, Creature?)
    private static void PotionUsedPrefix(object __1, object __2)
    {
        try
        {
            var id = Ids.Bare(Reflect.GetString(__2, "Id"));
            if (id != null) Add("potion", id, __1);
        }
        catch { }
    }

    // Hook.AfterItemPurchased(IRunState, Player, MerchantEntry, int). The concrete
    // entries carry their wares differently: relic/potion entries have Model, card
    // entries wrap theirs in CreationResult.Card. Removal-service purchases carry no
    // item at all — skip the buy; BeforeCardRemoved reports the actual card removed.
    private static void PurchasePrefix(object __2)
    {
        try
        {
            if (__2.GetType().Name.Contains("CardRemoval")) return;
            var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(__2, "Model"), "Id"))
                     ?? Ids.Bare(Reflect.GetString(
                            Reflect.GetMember(Reflect.GetMember(__2, "CreationResult"), "Card"), "Id"));
            Add("buy", id, null);
        }
        catch { }
    }

    // Hook.AfterRoomEntered(IRunState, AbstractRoom) — emit the event id when the room
    // is an event, so the ticker can show "Event: Abyssal Baths".
    private static void RoomEnteredPrefix(object __1)
    {
        try
        {
            if (__1.GetType().Name != "EventRoom") return;
            var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(__1, "CanonicalEvent"), "Id"))
                     ?? Ids.Bare(Reflect.GetString(__1, "ModelId"));
            if (id != null) Add("event", id, null);
        }
        catch { }
    }

    // Hook.BeforeCardRemoved(IRunState, CardModel) — shop removal service, events, and
    // anything else that takes a card out of the deck.
    private static void CardRemovedPrefix(object __1)
    {
        try
        {
            var id = Ids.Bare(Reflect.GetString(__1, "Id"));
            if (id != null) Add("remove", id, null);
        }
        catch { }
    }

    // Hook.AfterDeath(IRunState, CombatState?, Creature, ...) — only the player's death
    // is ticker-worthy; enemy deaths are covered by "victory".
    private static void DeathPrefix(object __1, object __2)
    {
        try
        {
            if (Reflect.GetMember(__2, "IsPlayer") is not true) return;
            CaptureDeath(__1, __2); // killer + loss quote, while the combat encounter is still live
            Add("death", null, __1);
        }
        catch { }
    }

    // Capture who killed the player + their loss-message line at the death moment. The encounter
    // is read from the live combat (combatState.Encounter); the loss quote is per-character. Stored
    // on DeathCapture for the snapshot to ship after the run flips to game-over (combat gone by
    // then). Combat deaths only; an event death (no encounter) leaves it unset.
    private static void CaptureDeath(object? combatState, object creature)
    {
        try
        {
            var encounter = Reflect.GetMember(combatState, "Encounter");
            var by = LocOf(Reflect.GetMember(encounter, "Title"));
            if (by == null) return;
            var character = Reflect.GetMember(Reflect.GetMember(creature, "Player"), "Character");
            var line = LocOf(Reflect.CallWith(encounter, "GetLossMessageFor", character));
            DeathCapture.Set(by, line);
        }
        catch { }
    }

    // Resolve a LocString to its display text (GetFormattedText), or null when empty/absent.
    private static string? LocOf(object? loc)
    {
        var s = Reflect.CallString(loc, "GetFormattedText");
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // CardReward.OnSkipped() — the player declined a card reward. Emitted as a "choice" (the kind
    // the live feed renders) with a "Skip" label, so a skip shows in the play-by-play alongside the
    // event leave/choice events.
    private static void SkipPrefix()
    {
        try { Add("choice", "Skip", null); } catch { }
    }

    // Hook.AfterActEntered(IRunState)
    private static void ActPrefix()
    {
        try { Add("act", null, null); } catch { }
    }

    // RelicCmd.Obtain(RelicModel relic, Player player, int index) — the one funnel every
    // relic acquisition routes through (combat/event/boss rewards, and the ancient 3-relic
    // pick, which calls it directly). Tag the ancient case so the ticker can call it out;
    // everything else is a plain "relic".
    private static void RelicObtainPrefix(object __0, object __1)
    {
        try
        {
            var id = Ids.Bare(Reflect.GetString(__0, "Id"));
            if (id != null) Add(InAncientEvent(__1) ? "ancient" : "relic", id, null);
        }
        catch { }
    }

    // True when a relic is being obtained inside an ancient event (Neow, Darv, Vakuu, ...).
    // player -> RunState -> CurrentRoom -> the event model; ancients all derive from
    // AncientEventModel. Any miss defaults to false (a plain "relic").
    private static bool InAncientEvent(object player)
    {
        try
        {
            var room = Reflect.GetMember(Reflect.GetMember(player, "RunState"), "CurrentRoom");
            if (room == null || room.GetType().Name != "EventRoom") return false;
            var ev = Reflect.GetMember(room, "CanonicalEvent");
            for (var t = ev?.GetType(); t != null; t = t.BaseType)
                if (t.Name == "AncientEventModel") return true;
            return false;
        }
        catch { return false; }
    }

    // Hook.AfterRewardTaken(IRunState, Player, Reward) — a reward grabbed off the
    // rewards/loot screen. Relics are already covered by RelicCmd.Obtain ("relic"); here we
    // emit the non-relic loot. "loot" value is a bare potion id, or the gold amount as a
    // numeric string for a gold reward. Card-reward picks (the chosen card isn't exposed
    // here) and removal rewards (covered by "remove") are skipped.
    private static void RewardTakenPrefix(object __2)
    {
        try
        {
            switch (__2.GetType().Name)
            {
                case "PotionReward":
                    var pid = Ids.Bare(Reflect.GetString(Reflect.GetMember(__2, "Potion"), "Id"));
                    if (pid != null) Add("loot", pid, null);
                    break;
                case "GoldReward":
                    var amt = Reflect.GetInt(__2, "Amount", -1);
                    if (amt >= 0) Add("loot", amt.ToString(), null);
                    break;
            }
        }
        catch { }
    }

    // Hook.AfterCardChangedPiles(IRunState, ICombatState?, CardModel, PileType oldPile, ...) —
    // a card gained from a reward / event lands in the deck (oldPile None) out of combat. Reads
    // as "Took <Card>". Skips: in-combat card generation (combatState set), shop buys (the
    // merchant room, already "buy"), pile moves (oldPile not None), and run-start deck setup
    // (no room entered yet).
    private static void CardObtainedPrefix(object __0, object __1, object __2, object __3)
    {
        try
        {
            if (__1 != null) return;                       // mid-combat generation, not a pickup
            if (__3?.ToString() != "None") return;          // a pile move, not a freshly-gained card
            var room = Reflect.GetMember(__0, "CurrentRoom");
            if (room == null) return;                       // run-start deck setup (no room yet)
            if (room.GetType().Name == "MerchantRoom") return; // shop buys are "buy"
            var id = Ids.Bare(Reflect.GetString(__2, "Id"));
            if (id != null) Add("loot", id, null);
        }
        catch { }
    }

    // CardCmd.Upgrade(CardModel card, CardPreviewStyle) — every committed upgrade routes through
    // here (the campfire smith calls it too). Reads as "Upgraded <Card>".
    private static void UpgradePrefix(object __0)
    {
        try
        {
            var id = Ids.Bare(Reflect.GetString(__0, "Id"));
            if (id != null) Add("upgrade", id, null);
        }
        catch { }
    }

    // HealRestSiteOption.OnSelect() — the player rested/healed at a campfire.
    private static void RestPrefix()
    {
        try { Add("rest", null, null); } catch { }
    }

    // EventOption.Chosen() — the player picked an event option, including the leave/proceed
    // option, so "left the event" shows in the play-by-play too. Value is the resolved option
    // label they saw, falling back to the stable TextKey.
    private static void EventChoicePrefix(object __instance)
    {
        try
        {
            var label = Reflect.CallString(Reflect.GetMember(__instance, "Title"), "GetFormattedText");
            if (string.IsNullOrWhiteSpace(label)) label = Reflect.GetString(__instance, "TextKey");
            if (!string.IsNullOrWhiteSpace(label)) Add("choice", label, null);
        }
        catch { }
    }

    // --- plumbing ---------------------------------------------------------------------

    private static int Patch(Harmony harmony, Type hook, string method, string prefixName)
    {
        try
        {
            var target = hook.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (target == null)
            {
                MainFile.Logger.Info($"run-events: Hook.{method} not found");
                return 0;
            }
            var prefix = typeof(RunEvents).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            return 1;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"run-events: patching {method} failed: {e.Message}");
            return 0;
        }
    }

    // Patch a static OR instance method on any type (not just Hook), disambiguating overloads
    // by parameter count and (optionally) the first parameter's type name. Needed because
    // GetMethod throws on overloads (RelicCmd.Obtain, CardCmd.Upgrade) and because some targets
    // are instance methods (HealRestSiteOption.OnSelect, EventOption.Chosen). Skips generics.
    private static int PatchOn(Harmony harmony, Type? target, string method, string prefixName,
                               int paramCount, string? firstParamType = null)
    {
        if (target == null)
        {
            MainFile.Logger.Info($"run-events: type for {method} not found");
            return 0;
        }
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance;
            var m = target.GetMethods(flags).FirstOrDefault(
                x => x.Name == method && !x.IsGenericMethodDefinition
                     && x.GetParameters().Length == paramCount
                     && (firstParamType == null
                         || (paramCount > 0 && x.GetParameters()[0].ParameterType.Name == firstParamType)));
            if (m == null)
            {
                MainFile.Logger.Info($"run-events: {method}({paramCount} args) not found");
                return 0;
            }
            var prefix = typeof(RunEvents).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(m, prefix: new HarmonyMethod(prefix));
            return 1;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"run-events: patching {method} failed: {e.Message}");
            return 0;
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.GetType(fullName) is { } t) return t;
            }
            catch { /* dynamic assemblies can throw */ }
        }
        return null;
    }
}
