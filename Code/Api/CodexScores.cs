using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace SpireCodex.Api;

// A card/relic/potion entry from /api/runs/scores/{type}: the Codex Score (0-100ish), the
// community win rate (percent), (when fetched with ?character=) which slice the numbers came
// from ("character" | "global"), and the Codex Elo (cards only; null elsewhere).
public sealed record EntityScore(
    double Score, double WinRate, int Picks, string? Scope = null, double? Elo = null);

// In-memory cache of community scores. Two layers:
//  - global sets, fetched once at startup (EnsureLoaded);
//  - a character-scoped set for the current run's character (EnsureCharacter, called from the
//    producer tick), so plates/best-pick rank by "win rate AS this character" when the sample
//    is big enough. Server falls back per-entry to global, so the char set is complete.
// Lookups return char-scoped entries while a run's character is known, else global.
public static class CodexScores
{
    private static Dictionary<string, EntityScore> _cards = new();
    private static Dictionary<string, EntityScore> _relics = new();
    private static Dictionary<string, EntityScore> _potions = new();
    private static bool _loading;

    // Character layer: current character id + its score sets, memoized per character so
    // re-runs with the same character don't refetch.
    private static string? _charId;
    private static Dictionary<string, EntityScore>? _charCards;
    private static Dictionary<string, EntityScore>? _charRelics;
    private static Dictionary<string, EntityScore>? _charPotions;
    private static string? _charLoading;
    private static readonly Dictionary<string, (Dictionary<string, EntityScore> Cards, Dictionary<string, EntityScore> Relics, Dictionary<string, EntityScore> Potions)> _byChar = new();

    public static bool Loaded { get; private set; }

    public static EntityScore? Card(string id) =>
        (_charId != null ? _charCards?.GetValueOrDefault(id) : null) ?? _cards.GetValueOrDefault(id);

    public static EntityScore? Relic(string id) =>
        (_charId != null ? _charRelics?.GetValueOrDefault(id) : null) ?? _relics.GetValueOrDefault(id);

    public static EntityScore? Potion(string id) =>
        (_charId != null ? _charPotions?.GetValueOrDefault(id) : null) ?? _potions.GetValueOrDefault(id);

    // Sorted Codex Elo values across all rated cards, for percentile-based Elo tiers.
    // Elo is global (the character slice doesn't change it), so one distribution suffices.
    private static double[] _eloSorted = Array.Empty<double>();

    // Elo percentile -> tier letter (S = top 5%, A = 20%, B = 40%, C = 65%, D = 85%, F = rest).
    // Null when the entity has no Elo or the distribution isn't loaded; callers fall back to
    // the Score tier.
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
        _ = LoadAsync();
    }

    // Called every producer tick with the live character (null outside a run). Cheap no-op
    // unless the character actually changed; fetches the scoped sets in the background and
    // swaps them in when ready (lookups keep serving global until then).
    public static void EnsureCharacter(string? charId)
    {
        if (charId == _charId) return;
        if (charId == null)
        {
            _charId = null; _charCards = null; _charRelics = null; _charPotions = null;
            return;
        }
        if (_byChar.TryGetValue(charId, out var cached))
        {
            _charId = charId;
            _charCards = cached.Cards; _charRelics = cached.Relics; _charPotions = cached.Potions;
            return;
        }
        if (_charLoading == charId) return;
        _charLoading = charId;
        Diag($"fetching character scores: {charId}");
        _ = LoadCharacterAsync(charId);
    }

    private static async Task LoadCharacterAsync(string charId)
    {
        try
        {
            var client = new SpireCodexClient();
            var cards = await client.GetScoresAsync("cards", charId).ConfigureAwait(false);
            var relics = await client.GetScoresAsync("relics", charId).ConfigureAwait(false);
            var potions = await client.GetScoresAsync("potions", charId).ConfigureAwait(false);
            _byChar[charId] = (cards, relics, potions);
            _charId = charId; _charCards = cards; _charRelics = relics; _charPotions = potions;
            Diag($"character scores loaded: {charId} ({cards.Count} cards, {relics.Count} relics, {potions.Count} potions)");
        }
        catch (Exception e)
        {
            Diag($"character scores FAILED ({charId}): {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            if (_charLoading == charId) _charLoading = null;
        }
    }

    private static async Task LoadAsync()
    {
        // Retry with backoff: a reachable API can still serve an EMPTY score set while the
        // server's stats snapshot is rebuilding (seen in prod), and caching that for the
        // whole session would leave every plate/tip blank until relaunch.
        var delays = new[] { 0, 30, 60, 120, 300, 600 };
        try
        {
            for (var attempt = 0; attempt < delays.Length; attempt++)
            {
                if (delays[attempt] > 0)
                    await Task.Delay(TimeSpan.FromSeconds(delays[attempt])).ConfigureAwait(false);
                try
                {
                    Diag($"LoadAsync attempt {attempt + 1}");
                    var client = new SpireCodexClient();
                    var cards = await client.GetScoresAsync("cards").ConfigureAwait(false);
                    var relics = await client.GetScoresAsync("relics").ConfigureAwait(false);
                    var potions = await client.GetScoresAsync("potions").ConfigureAwait(false);
                    if (cards.Count == 0 && relics.Count == 0)
                    {
                        Diag("server returned empty score sets (stats snapshot cold?); will retry");
                        continue;
                    }
                    _cards = cards;
                    _relics = relics;
                    _potions = potions;
                    _eloSorted = cards.Values
                        .Where(e => e.Elo != null).Select(e => e.Elo!.Value)
                        .OrderBy(x => x).ToArray();
                    Loaded = true;
                    Diag($"loaded OK: {cards.Count} cards, {relics.Count} relics, {potions.Count} potions");
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
