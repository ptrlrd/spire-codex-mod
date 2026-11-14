using System;
using System.Collections;
using SpireCodex.Core;

namespace SpireCodex.Replay;

// Whether a CardModel belongs to the run being recorded.
//
// The card-level hooks (CardCmd.Transform's callbacks, CardCmd.Upgrade, Downgrade, Enchant,
// Afflict) fire for ANY CardModel, including clones another mod builds to simulate a fight.
// A solver mod (CombatSolver) clones the fight and runs real CardCmd.Transform on the clones;
// with no filter its 2,204-row burst of phantom MINION_STRIKE transforms landed in the journal
// ahead of the real one and desynced the reader (STS-145).
//
// Membership is read from game source (v0.113 decompile), not inferred:
//  - CardModel._owner is the Player the card belongs to (CardModel.cs:351, set once; the setter
//    throws on a second owner).
//  - The live run is RunManager.Instance.DebugOnlyGetState() (RunManager.cs:1686), and its
//    players are RunState.Players (RunState.cs:310). A clone's owner is a different Player
//    object, or the live one; either way identity against that list decides.
//  - CardModel.Pile is the owner's pile that contains the card (CardModel.cs:369), drawn from
//    Player.Piles = PlayerCombatState.AllPiles + Deck (Player.cs:154).
//  - A combat pile is live only when the owner's creature is in the live fight:
//    Player.Creature.CombatState (Creature.cs:119) is the object CombatManager.Instance
//    holds (CombatManager.DebugOnlyGetState, CombatManager.cs:179).
//
// Unknown is not Foreign: a card with no owner or no pile yet (an upgraded reward card being
// built) is neither proven live nor proven foreign.
internal static class LiveCards
{
    internal enum Verdict { Unknown, Live, Foreign }

    // Bound by ReplayHooks.Apply to the game singletons; tests bind fakes.
    private static Func<object?>? _liveRun;
    private static Func<object?>? _liveCombat;

    public static void Bind(Func<object?>? liveRun, Func<object?>? liveCombat)
    {
        _liveRun = liveRun;
        _liveCombat = liveCombat;
    }

    public static Verdict Of(object? card)
    {
        if (card == null) return Verdict.Unknown;
        try
        {
            var run = _liveRun?.Invoke();
            if (run == null) return Verdict.Unknown;
            var owner = Reflect.GetMember(card, "_owner");
            if (owner == null) return Verdict.Unknown;
            if (Reflect.GetMember(run, "Players") is not IEnumerable players) return Verdict.Unknown;
            var ownerInRun = false;
            foreach (var p in players)
                if (ReferenceEquals(p, owner)) { ownerInRun = true; break; }
            if (!ownerInRun) return Verdict.Foreign;

            var pile = Reflect.GetMember(card, "Pile");
            if (pile == null) return Verdict.Unknown;
            if (Reflect.GetMember(pile, "IsCombatPile") is not bool combatPile) return Verdict.Unknown;
            if (!combatPile) return Verdict.Live;

            var fight = _liveCombat?.Invoke();
            var ownerFight = Reflect.GetMember(Reflect.GetMember(owner, "Creature"), "CombatState");
            if (fight == null || ownerFight == null) return Verdict.Unknown;
            return ReferenceEquals(fight, ownerFight) ? Verdict.Live : Verdict.Foreign;
        }
        catch
        {
            return Verdict.Unknown;
        }
    }

    // A transform row is written only for a card proven live. CardCmd.Transform puts the
    // replacement into the original's pile, with the original's owner, before either
    // transform callback runs (CardCmd.cs:337-360), so a live transform always proves live.
    public static bool RecordTransform(object? replacement) => Of(replacement) == Verdict.Live;

    // Upgrade, downgrade, enchant and afflict can legitimately touch a card that is in no pile
    // yet, so those rows are dropped only when the card is proven to be someone else's.
    public static bool Record(object? card) => Of(card) != Verdict.Foreign;
}
