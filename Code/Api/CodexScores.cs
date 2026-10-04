using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SpireCodex.Api;

public sealed record EntityScore(
    double Score, double WinRate, int Picks, string? Scope = null, double? Elo = null);

public sealed record SkipScore(
    double Elo, long Screens, long Skipped, double SkipRate,
    long[]? ScreensByAct = null, long[]? SkippedByAct = null)
{
    public double RateForAct(int act)
    {
        if (ScreensByAct is not { } off || SkippedByAct is not { } pick) return SkipRate;
        var i = Math.Clamp(act - 1, 0, Math.Min(off.Length, pick.Length) - 1);
        if (i < 0 || off[i] <= 0) return SkipRate;
        return 100.0 * pick[i] / off[i];
    }
}

public sealed record ScoreSet(Dictionary<string, EntityScore> Scores, SkipScore? Skip = null);

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

    private static volatile Sets _global = Sets.Empty;
    private static volatile Sets _active = Sets.Empty;

    private static volatile string? _charId;
    private static volatile string _filter = StatFilter.DefaultKey;
    private static readonly ConcurrentDictionary<string, Sets> _cache = new();
    private static readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private static bool _loading;

    public static bool Loaded { get; private set; }

    public static string CurrentFilter => _filter;
    public static string CurrentFilterLabel => Loc.T(StatFilter.ByKey(_filter).LabelKey);

    private static string Key(string? charId, string filter) => $"{charId ?? "_"}|{filter}";

    public static EntityScore? Card(string id) =>
        _active.Cards.GetValueOrDefault(id) ?? _global.Cards.GetValueOrDefault(id);

    public static EntityScore? Relic(string id) =>
        _active.Relics.GetValueOrDefault(id) ?? _global.Relics.GetValueOrDefault(id);

    public static EntityScore? Potion(string id) =>
        _active.Potions.GetValueOrDefault(id) ?? _global.Potions.GetValueOrDefault(id);

    public static SkipScore? Skip => _active.Skip ?? _global.Skip;

    public static void EnsureLoaded()
    {
        if (Loaded || _loading) return;
        _loading = true;
        Diag("EnsureLoaded called");
        _ = LoadGlobalAsync();
    }

    public static void EnsureCharacter(string? charId)
    {
        if (charId == _charId) return;
        _charId = charId;
        Activate();
    }

    public static void SetFilter(string key)
    {
        if (key == _filter) return;
        _filter = key;
        Diag($"stat filter -> {_filter}");
        Activate();
    }

    private static string? EffectiveChar() => _filter == StatFilter.DefaultKey ? _charId : null;

    private static void Activate()
    {
        var charId = EffectiveChar();
        var key = Key(charId, _filter);
        if (_cache.TryGetValue(key, out var sets)) { _active = sets; return; }
        if (!_inFlight.TryAdd(key, 0)) return;
        _ = LoadSetAsync(charId, _filter, key);
    }

    private static async Task LoadSetAsync(string? charId, string filter, string key)
    {
        try
        {
            var sets = await FetchAsync(charId, filter).ConfigureAwait(false);
            _cache[key] = sets;
            if (Key(EffectiveChar(), _filter) == key) _active = sets;
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

    private static async Task<Sets> FetchAsync(string? charId, string filter)
    {
        var client = new SpireCodexClient();
        var cards = client.GetScoresAsync("cards", charId, filter, includeSkip: true);
        var relics = client.GetScoresAsync("relics", charId, filter);
        var potions = client.GetScoresAsync("potions", charId, filter);
        await Task.WhenAll(cards, relics, potions).ConfigureAwait(false);
        return new Sets(
            cards.Result.Scores, relics.Result.Scores, potions.Result.Scores, cards.Result.Skip);
    }

    private static async Task LoadGlobalAsync()
    {
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
                    Activate();
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

    internal static void DiagPublic(string msg) => Diag(msg);

    private static void Diag(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "spire-codex-scores.log"),
                $"{DateTimeOffset.UtcNow:o}  {msg}\n");
        }
        catch {  }
        MainFile.Logger.Info($"scores: {msg}");
    }
}
