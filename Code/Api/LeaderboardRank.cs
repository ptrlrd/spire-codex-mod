using System.Threading.Tasks;

namespace SpireCodex.Api;

// Rank fields are null when the player has no winning run in that pool yet.
public sealed record RankInfo(int? SeedRank, int SeedTotal, int? GlobalRank, int GlobalTotal, double? Percentile);

// Fetches and caches the player's seed + global standing for the F9 panel. Keyed by
// steam id + seed; refetches when the seed changes. Returns null until loaded and stays
// null if the backend endpoint isn't live yet (graceful no-op until M1-B ships).
public static class LeaderboardRank
{
    private static readonly SpireCodexClient Client = new();
    private static string? _key;
    private static RankInfo? _current;
    private static bool _inFlight;

    public static RankInfo? For(string? steamId, string? seed)
    {
        if (string.IsNullOrEmpty(steamId) || string.IsNullOrEmpty(seed)) return null;
        var key = $"{steamId}|{seed}";
        if (key != _key && !_inFlight)
        {
            _inFlight = true;
            _key = key;
            _current = null;
            _ = Fetch(steamId, seed, key);
        }
        return _key == key ? _current : null;
    }

    private static async Task Fetch(string steamId, string seed, string key)
    {
        try
        {
            var r = await Client.GetRankAsync(steamId, seed).ConfigureAwait(false);
            if (_key == key) _current = r;
        }
        catch { /* ignore */ }
        finally { _inFlight = false; }
    }
}
