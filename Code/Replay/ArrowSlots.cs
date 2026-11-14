using System;
using System.Collections;
using System.Collections.Generic;
using SpireCodex.Core;

namespace SpireCodex.Replay;

// Reads The Fool's arrow slots for the `arrow` rows (see ArrowHooks).
//
// Everything here is a READ of game state, never a rule. The game's own rules, transcribed from
// the v0.113 decompile so a consumer knows what these rows let it check:
//
//   - a slot is a `MegaCrit.Sts2.Core.Entities.FoolArrows.ArrowSlot` with a `Status` of
//     Retrieved, Away or Destroyed (ArrowStatus.cs); the player's slots are
//     `PlayerCombatState.Arrows`, in the order `GainArrow` appended them (PlayerCombatState.cs:170);
//   - a shot takes the LAST slot whose status is Retrieved (ArrowCmd.cs:60) and sets it Away
//     (ArrowCmd.cs:116);
//   - an impale appends the slot to the enemy's `ImpaledPower.Arrows` (ImpaledPower.cs:63-67,
//     ArrowCmd.cs:153/193), one ImpaledPower per applier, so a second arrow into an enemy that
//     already has one adds to the list and applies no power -- which is why it wrote no row before;
//   - a dislodge removes the FIRST slot in that list (ImpaledPower.cs:69-79, ArrowCmd.cs:219),
//     then the slot is either Destroyed (DestroyAfterImpaled, ArrowCmd.cs:220-223) or Retrieved
//     (ArrowCmd.cs:226) after the AfterArrowDislodged hooks run;
//   - an enemy's death dislodges its arrows first to last (ImpaledPower.cs:111-126).
//
// A slot is named by its index in `PlayerCombatState.Arrows`, matched by REFERENCE. Every read
// that fails returns null and the field is omitted: a slot index or status that is guessed would
// let a wrong simulator rule pass, which is worse than having no row to check it against.
internal static class ArrowSlots
{
    // The owning player's slot list, as the game holds it. `ArrowSlot.owner` is a public
    // readonly field; `Arrows` returns a copy, so one read per row is one consistent snapshot.
    public static List<object>? SlotsOf(object? slot)
        => Slots(Reflect.GetMember(Reflect.GetMember(slot, "owner"), "PlayerCombatState"));

    public static List<object>? Slots(object? playerCombatState)
    {
        if (Reflect.GetMember(playerCombatState, "Arrows") is not IEnumerable seq) return null;
        var list = new List<object>();
        foreach (var item in seq)
        {
            if (item == null) return null;
            list.Add(item);
        }
        return list;
    }

    // Reference identity, never Equals: two slots in the same state are still two slots.
    public static int? IndexIn(List<object>? slots, object? slot)
    {
        if (slots == null || slot == null) return null;
        for (var i = 0; i < slots.Count; i++)
            if (ReferenceEquals(slots[i], slot)) return i;
        return null;
    }

    // "retrieved" | "away" | "destroyed", from the game's enum name.
    public static string? StatusOf(object? slot)
    {
        var status = Reflect.GetMember(slot, "Status");
        if (status is not Enum) return null;
        return status.ToString()?.ToLowerInvariant();
    }

    // Every slot's status, in slot order. All or nothing.
    public static List<string>? Statuses(List<object>? slots)
    {
        if (slots == null) return null;
        var statuses = new List<string>(slots.Count);
        foreach (var slot in slots)
        {
            if (StatusOf(slot) is not { } s) return null;
            statuses.Add(s);
        }
        return statuses;
    }

    // The slot indices an ImpaledPower holds, first in to last in -- the order a dislodge takes
    // them out. All or nothing: one unplaceable arrow drops the whole list.
    public static List<int>? ImpaledOrder(object? impaledPower, List<object>? slots)
    {
        if (slots == null) return null;
        if (Reflect.GetMember(impaledPower, "Arrows") is not IEnumerable seq) return null;
        var order = new List<int>();
        foreach (var arrow in seq)
        {
            if (IndexIn(slots, arrow) is not int i) return null;
            order.Add(i);
        }
        return order;
    }

    // Fill the slot fields of one `arrow` row. `slot` may be null (a shot with no slot left);
    // `impaledPower` is the enemy's ImpaledPower the row concerns, if any.
    //
    //   slot          index of the slot this event is about
    //   status        that slot's status as of this row
    //   destroy_after that slot's DestroyAfterImpaled flag as of this row
    //   slots         every slot's status as of this row, in slot order
    //   impaled       the slot indices in `impaledPower`, first in to last in, as of this row
    public static ReplayLine Describe(ReplayLine line, string ev, object? slot, object? impaledPower,
                                      object? playerCombatState = null)
    {
        var slots = slot != null ? SlotsOf(slot) : Slots(playerCombatState);
        line.Set("ev", ev);
        if (slot != null)
        {
            line.Set("slot", IndexIn(slots, slot))
                .Set("status", StatusOf(slot));
            if (Reflect.GetMember(slot, "DestroyAfterImpaled") is bool destroy)
                line.SetFlag("destroy_after", destroy);
        }
        line.Set("slots", Statuses(slots));
        if (impaledPower != null) line.Set("impaled", ImpaledOrder(impaledPower, slots));
        return line;
    }
}
