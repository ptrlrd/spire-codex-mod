using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace SpireCodex.Core;

// One play-by-play moment for the live ticker: Kind is a short verb ("card", "potion",
// "combat", "victory", "buy", "death", "act", "event", "remove"), Value the bare entity
// id when one applies, Turn the combat round, At unix seconds.
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
            GD.Print("[SpireCodex] run-events: Hook type not found; ticker disabled");
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
        GD.Print($"[SpireCodex] run-events: {patched}/9 hooks patched");
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

    // Hook.BeforeCombatStart(IRunState, CombatState?)
    private static void CombatStartPrefix(object __1)
    {
        try { Add("combat", null, __1); } catch { }
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
            if (Reflect.GetMember(__2, "IsPlayer") is true) Add("death", null, __1);
        }
        catch { }
    }

    // Hook.AfterActEntered(IRunState)
    private static void ActPrefix()
    {
        try { Add("act", null, null); } catch { }
    }

    // --- plumbing ---------------------------------------------------------------------

    private static int Patch(Harmony harmony, Type hook, string method, string prefixName)
    {
        try
        {
            var target = hook.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (target == null)
            {
                GD.Print($"[SpireCodex] run-events: Hook.{method} not found");
                return 0;
            }
            var prefix = typeof(RunEvents).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            return 1;
        }
        catch (Exception e)
        {
            GD.Print($"[SpireCodex] run-events: patching {method} failed: {e.Message}");
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
