using System.Collections;
using System.Collections.Generic;
using SpireCodex.Core;

namespace SpireCodex.Replay;

// The profile's unlock state as the run holds it, as an `unlock_state` object on the header
// and resume rows. Same three keys, same spellings, as `players[i].unlock_state` in the run save.
//
// Every pool roll reads it: Player.UnlockState gates card rewards, shops, potions, relics and
// mid-fight generated cards (CardFactory.cs:126, PotionFactory.cs:66), and the run-level union
// built from it (RunState.cs:513) sets the ancients, the shared relic bag and the boss
// discovery order (RunManager.cs:434,620,634; ActModel.cs:336 reads encounters_seen).
// number_of_runs gates the first-run treasure and reward rules (TreasureRoomRelicSynchronizer.cs:327,
// RewardsSet.cs:223). A profile that is still unlocking draws from smaller pools, so a replay
// without this can only be simulated by assuming a full unlock.
//
// It is fixed for the run: UnlockState's sets are readonly with no mutator (UnlockState.cs:19-21),
// built once when the Player is created and restored from the save on load (Player.cs:233).
//
// The values come from the game's own UnlockState.ToSerializable() (UnlockState.cs:124), which
// is what the run save writes, so nothing here can drift from the save. A key that cannot be
// read is OMITTED, never defaulted: an empty epoch list is a real, common value (a fresh profile)
// and must stay distinguishable from "not read".
internal static class UnlockStateRow
{
    // `player` is the live local Player (Sts2Access.LivePlayer). Taking it as an argument keeps
    // this file free of game types, so the tests compile it and run the same chain.
    public static ReplayLine? Read(object? player)
    {
        try
        {
            return From(Reflect.Call(Reflect.GetMember(player, "UnlockState"), "ToSerializable"));
        }
        catch { return null; }
    }

    // Reads a SerializableUnlockState (or anything shaped like it). Null when nothing resolves,
    // so the caller's Set() drops the key rather than writing an empty object.
    public static ReplayLine? From(object? serializable)
    {
        if (serializable == null) return null;
        var line = new ReplayLine("unlock_state", 3);
        line.Set("unlocked_epochs", Strings(Reflect.GetMember(serializable, "UnlockedEpochs")));
        line.Set("encounters_seen", Strings(Reflect.GetMember(serializable, "EncountersSeen")));
        if (Reflect.GetMember(serializable, "NumberOfRuns") is int runs) line.Set("number_of_runs", runs);
        return line.Fields.Count > 0 ? line : null;
    }

    // The list exactly as handed over, in the game's order. ModelId.ToString() is
    // "CATEGORY.ENTRY", the same form the save's JSON writes. One unreadable entry voids the
    // whole list: a partial list would read as a smaller unlock set, which is a wrong value.
    private static List<string>? Strings(object? items)
    {
        if (items is not IEnumerable enumerable || items is string) return null;
        var list = new List<string>();
        foreach (var item in enumerable)
        {
            var s = item?.ToString();
            if (string.IsNullOrEmpty(s)) return null;
            list.Add(s!);
        }
        return list;
    }
}
