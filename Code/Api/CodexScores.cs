using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// A card/relic/potion entry from /api/runs/scores/{type}: the Codex Score (0-100ish), the
// community win rate (percent), (when fetched with ?character=) which slice the numbers came
// from ("character" | "global"), and the Codex Elo (cards only; null elsewhere).
public sealed record EntityScore(
    double Score, double WinRate, int Picks, string? Scope = null, double? Elo = null);

// The reserved SKIP entrant from /api/runs/scores/cards?include_skip=1. Skipping competes
// for the same decision as the cards on screen, so it is fitted in the same Bradley-Terry
// pass and its Elo is directly comparable to a card's. It has no win rate or Codex Score
// (there is no "winning with" a skip), so its sample is screens seen / screens skipped and
// SkipRate is the community skip rate.
public sealed record SkipScore(
    double Elo, long Screens, long Skipped, double SkipRate,
    long[]? ScreensByAct = null, long[]? SkippedByAct = null)
{
    // Skipping gets much more attractive as the deck fills up: 20% in Act 1, 32% in Act 2,
    // 43% in Act 3+. A flat community average would understate the case for skipping late
    // and overstate it early, so the plate quotes the rate for the act you're actually in.
    // Falls back to the overall rate when the per-act arrays aren't served.
    public double RateForAct(int act)
    {
        if (ScreensByAct is not { } off || SkippedByAct is not { } pick) return SkipRate;
        var i = Math.Clamp(act - 1, 0, Math.Min(off.Length, pick.Length) - 1);
        if (i < 0 || off[i] <= 0) return SkipRate;
        return 100.0 * pick[i] / off[i];
    }
}

// One /scores/{type} response: the entity table, plus the SKIP entrant when it was asked
// for and the server had it. Skip is null on an older backend, or before the entity store
// rebuilds after a deploy, and every consumer treats that as "no skip advice".
public sealed record ScoreSet(Dictionary<string, EntityScore> Scores, SkipScore? Skip = null);

// In-memory cache of community scores across two dimensions:
//  - character: the current run's character (numbers AS this character when the sample is big
//    enough; the server falls back per-entry to global so the set stays complete);
//  - stat filter: which slice of the run population the numbers come from (all runs / Ascension
//    10 / higher-win-rate brackets), chosen via the Stats setting and applied by SetFilter
//    (polled each producer tick). See StatFilter / SpireCodexConfig.StatBracket.
// Both ride the score fetch as query params and key the cache. Lookups serve the active
// (character, filter) set and fall back to the all-runs global baseline for any missing entry.
public static class CodexScores
{
    private sealed record Sets(
        Dictionary<string, EntityScore> Cards,
        Dictionary<string, EntityScore> Relics,
        Dictionary<string, EntityScore> Potions,
        SkipScore? Skip = null)
    {
        public static readonly Sets Empty = new(new(), new(), new());
    }

    // volatile: written by background fetch continuations, read every tick on the Godot thread.
    // Without it the game thread can keep reading a stale set after a bracket swap.
    private static volatile Sets _global = Sets.Empty; // (no character, all runs): startup baseline + fallback
    private static volatile Sets _active = Sets.Empty; // the current (character, filter) set lookups serve

    private static volatile string? _charId;                        // current run character (null outside a run)
    private static volatile string _filter = StatFilter.DefaultKey; // active stat-filter key
    private static readonly ConcurrentDictionary<string, Sets> _cache = new(); // key: Key(char, filter)
    // Keys with a fetch in flight. A SET, not a single slot: there are five brackets and the
    // player can switch away and back before one lands. A single slot only remembered the most
    // recent key, so returning to an earlier bracket re-issued its fetch (seen in the wild:
    // three concurrent loads of the same key, which then blew through the 60/min scores limit
    // and left a switch taking 30s+ instead of 0.3s).
    private static readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private static bool _loading;

    public static bool Loaded { get; private set; }

    // The active stat filter (for the on-screen indicator).
    public static string CurrentFilter => _filter;
    public static string CurrentFilterLabel => Loc.T(StatFilter.ByKey(_filter).LabelKey);

    private static string Key(string? charId, string filter) => $"{charId ?? "_"}|{filter}";

    public static EntityScore? Card(string id) =>
        _active.Cards.GetValueOrDefault(id) ?? _global.Cards.GetValueOrDefault(id);

    public static EntityScore? Relic(string id) =>
        _active.Relics.GetValueOrDefault(id) ?? _global.Relics.GetValueOrDefault(id);

    public static EntityScore? Potion(string id) =>
        _active.Potions.GetValueOrDefault(id) ?? _global.Potions.GetValueOrDefault(id);

    // The community's skip rating for card rewards, or null when the server hasn't got one
    // yet. The rating is all-runs regardless of bracket (same policy as card Elo), so the
    // global set is a fine fallback for a bracket that predates it.
    public static SkipScore? Skip => _active.Skip ?? _global.Skip;

    public static void EnsureLoaded()
    {
        if (Loaded || _loading) return;
        _loading = true;
        Diag("EnsureLoaded called");
        _ = LoadGlobalAsync();
    }

    // Called every producer tick with the live character (null outside a run). No-op unless it
    // changed; activates the (character, current filter) set.
    public static void EnsureCharacter(string? charId)
    {
        if (charId == _charId) return;
        _charId = charId;
        Activate();
    }

    // Set the active stat filter (driven by the Stats setting, polled each producer tick).
    // Re-activates scores for it; lookups keep serving the old set until the new one lands.
    public static void SetFilter(string key)
    {
        if (key == _filter) return;
        _filter = key;
        Diag($"stat filter -> {_filter}");
        Activate();
    }

    // A stat-filter bracket grades across ALL characters: the backend ignores ?character= when a
    // bracket is set, so brackets fetch + cache character-agnostically (once per bracket) and only
    // "all" is per-character. Keeps the cache honest and avoids refetching the same bracket for
    // every character.
    private static string? EffectiveChar() => _filter == StatFilter.DefaultKey ? _charId : null;

    // Make the (effective character, current filter) set the active one, fetching + caching it if
    // needed. A cached set swaps in instantly; an un-cached one loads in the background and
    // swaps in when ready, staying on the previous set meanwhile (so plates never blank).
    private static void Activate()
    {
        var charId = EffectiveChar();
        var key = Key(charId, _filter);
        if (_cache.TryGetValue(key, out var sets)) { _active = sets; return; }
        // TryAdd is the claim: exactly one caller starts the fetch for a key, however many
        // ticks or bracket flips ask for it while that fetch is running.
        if (!_inFlight.TryAdd(key, 0)) return;
        _ = LoadSetAsync(charId, _filter, key);
    }

    private static async Task LoadSetAsync(string? charId, string filter, string key)
    {
        try
        {
            var sets = await FetchAsync(charId, filter).ConfigureAwait(false);
            _cache[key] = sets;
            if (Key(EffectiveChar(), _filter) == key) _active = sets; // still what the player's looking at
            Diag($"set loaded [{key}]: {sets.Cards.Count} cards, {sets.Relics.Count} relics, {sets.Potions.Count} potions");
        }
        catch (Exception e)
        {
            Diag($"set load FAILED [{key}]: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    // The three entity types are independent, so fetch them together: one round trip's latency
    // per bracket instead of three chained ones.
    private static async Task<Sets> FetchAsync(string? charId, string filter)
    {
        var client = new SpireCodexClient();
        // Only the card fetch asks for SKIP; it's a card-reward decision and the other two
        // types have no skip to rate.
        var cards = client.GetScoresAsync("cards", charId, filter, includeSkip: true);
        var relics = client.GetScoresAsync("relics", charId, filter);
        var potions = client.GetScoresAsync("potions", charId, filter);
        await Task.WhenAll(cards, relics, potions).ConfigureAwait(false);
        return new Sets(
            cards.Result.Scores, relics.Result.Scores, potions.Result.Scores, cards.Result.Skip);
    }

    private static async Task LoadGlobalAsync()
    {
        // Retry with backoff: a reachable API can still serve an EMPTY score set while the
        // server's stats snapshot is rebuilding (seen in prod), and caching that for the whole
        // session would leave every plate/tip blank until relaunch.
        var delays = new[] { 0, 30, 60, 120, 300, 600 };
        try
        {
            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                if (delays[attempt] > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delays[attempt])).ConfigureAwait(false);
                try
                {
                    Diag($"LoadGlobal attempt {attempt + 1}");
                    var sets = await FetchAsync(null, StatFilter.DefaultKey).ConfigureAwait(false);
                    if (sets.Cards.Count == 0 && sets.Relics.Count == 0)
                    {
                        Diag("server returned empty score sets (stats snapshot cold?); will retry");
                        continue;
                    }
                    _global = sets;
                    _cache[Key(null, StatFilter.DefaultKey)] = sets;
                    Loaded = true;
                    Activate(); // point _active at the right (char, filter) now that we have data
                    Diag($"global loaded OK: {sets.Cards.Count} cards, {sets.Relics.Count} relics, {sets.Potions.Count} potions");
                    return;
                }
                catch (Exception e)
                {
                    Diag($"attempt {attempt + 1} FAILED: {e.GetType().Name}: {e.Message}");
                }
            }
            Diag("giving up on scores for this session");
        }
        finally
        {
            _loading = false;
        }
    }

    // Let other components write into the same log; it's the one diagnostic channel that
    // stays on in release builds.
    internal static void DiagPublic(string msg) => Diag(msg);

    // Godot drops GD.Print from background threads, so also write to a file we can read.
    private static void Diag(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "spire-codex-scores.log"),
                $"{DateTimeOffset.UtcNow:o}  {msg}\n");
        }
        catch { /* ignore */ }
        MainFile.Logger.Info($"scores: {msg}");
    }
}
