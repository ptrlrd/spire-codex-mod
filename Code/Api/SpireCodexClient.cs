using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// Thin client for the Spire Codex API. See docs/API.md for the full contract.
public sealed class SpireCodexClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // POST the raw .run JSON to /api/runs. Idempotent; the server dedupes by run hash.
    // Attribution by ?steam_id / ?username; sts2_version tags the build. There is no
    // bearer auth today (Phase 3 adds the Steam JWT flow). See docs/API.md.
    public async Task<RunUploadResult> UploadRunAsync(
        string runJson, string? steamId, string? username, string sts2Version)
    {
        var url = $"{Config.ApiBase}/runs?sts2_version={Uri.EscapeDataString(sts2Version)}";
        if (!string.IsNullOrEmpty(steamId)) url += $"&steam_id={Uri.EscapeDataString(steamId)}";
        if (!string.IsNullOrEmpty(username)) url += $"&username={Uri.EscapeDataString(username)}";

        try
        {
            using var content = new StringContent(runJson, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            // Authenticated upload when signed in; ?steam_id still attributes unauthenticated.
            if (!string.IsNullOrEmpty(SteamAuth.Token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SteamAuth.Token);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new RunUploadResult(resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
        }
        catch (Exception e)
        {
            return new RunUploadResult(false, 0, e.Message);
        }
    }

    // GET /api/runs/scores/{entityType}[?character=] -> { "ENTITY_ID": {score, win_rate,
    // picks, wins[, scope]}, ... }. With `character`, entries carry that character's slice
    // when its sample is big enough (scope="character"), else global (scope="global").
    // Older backends ignore the param and return global numbers with no scope.
    public async Task<Dictionary<string, EntityScore>> GetScoresAsync(string entityType, string? character = null)
    {
        var url = $"{Config.ApiBase}/runs/scores/{entityType}";
        if (!string.IsNullOrEmpty(character)) url += $"?character={Uri.EscapeDataString(character)}";
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var raw = await JsonSerializer
            .DeserializeAsync<Dictionary<string, ScoreDto>>(stream)
            .ConfigureAwait(false) ?? new();
        return raw.ToDictionary(
            kv => kv.Key,
            kv => new EntityScore(kv.Value.Score ?? 0, kv.Value.WinRate, kv.Value.Picks, kv.Value.Scope, kv.Value.Elo));
    }

    private sealed class ScoreDto
    {
        // score is null for entries with no data (0 picks); elo only exists for
        // reward-offered cards (null for relics/potions/starters).
        [JsonPropertyName("score")] public double? Score { get; set; }
        [JsonPropertyName("win_rate")] public double WinRate { get; set; }
        [JsonPropertyName("picks")] public int Picks { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("elo")] public double? Elo { get; set; }
    }

    // GET /api/runs/community-stats -> headline community numbers. We only parse what the
    // in-game tips need: per-character win rates and the most-removed cards.
    public async Task<CommunityStatsData?> GetCommunityStatsAsync()
    {
        try
        {
            using var resp = await Http.GetAsync($"{Config.ApiBase}/runs/community-stats").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<CommunityDto>(stream).ConfigureAwait(false);
            if (dto == null) return null;

            var chars = new List<CharCommunity>();
            foreach (var c in dto.ByCharacter ?? new())
                chars.Add(new CharCommunity(
                    (c.Id ?? "").ToUpperInvariant(), c.Name ?? c.Id ?? "?", c.Runs, c.WinRate, c.Share));
            var removed = new List<RemovedCard>();
            foreach (var r in dto.MostRemoved ?? new())
                removed.Add(new RemovedCard(r.Id ?? "?", r.Name ?? r.Id ?? "?", r.Pct));
            var events = new List<EventCommunity>();
            foreach (var e in dto.Events ?? new())
            {
                var opts = new List<EventOptionStat>();
                foreach (var o in e.Options ?? new())
                    opts.Add(new EventOptionStat(
                        (o.Id ?? "").ToUpperInvariant(), o.Label ?? o.Id ?? "?", o.Count, o.Pct));
                events.Add(new EventCommunity(
                    (e.Id ?? "").ToUpperInvariant(), e.Name ?? e.Id ?? "?", e.Total, opts));
            }
            var danger = new List<ActDanger>();
            foreach (var a in dto.MapDanger ?? new())
            {
                var types = new Dictionary<string, NodeDanger>();
                foreach (var (t, nd) in a.Types ?? new())
                    types[t] = new NodeDanger(nd.Visits, nd.AvgDmgPct, nd.DeathRate);
                danger.Add(new ActDanger(a.Act, types));
            }
            var rest = new List<RestChoice>();
            foreach (var r in dto.RestSites ?? new())
                rest.Add(new RestChoice(
                    (r.Id ?? "").ToUpperInvariant(), r.Label ?? r.Id ?? "?",
                    r.Pct, r.WinRate, r.PctLowHp, r.PctHighHp));
            var ancients = new Dictionary<string, AncientOffer>();
            foreach (var (rid, ao) in dto.AncientOffers ?? new())
                ancients[rid.ToUpperInvariant()] = new AncientOffer(ao.Picks, ao.Offered, ao.TakeRate);
            var encounterDanger = new Dictionary<string, NodeDanger>();
            foreach (var (eid, nd) in dto.EncounterDanger ?? new())
                encounterDanger[eid.ToUpperInvariant()] = new NodeDanger(nd.Visits, nd.AvgDmgPct, nd.DeathRate);

            return new CommunityStatsData(chars, removed, events, danger, rest, ancients, encounterDanger, dto.RewardSkipRate);
        }
        catch { return null; }
    }

    private sealed class CommunityDto
    {
        [JsonPropertyName("by_character")] public List<CommunityCharDto>? ByCharacter { get; set; }
        [JsonPropertyName("most_removed")] public List<RemovedDto>? MostRemoved { get; set; }
        [JsonPropertyName("events")] public List<EventDto>? Events { get; set; }
        [JsonPropertyName("map_danger")] public List<ActDangerDto>? MapDanger { get; set; }
        [JsonPropertyName("rest_sites")] public List<RestSiteDto>? RestSites { get; set; }
        [JsonPropertyName("ancient_offers")] public Dictionary<string, AncientOfferDto>? AncientOffers { get; set; }
        [JsonPropertyName("encounter_danger")] public Dictionary<string, NodeDangerDto>? EncounterDanger { get; set; }
        [JsonPropertyName("reward_skip_rate")] public double RewardSkipRate { get; set; }
    }

    private sealed class RestSiteDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("pct")] public double Pct { get; set; }
        // Nullable: older payloads (pre map-danger deploy) lack these.
        [JsonPropertyName("win_rate")] public double? WinRate { get; set; }
        [JsonPropertyName("pct_low_hp")] public double? PctLowHp { get; set; }
        [JsonPropertyName("pct_high_hp")] public double? PctHighHp { get; set; }
    }

    private sealed class AncientOfferDto
    {
        [JsonPropertyName("picks")] public int Picks { get; set; }
        [JsonPropertyName("offered")] public int Offered { get; set; }
        [JsonPropertyName("take_rate")] public double TakeRate { get; set; }
    }

    private sealed class ActDangerDto
    {
        [JsonPropertyName("act")] public int Act { get; set; }
        [JsonPropertyName("types")] public Dictionary<string, NodeDangerDto>? Types { get; set; }
    }

    private sealed class NodeDangerDto
    {
        [JsonPropertyName("visits")] public int Visits { get; set; }
        [JsonPropertyName("avg_dmg_pct")] public double AvgDmgPct { get; set; }
        [JsonPropertyName("death_rate")] public double DeathRate { get; set; }
    }

    private sealed class EventDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("options")] public List<EventOptionDto>? Options { get; set; }
    }

    private sealed class EventOptionDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("pct")] public double Pct { get; set; }
    }

    private sealed class CommunityCharDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("runs")] public int Runs { get; set; }
        [JsonPropertyName("win_rate")] public double WinRate { get; set; }
        [JsonPropertyName("share")] public double Share { get; set; }
    }

    private sealed class RemovedDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("pct")] public double Pct { get; set; }
    }

    public Task<CardStats> GetCardStatsAsync(string id) => GetStatsAsync("cards", id);

    // GET /api/runs/stats/{entityType}/{id} -> full per-entity stats (for the hover tooltip).
    // Same JSON shape for cards and relics.
    public async Task<CardStats> GetStatsAsync(string entityType, string id)
    {
        var url = $"{Config.ApiBase}/runs/stats/{entityType}/{Uri.EscapeDataString(id)}";
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var dto = await JsonSerializer.DeserializeAsync<StatsDto>(stream).ConfigureAwait(false) ?? new StatsDto();

        var chars = new List<CharStat>();
        if (dto.ByCharacter != null)
            foreach (var c in dto.ByCharacter)
                chars.Add(new CharStat(c.Character ?? "?", c.WinRate, c.Picks));

        return new CardStats(id, dto.Score, dto.WinRate, dto.PickRate, dto.BaselineWinRate, dto.Picks, chars);
    }

    private sealed class StatsDto
    {
        [JsonPropertyName("score")] public double? Score { get; set; }
        [JsonPropertyName("win_rate")] public double WinRate { get; set; }
        [JsonPropertyName("pick_rate")] public double PickRate { get; set; }
        [JsonPropertyName("baseline_win_rate")] public double BaselineWinRate { get; set; }
        [JsonPropertyName("picks")] public int Picks { get; set; }
        [JsonPropertyName("by_character")] public List<CharDto>? ByCharacter { get; set; }
    }

    private sealed class CharDto
    {
        [JsonPropertyName("character")] public string? Character { get; set; }
        [JsonPropertyName("win_rate")] public double WinRate { get; set; }
        [JsonPropertyName("picks")] public int Picks { get; set; }
    }

    // POST /api/presence — the ~30s live-run heartbeat that feeds the site's "who is in a
    // run right now" view. Requires the Steam JWT (the server keys the entry by the token's
    // verified steam_id); returns false without one or on any failure so the publisher
    // just retries on its next tick.
    public async Task<bool> PostPresenceAsync(string json)
    {
        if (string.IsNullOrEmpty(SteamAuth.Token)) return false;
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Config.ApiBase}/presence") { Content = content };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SteamAuth.Token);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // GET /api/runs/leaderboard/seed-rank?seed=&steam_id= -> seed + global standing. Rank
    // fields are null when the player has no winning run in that pool. Returns null on any
    // non-2xx (e.g. the endpoint isn't deployed yet), so callers degrade silently.
    public async Task<RankInfo?> GetRankAsync(string steamId, string seed)
    {
        var url = $"{Config.ApiBase}/runs/leaderboard/seed-rank?seed={Uri.EscapeDataString(seed)}&steam_id={Uri.EscapeDataString(steamId)}";
        try
        {
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<RankDto>(stream).ConfigureAwait(false);
            return dto == null
                ? null
                : new RankInfo(dto.SeedRank, dto.SeedTotal, dto.GlobalRank, dto.GlobalTotal, dto.Percentile);
        }
        catch { return null; }
    }

    private sealed class RankDto
    {
        [JsonPropertyName("seed_rank")] public int? SeedRank { get; set; }
        [JsonPropertyName("seed_total")] public int SeedTotal { get; set; }
        [JsonPropertyName("global_rank")] public int? GlobalRank { get; set; }
        [JsonPropertyName("global_total")] public int GlobalTotal { get; set; }
        [JsonPropertyName("percentile")] public double? Percentile { get; set; }
    }
}

public readonly record struct RunUploadResult(bool Success, int StatusCode, string Body);
