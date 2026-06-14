using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// One leaderboard row: Rank is 1-based position in the board. Hash links to the run page.
public sealed record BoardRun(
    int Rank, string Player, string Character, int Ascension, int RunTime, int Floors, string? Hash);

// One of the player's own runs (recent-first).
public sealed record RunSummary(
    string Character, bool Win, bool Abandoned, int Ascension, int RunTime, int Floors,
    string? KilledBy, string? Date, string? Hash);

// Fetches the leaderboard and the player's recent runs for the companion panel.
// Read-only GETs against the public API; both return an empty list on any failure so the
// panel just shows "nothing to show" rather than erroring.
public static class RunFeeds
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    // A single-player leaderboard. category "fastest" (ranked by run_time, wins) or
    // "highest_ascension" (the daily climb). minAscension filters client-side (the API has
    // no ascension param) so we over-fetch to refill; today=true scopes the daily to UTC
    // today; ranks are re-numbered after the filter.
    public static async Task<List<BoardRun>> LeaderboardAsync(
        string category, string? gameMode = null, bool today = false, int minAscension = 0, int limit = 25)
    {
        var fetch = minAscension > 0 ? limit * 4 : limit;
        var url = $"{Config.ApiBase}/runs/leaderboard?category={category}&players=single&limit={fetch}";
        if (gameMode != null) url += $"&game_mode={gameMode}";
        if (today) url += "&today=true";

        var board = new List<BoardRun>();
        try
        {
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("runs", out var runs)) return board;
            var rank = 1;
            foreach (var r in runs.EnumerateArray())
            {
                var asc = Int(r, "ascension");
                if (asc < minAscension) continue;
                board.Add(new BoardRun(
                    rank++, Str(r, "username") ?? "anon", Str(r, "character") ?? "?",
                    asc, Int(r, "run_time"), Int(r, "floors_reached"), Str(r, "run_hash")));
                if (board.Count >= limit) break;
            }
        }
        catch { /* empty board */ }
        return board;
    }

    // The player's own winning runs, fastest first (their personal board).
    public static async Task<List<RunSummary>> PlayerWinsAsync(string? steamId, int limit = 60)
    {
        var all = await RecentRunsAsync(steamId, limit).ConfigureAwait(false);
        all.RemoveAll(r => !r.Win || r.Abandoned);
        all.Sort((a, b) => a.RunTime.CompareTo(b.RunTime));
        return all;
    }

    // A run's global rank on a board (null when it doesn't place, e.g. a loss on "fastest").
    public static async Task<int?> RunRankAsync(string? hash, string category = "fastest")
    {
        if (string.IsNullOrEmpty(hash)) return null;
        try
        {
            var json = await Http.GetStringAsync(
                $"{Config.ApiBase}/runs/leaderboard/rank/{hash}?category={category}").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("rank", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : null;
        }
        catch { return null; }
    }

    public static async Task<List<RunSummary>> RecentRunsAsync(string? steamId, int limit = 20)
    {
        var runs = new List<RunSummary>();
        if (string.IsNullOrEmpty(steamId)) return runs;
        var url = $"{Config.ApiBase}/runs/list?steam_id={Uri.EscapeDataString(steamId)}&limit={limit}";
        try
        {
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("runs", out var arr)) return runs;
            foreach (var r in arr.EnumerateArray())
                runs.Add(new RunSummary(
                    Str(r, "character") ?? "?",
                    Int(r, "win") != 0,
                    Int(r, "was_abandoned") != 0,
                    Int(r, "ascension"),
                    Int(r, "run_time"),
                    Int(r, "floors_reached"),
                    Str(r, "killed_by"),
                    Str(r, "submitted_at"),
                    Str(r, "run_hash")));
        }
        catch { /* empty list */ }
        return runs;
    }

    private static string? Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
