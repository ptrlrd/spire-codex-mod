using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// A card/relic/potion entry from /api/runs/scores/{type}: the Codex Score (0-100ish), the
// community win rate (percent), (when fetched with ?character=) which slice the numbers came
// from ("character" | "global"), and the Codex Elo (cards only; null elsewhere).
public sealed record EntityScore(
    double Score, double WinRate, int Picks, string? Scope = null, double? Elo = null);

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
        Dictionary<string, EntityScore> Potions)
    {
        public static readonly Sets Empty = new(new(), new(), new());
    }

    private static Sets _global = Sets.Empty; // (no character, all runs): startup baseline + fallback
    private static Sets _active = Sets.Empty; // the current (character, filter) set lookups serve
    private static double[] _eloSorted = Array.Empty<double>();

    private static string? _charId;                        // current run character (null outside a run)
    private static string _filter = StatFilter.DefaultKey; // active stat-filter key
    private static readonly Dictionary<string, Sets> _cache = new(); // key: Key(char, filter)
    private static string? _loadingKey;
    private static bool _loading;

    public static bool Loaded { get; private set; }

    // The active stat filter (for the on-screen indicator).
    public static string CurrentFilter => _filter;
    public static string CurrentFilterLabel => StatFilter.ByKey(_filter).Label;

    private static string Key(string? charId, string filter) => $"{charId ?? "_"}|{filter}";

    public static EntityScore? Card(string id) =>
        _active.Cards.GetValueOrDefault(id) ?? _global.Cards.GetValueOrDefault(id);

    public static EntityScore? Relic(string id) =>
        _active.Relics.GetValueOrDefault(id) ?? _global.Relics.GetValueOrDefault(id);

    public static EntityScore? Potion(string id) =>
        _active.Potions.GetValueOrDefault(id) ?? _global.Potions.GetValueOrDefault(id);

    // Sorted Codex Elo values across all rated cards, for percentile-based Elo tiers. Elo is
    // global (neither the character slice nor the stat filter changes it), so the global
    // distribution suffices.
    public static string? EloTier(double? elo)
    {
        if (elo is not { } e || _eloSorted.Length < 20) return null;
        var idx = Array.BinarySearch(_eloSorted, e);
        if (idx < 0) idx = ~idx;
        var topShare = 1.0 - (double)idx / _eloSorted.Length;
        return topShare <= 0.05 ? "S"
            : topShare <= 0.20 ? "A"
            : topShare <= 0.40 ? "B"
            : topShare <= 0.65 ? "C"
            : topShare <= 0.85 ? "D" : "F";
    }

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
        if (_loadingKey == key) return;
        _loadingKey = key;
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
            if (_loadingKey == key) _loadingKey = null;
        }
    }

    private static async Task<Sets> FetchAsync(string? charId, string filter)
    {
        var client = new SpireCodexClient();
        var cards = await client.GetScoresAsync("cards", charId, filter).ConfigureAwait(false);
        var relics = await client.GetScoresAsync("relics", charId, filter).ConfigureAwait(false);
        var potions = await client.GetScoresAsync("potions", charId, filter).ConfigureAwait(false);
        return new Sets(cards, relics, potions);
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
                    _eloSorted = sets.Cards.Values
                        .Where(e => e.Elo != null).Select(e => e.Elo!.Value)
                        .OrderBy(x => x).ToArray();
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
