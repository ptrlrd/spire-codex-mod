using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using SpireCodex.Producer;

namespace SpireCodex.Core;

// Per-hit damage tracking off the game's first-party damage hooks
// (MegaCrit.Sts2.Core.Hooks.Hook.AfterDamageGiven / AfterDamageReceived). Accumulates
// damage dealt (to enemies) and taken (by the player) at three scopes: this turn, this
// combat, and this run, plus per-card attribution for the run. The live snapshot reads the
// combat-scoped numbers (FillCombat); the run-scoped summary rides along on the run upload
// (AttachTo). Capture only, never throws into the game: a renamed hook just stops producing
// numbers, exactly like RunEvents.
//
// Threading: the damage hooks and the producer's FillCombat read both run on the game's main
// thread, but the run upload reads the summary from a threadpool thread, so every read/write
// is guarded by Gate.
public static class DamageTracker
{
    private static readonly object Gate = new();

    // This combat (reset on BeforeCombatStart; the live snapshot reads these).
    private static int _combatDealt;   // unblocked HP damage to enemies
    private static int _combatTaken;   // unblocked HP damage to the player
    private static int _combatBiggest; // biggest single unblocked hit we dealt
    private static int _turnDealt;     // unblocked dealt in the current round
    private static int _turn;          // current round number (drives the per-turn reset)
    private static int _combatMaxTurn; // highest round seen this combat (for the run turn count)

    // This run (reset when the seed changes; the upload reads these).
    private static string? _runSeed;
    private static int _runDealt;
    private static int _runTaken;
    private static int _runBiggest;
    private static int _runCombats;
    private static int _runTurns;
    private static readonly Dictionary<string, int> _runByCard = new();

    public static void Apply(Harmony harmony)
    {
        var hook = FindType("MegaCrit.Sts2.Core.Hooks.Hook");
        if (hook == null)
        {
            MainFile.Logger.Info("damage-tracker: Hook type not found; tracking disabled");
            return;
        }

        var patched = 0;
        patched += Patch(harmony, hook, "AfterDamageGiven", nameof(DamageGivenPrefix));
        patched += Patch(harmony, hook, "AfterDamageReceived", nameof(DamageReceivedPrefix));
        patched += Patch(harmony, hook, "BeforeCombatStart", nameof(CombatStartPrefix));
        patched += Patch(harmony, hook, "AfterCombatEnd", nameof(CombatEndPrefix));
        MainFile.Logger.Info($"damage-tracker: {patched}/4 hooks patched");
    }

    // --- snapshot + upload readers ---------------------------------------------------

    // Copy the combat-scoped numbers into the live snapshot's combat block (read each tick
    // by Sts2Access.ReadCombat while a fight is live).
    public static void FillCombat(CombatSnapshot combat)
    {
        lock (Gate)
        {
            combat.DamageDealt = _combatDealt;
            combat.DamageDealtThisTurn = _turnDealt;
            combat.DamageTaken = _combatTaken;
            combat.BiggestHit = _combatBiggest;
        }
    }

    // A one-line run damage summary for the post-run card (total dealt, avg per turn, biggest
    // hit). Null when nothing was tracked, so the card just omits the line.
    public static string? RunCardLine()
    {
        lock (Gate)
        {
            if (_runCombats == 0 && _runDealt == 0) return null;
            var perTurn = _runTurns > 0 ? _runDealt / _runTurns : _runDealt;
            return $"Dealt {_runDealt:N0} · {perTurn:N0}/turn · biggest hit {_runBiggest:N0}";
        }
    }

    // Run lifecycle: the producer reports the current seed each tick. A new seed means a new
    // run, so the run aggregate resets. Keeps the tracker run-scoped without reaching into
    // game types for a "run start" signal.
    public static void NoteRun(string? seed)
    {
        if (string.IsNullOrEmpty(seed)) return;
        lock (Gate)
        {
            if (seed == _runSeed) return;
            _runSeed = seed;
            _runDealt = _runTaken = _runBiggest = _runCombats = _runTurns = 0;
            _runByCard.Clear();
        }
    }

    // Attach the run-scoped damage summary to the outgoing run JSON as `_spirecodex_damage`
    // (the game's parser ignores the extra key; the backend reads it). Returns the JSON
    // unchanged when there is nothing to attach, so backfilled old runs ride through clean.
    public static string AttachTo(string runJson)
    {
        try
        {
            JsonObject damage;
            lock (Gate)
            {
                if (_runCombats == 0 && _runDealt == 0) return runJson;
                var byCard = new JsonObject();
                foreach (var (id, amt) in _runByCard) byCard[id] = amt;
                damage = new JsonObject
                {
                    ["damage_dealt"] = _runDealt,
                    ["damage_taken"] = _runTaken,
                    ["biggest_hit"] = _runBiggest,
                    ["combats"] = _runCombats,
                    ["turns"] = _runTurns,
                    ["by_card"] = byCard,
                };
            }

            if (JsonNode.Parse(runJson) is not JsonObject root) return runJson;
            root["_spirecodex_damage"] = damage;
            return root.ToJsonString();
        }
        catch
        {
            return runJson; // never block an upload over the extra field
        }
    }

    // --- capture ---------------------------------------------------------------------

    // Hook.AfterDamageGiven(PlayerChoiceContext, ICombatState, Creature? dealer,
    //   DamageResult results, ValueProp props, Creature target, CardModel? cardSource).
    // Damage whose TARGET is an enemy counts as "dealt", so poison/relics/powers all count,
    // not just card hits; cardSource attributes it to a card when one caused it.
    private static void DamageGivenPrefix(object __1, object __3, object __5, object __6)
    {
        try
        {
            if (Reflect.GetMember(__5, "IsPlayer") is true) return; // taken, counted elsewhere
            var unblocked = Reflect.GetInt(__3, "UnblockedDamage");
            if (unblocked <= 0) return;

            var turn = Reflect.GetInt(__1, "RoundNumber");
            var card = Ids.Bare(Reflect.GetString(__6, "Id")) ?? "other";

            lock (Gate)
            {
                if (turn != _turn) { _turn = turn; _turnDealt = 0; }
                if (turn > _combatMaxTurn) _combatMaxTurn = turn;

                _turnDealt += unblocked;
                _combatDealt += unblocked;
                _runDealt += unblocked;
                if (unblocked > _combatBiggest) _combatBiggest = unblocked;
                if (unblocked > _runBiggest) _runBiggest = unblocked;
                _runByCard.TryGetValue(card, out var prev);
                _runByCard[card] = prev + unblocked;
            }
        }
        catch { /* capture must never break the game */ }
    }

    // Hook.AfterDamageReceived(PlayerChoiceContext, IRunState, ICombatState?, Creature target,
    //   DamageResult result, ValueProp props, Creature? dealer, CardModel? cardSource).
    // Count only what the PLAYER takes (HP lost after block).
    private static void DamageReceivedPrefix(object __3, object __4)
    {
        try
        {
            if (Reflect.GetMember(__3, "IsPlayer") is not true) return; // player only
            var unblocked = Reflect.GetInt(__4, "UnblockedDamage");
            if (unblocked <= 0) return;
            lock (Gate)
            {
                _combatTaken += unblocked;
                _runTaken += unblocked;
            }
        }
        catch { }
    }

    // Hook.BeforeCombatStart(IRunState, CombatState?) — reset the per-combat counters.
    private static void CombatStartPrefix()
    {
        lock (Gate)
        {
            _combatDealt = _combatTaken = _combatBiggest = _turnDealt = _turn = _combatMaxTurn = 0;
        }
    }

    // Hook.AfterCombatEnd(IRunState, CombatState?, CombatRoom) — fold this combat into the
    // run totals (combats + turns, the basis for the site's avg-damage-per-turn).
    private static void CombatEndPrefix()
    {
        lock (Gate)
        {
            _runCombats++;
            _runTurns += Math.Max(_combatMaxTurn, 1);
        }
    }

    // --- plumbing (mirrors RunEvents) ------------------------------------------------

    private static int Patch(Harmony harmony, Type hook, string method, string prefixName)
    {
        try
        {
            var target = hook.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (target == null) { MainFile.Logger.Info($"damage-tracker: Hook.{method} not found"); return 0; }
            var prefix = typeof(DamageTracker).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            return 1;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"damage-tracker: patching {method} failed: {e.Message}");
            return 0;
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { if (asm.GetType(fullName) is { } t) return t; }
            catch { /* dynamic assemblies can throw */ }
        }
        return null;
    }
}
