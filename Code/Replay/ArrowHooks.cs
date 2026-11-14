using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using SpireCodex.Core;

namespace SpireCodex.Replay;

// One `arrow` row per event on The Fool's arrow slots (v0.113+), so a replay can check the
// simulator's arrow rules: which slot a shot leaves from, which enemy holds which slots in what
// order, and which slot a dislodge or a death gives back. See ArrowSlots for the rules and the
// slot fields every row carries.
//
//   ev=start       BeforeCombatStart, when the local player has any slot
//   ev=gain        PlayerCombatState.ArrowsChanged: a slot was appended (Quiver, a Shoot card)
//   ev=shot        Hook.AfterArrowShot (ArrowCmd.cs:119); the slot is already Away, `card` is the
//                  card that shot it
//   ev=empty_shot  Hook.AfterArrowShot with no slot: a shot was attempted with none Retrieved
//   ev=impale      Hook.AfterArrowImpaled (ArrowCmd.cs:154/194); the slot is already the last
//                  entry of `impaled`. Written for EVERY arrow, including a second arrow into an
//                  enemy that already holds one (that one applies no power, so had no row).
//                  `tgt_dead` is the target's IsDead after the hit: true sends the arrow straight
//                  back out (ArrowCmd.cs:159-161/199-201), and the ImpaledPower then never landed
//                  on the board, so it has no power row
//   ev=dislodge    Hook.AfterArrowDislodged (ArrowCmd.cs:225); the slot is already out of
//                  `impaled` and still Away; its retrieve row follows. A slot dislodged with
//                  DestroyAfterImpaled skips this hook and writes only a destroy row (ArrowCmd.cs:220)
//   ev=retrieve    ArrowSlot.ArrowStatusUpdated to Retrieved (ArrowCmd.cs:40)
//   ev=destroy     ArrowSlot.ArrowStatusUpdated to Destroyed (ArrowCmd.cs:222, :238). The game can
//                  set Destroyed on a slot that is already Destroyed; each set is a row
//
// `tgt`/`tgt_cid` name the creature the slot was impaled into: the `target` ArrowCmd.ImpaleTarget
// was called with, read by a prefix on it. `pid` is that impale's ImpaledPower instance (the `pid`
// of its power rows, when it landed). Retrieve, destroy and dislodge rows carry the same three
// for the slot's last impale since it was last shot, as seen by this recorder's own impale row;
// they are omitted when the slot has not been impaled since its last shot.
//
// Only the local player's slots are recorded. A status change to Away writes no row of its own:
// the shot row fires right after it with nothing in between (ArrowCmd.cs:116-119).
internal static class ArrowHooks
{
    private sealed class Holder
    {
        public object? Creature;
        public object? Power;
    }

    // The enemy each slot was last impaled into, by reference; cleared when the slot is shot.
    private static readonly ConditionalWeakTable<object, Holder> Holders = new();

    // The creature ArrowCmd.ImpaleTarget was called with for each slot, by reference. The
    // ImpaledPower cannot say: when the shot itself kills the target, PowerCmd.Apply refuses a
    // creature that can no longer receive powers (PowerCmd.cs:63), so the arrow is added to a
    // power with no Owner (ArrowCmd.cs:147-153) and then dislodged at once (ArrowCmd.cs:159-161).
    private static readonly ConditionalWeakTable<object, object> Aimed = new();

    // Slots and combat states whose game events this recorder already listens to.
    private static readonly ConditionalWeakTable<object, object> Listening = new();

    public static void Apply(Harmony harmony, Type? hook, ref int n, ref int attempted)
    {
        var me = typeof(ArrowHooks);
        attempted++; n += HookPatcher.Patch(harmony, hook, "BeforeCombatStart", me, nameof(CombatStarting));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterArrowShot", me, nameof(Shot));
        var arrowCmd = HookPatcher.FindType("MegaCrit.Sts2.Core.Commands.ArrowCmd");
        attempted++; n += HookPatcher.PatchOn(harmony, arrowCmd, "ImpaleTarget", me, nameof(Impaling), 4,
                                              firstParamType: "Creature");
        attempted++; n += HookPatcher.PatchOn(harmony, arrowCmd, "ImpaleTarget", me, nameof(Impaling), 5,
                                              firstParamType: "Creature");
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterArrowImpaled", me, nameof(Impaled));
        attempted++; n += HookPatcher.Patch(harmony, hook, "AfterArrowDislodged", me, nameof(Dislodged));
    }

    private static void CombatStarting(object __1)
    {
        try
        {
            var players = new System.Collections.Generic.List<object>();
            if (Reflect.GetMember(__1, "Players") is System.Collections.IEnumerable seq)
                foreach (var p in seq)
                    if (p != null) players.Add(p);
            // Same choice as the draw_order row: the local player, or the only player.
            var me = players.Find(LocalPlayer.IsLocalPlayer) ?? (players.Count == 1 ? players[0] : null);
            var pcs = Reflect.GetMember(me, "PlayerCombatState");
            if (pcs == null) return;
            Listen(pcs);
            if (ArrowSlots.Slots(pcs) is not { Count: > 0 }) return;
            var line = ReplayRecorder.Line("arrow");
            if (line == null) return;
            ArrowSlots.Describe(line, "start", null, null, pcs);
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void Shot(object __1, object? __2, object? __4)
    {
        try
        {
            if (!Mine(__1)) return;
            Listen(Reflect.GetMember(__1, "PlayerCombatState"));
            if (__2 != null)
            {
                Holders.Remove(__2);
                Aimed.Remove(__2);
            }
            var line = ReplayRecorder.Line("arrow");
            if (line == null) return;
            ArrowSlots.Describe(line, __2 == null ? "empty_shot" : "shot", __2, null,
                                Reflect.GetMember(__1, "PlayerCombatState"));
            line.Set("card", Card(__4)).Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void Impaling(object __0, object __1)
    {
        try
        {
            if (__0 == null || __1 == null) return;
            Aimed.Remove(__1);
            Aimed.Add(__1, __0);
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void Impaled(object __1, object __2, object? __4)
    {
        try
        {
            if (!Mine(Reflect.GetMember(__2, "owner"))) return;
            var target = Aimed.TryGetValue(__2, out var aimed) ? aimed : Reflect.GetMember(__1, "Owner");
            Holders.Remove(__2);
            Holders.Add(__2, new Holder { Creature = target, Power = __1 });
            var line = ReplayRecorder.Line("arrow");
            if (line == null) return;
            ArrowSlots.Describe(line, "impale", __2, __1);
            Target(line, target, __1).Set("card", Card(__4));
            if (Reflect.GetMember(target, "IsDead") is bool dead) line.SetFlag("tgt_dead", dead);
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void Dislodged(object __1, object __2, object? __4)
    {
        try
        {
            if (!Mine(Reflect.GetMember(__2, "owner"))) return;
            var line = ReplayRecorder.Line("arrow");
            if (line == null) return;
            ArrowSlots.Describe(line, "dislodge", __2, __1);
            Holders.TryGetValue(__2, out var holder);
            Target(line, holder?.Creature ?? Reflect.GetMember(__1, "Owner"), __1).Set("card", Card(__4)).Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void StatusChanged(object slot)
    {
        try
        {
            var ev = ArrowSlots.StatusOf(slot) switch
            {
                "retrieved" => "retrieve",
                "destroyed" => "destroy",
                _ => null,
            };
            if (ev == null) return;
            var line = ReplayRecorder.Line("arrow");
            if (line == null) return;
            Holders.TryGetValue(slot, out var holder);
            ArrowSlots.Describe(line, ev, slot, holder?.Power);
            if (holder != null) Target(line, holder.Creature, holder.Power);
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    private static void SlotsChanged(object pcs)
    {
        try
        {
            ListenToSlots(pcs);
            var line = ReplayRecorder.Line("arrow");
            if (line == null) return;
            // GainArrow appends one slot and raises ArrowsChanged (PlayerCombatState.cs:170-174),
            // so the new slot is the last one.
            var slots = ArrowSlots.Slots(pcs);
            ArrowSlots.Describe(line, "gain", slots is { Count: > 0 } ? slots[^1] : null, null, pcs);
            line.Emit();
        }
        catch (Exception e) { ReplayRecorder.Fault(e); }
    }

    // Subscribe to the combat state's ArrowsChanged and every slot's ArrowStatusUpdated, once
    // each. Both are plain `Action` events the game raises itself, so a status change is seen
    // from every path that sets it, including paths no hook covers.
    private static void Listen(object? pcs)
    {
        if (pcs == null) return;
        if (!Listening.TryGetValue(pcs, out _))
        {
            var ev = pcs.GetType().GetEvent("ArrowsChanged", BindingFlags.Public | BindingFlags.Instance);
            if (ev != null && ev.EventHandlerType == typeof(Action))
            {
                ev.AddEventHandler(pcs, new Action(() => SlotsChanged(pcs)));
                Listening.Add(pcs, pcs);
            }
        }
        ListenToSlots(pcs);
    }

    private static void ListenToSlots(object pcs)
    {
        var slots = ArrowSlots.Slots(pcs);
        if (slots == null) return;
        foreach (var slot in slots)
        {
            if (Listening.TryGetValue(slot, out _)) continue;
            var ev = slot.GetType().GetEvent("ArrowStatusUpdated", BindingFlags.Public | BindingFlags.Instance);
            if (ev == null || ev.EventHandlerType != typeof(Action)) continue;
            var s = slot;
            ev.AddEventHandler(slot, new Action(() => StatusChanged(s)));
            Listening.Add(slot, slot);
        }
    }

    private static bool Mine(object? player)
        => player != null && (!LocalPlayer.IsCoop || LocalPlayer.IsLocalPlayer(player));

    private static ReplayLine Target(ReplayLine line, object? creature, object? power)
    {
        line.Set("tgt", Ids.Bare(Reflect.GetString(creature, "ModelId")))
            .Set("tgt_cid", CreatureSlots.Maybe(creature));
        if (power != null) line.Set("pid", PowerInstances.Of(power));
        return line;
    }

    private static string? Card(object? card)
        => card == null ? null : Ids.Bare(Reflect.GetString(card, "Id"));
}
