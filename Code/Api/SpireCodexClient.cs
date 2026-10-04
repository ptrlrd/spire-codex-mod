using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SpireCodex.Api;

public sealed class SpireCodexClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<RunUploadResult> UploadRunAsync(
        string runJson, string? steamId, string? username, string sts2Version)
    {
        var url = $"{Config.ApiBase}/runs?sts2_version={Uri.EscapeDataString(sts2Version)}";
        if (!string.IsNullOrEmpty(steamId)) url += $"&steam_id={Uri.EscapeDataString(steamId)}";
        if (!string.IsNullOrEmpty(username)) url += $"&username={Uri.EscapeDataString(username)}";

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(runJson, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (!string.IsNullOrEmpty(SteamAuth.Token))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SteamAuth.Token);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                var code = (int)resp.StatusCode;
                if ((code == 429 || code >= 500) && attempt < UploadMaxRetries)
                {
                    await Task.Delay(RetryDelay(resp, attempt)).ConfigureAwait(false);
                    continue;
                }
                return new RunUploadResult(resp.IsSuccessStatusCode, code, body);
            }
            catch (Exception e)
            {
                if (attempt < UploadMaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 1 << attempt))).ConfigureAwait(false);
                    continue;
                }
                return new RunUploadResult(false, 0, e.Message);
            }
        }
    }

    private const int UploadMaxRetries = 4;

    private static TimeSpan RetryDelay(HttpResponseMessage resp, int attempt)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } d && d > TimeSpan.Zero)
            return d < TimeSpan.FromSeconds(60) ? d : TimeSpan.FromSeconds(60);
        if (ra?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait < TimeSpan.FromSeconds(60) ? wait : TimeSpan.FromSeconds(60);
        }
        return TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));
    }

    public async Task<RunUploadResult> UploadReplayAsync(string runHash, byte[] gzip)
    {
        var url = $"{Config.ApiBase}/runs/{Uri.EscapeDataString(runHash)}/replay";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(gzip);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
                content.Headers.ContentEncoding.Add("gzip");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (!string.IsNullOrEmpty(SteamAuth.Token))
                    req.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SteamAuth.Token);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                var code = (int)resp.StatusCode;
                if ((code == 429 || code >= 500) && attempt < UploadMaxRetries)
                {
                    await Task.Delay(RetryDelay(resp, attempt)).ConfigureAwait(false);
                    continue;
                }
                return new RunUploadResult(resp.IsSuccessStatusCode, code, body);
            }
            catch (Exception e)
            {
                if (attempt < UploadMaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 1 << attempt))).ConfigureAwait(false);
                    continue;
                }
                return new RunUploadResult(false, 0, e.Message);
            }
        }
    }

    public const string SkipId = "SKIP";

    public async Task<ScoreSet> GetScoresAsync(
        string entityType, string? character = null, string? statFilter = null,
        bool includeSkip = false)
    {
        var url = $"{Config.ApiBase}/runs/scores/{entityType}";
        var query = new List<string>();
        if (!string.IsNullOrEmpty(character)) query.Add($"character={Uri.EscapeDataString(character)}");
        if (!string.IsNullOrEmpty(statFilter) && statFilter != StatFilter.DefaultKey)
            query.Add($"stat_filter={Uri.EscapeDataString(statFilter)}");
        if (includeSkip) query.Add("include_skip=1");
        if (query.Count > 0) url += "?" + string.Join("&", query);
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var raw = await JsonSerializer
            .DeserializeAsync<Dictionary<string, ScoreDto>>(stream)
            .ConfigureAwait(false) ?? new();

        SkipScore? skip = null;
        if (raw.TryGetValue(SkipId, out var s) && s.Elo is { } skipElo)
            skip = new SkipScore(
                skipElo, s.Offered, s.Picked ?? s.Picks, s.PickRate, s.OffAct, s.PickAct);

        var scores = new Dictionary<string, EntityScore>(raw.Count);
        foreach (var (id, v) in raw)
        {
            if (id == SkipId) continue;
            scores[id] = new EntityScore(
                v.Score ?? 0, v.WinRate ?? 0,
                (int)Math.Min(v.Picks, int.MaxValue), v.Scope, v.Elo);
        }
        return new ScoreSet(scores, skip);
    }

    private sealed class ScoreDto
    {
        [JsonPropertyName("score")] public double? Score { get; set; }
        [JsonPropertyName("win_rate")] public double? WinRate { get; set; }
        [JsonPropertyName("picks")] public long Picks { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("elo")] public double? Elo { get; set; }
        [JsonPropertyName("offered")] public long Offered { get; set; }
        [JsonPropertyName("picked")] public long? Picked { get; set; }
        [JsonPropertyName("pick_rate")] public double PickRate { get; set; }
        [JsonPropertyName("off_act")] public long[]? OffAct { get; set; }
        [JsonPropertyName("pick_act")] public long[]? PickAct { get; set; }
    }

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

    public async Task<PersonalStatsData?> GetUserPicksAsync()
    {
        if (string.IsNullOrEmpty(SteamAuth.Token)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Config.ApiBase}/runs/me/picks");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SteamAuth.Token);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<UserPicksDto>(stream).ConfigureAwait(false);
            return new PersonalStatsData(Picks(dto?.Cards), Picks(dto?.Ancients));
        }
        catch { return null; }
    }

    private static Dictionary<string, UserPick> Picks(Dictionary<string, UserPickDto>? raw)
    {
        var result = new Dictionary<string, UserPick>();
        foreach (var (id, p) in raw ?? new())
            result[id.ToUpperInvariant()] = new UserPick(p.Picked, p.Offered);
        return result;
    }

    private sealed class UserPicksDto
    {
        [JsonPropertyName("cards")] public Dictionary<string, UserPickDto>? Cards { get; set; }
        [JsonPropertyName("ancients")] public Dictionary<string, UserPickDto>? Ancients { get; set; }
    }

    private sealed class UserPickDto
    {
        [JsonPropertyName("picked")] public int Picked { get; set; }
        [JsonPropertyName("offered")] public int Offered { get; set; }
    }

    public Task<CardStats> GetCardStatsAsync(string id) => GetStatsAsync("cards", id);

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
