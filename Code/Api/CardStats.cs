using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpireCodex.Api;

public sealed record CharStat(string Character, double WinRate, int Picks);

// Full per-card stats for the hover tooltip (/api/runs/stats/cards/{id}).
public sealed record CardStats(
    string Id, double? Score, double WinRate, double PickRate,
    double BaselineWinRate, int Picks, IReadOnlyList<CharStat> ByCharacter);

public static class Ranks
{
    // Codex Score (0-100) -> tier letter. Cutoffs are easy to retune to the site's Tier List.
    public static string Tier(double score) =>
        score >= 90 ? "S" : score >= 75 ? "A" : score >= 60 ? "B"
        : score >= 45 ? "C" : score >= 30 ? "D" : "F";
}

// Lazily fetches and caches full per-entity stats. Get() returns null while loading; a fetch
// is kicked off on first request and the result cached (including failures, to avoid retries).
public sealed class StatsCache
{
    private readonly string _entityType;
    private readonly ConcurrentDictionary<string, CardStats?> _cache = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private static readonly SpireCodexClient Client = new();

    public StatsCache(string entityType) => _entityType = entityType;

    public CardStats? Get(string id)
    {
        if (_cache.TryGetValue(id, out var v)) return v;
        if (_inFlight.TryAdd(id, 0)) _ = Fetch(id);
        return null;
    }

    private async Task Fetch(string id)
    {
        try { _cache[id] = await Client.GetStatsAsync(_entityType, id).ConfigureAwait(false); }
        catch { _cache[id] = null; }
        finally { _inFlight.TryRemove(id, out _); }
    }
}

public static class CardStatsCache
{
    private static readonly StatsCache Cache = new("cards");
    public static CardStats? Get(string id) => Cache.Get(id);
}

public static class RelicStatsCache
{
    private static readonly StatsCache Cache = new("relics");
    public static CardStats? Get(string id) => Cache.Get(id);
}

public static class PotionStatsCache
{
    private static readonly StatsCache Cache = new("potions");
    public static CardStats? Get(string id) => Cache.Get(id);
}
