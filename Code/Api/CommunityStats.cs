using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpireCodex.Api;

public sealed record CharCommunity(string Id, string Name, int Runs, double WinRate, double Share);

public sealed record RemovedCard(string Id, string Name, double Pct);

public sealed record EventOptionStat(string Id, string Label, int Count, double Pct);

public sealed record EventCommunity(string Id, string Name, int Total, IReadOnlyList<EventOptionStat> Options);

public sealed record NodeDanger(int Visits, double AvgDmgPct, double DeathRate);

public sealed record ActDanger(int Act, IReadOnlyDictionary<string, NodeDanger> Types);

public sealed record RestChoice(
    string Id, string Label, double Pct, double? WinRate, double? PctLowHp, double? PctHighHp);

public sealed record AncientOffer(int Picks, int Offered, double TakeRate);

public sealed record CommunityStatsData(
    IReadOnlyList<CharCommunity> ByCharacter,
    IReadOnlyList<RemovedCard> MostRemoved,
    IReadOnlyList<EventCommunity> Events,
    IReadOnlyList<ActDanger> MapDanger,
    IReadOnlyList<RestChoice> RestSites,
    IReadOnlyDictionary<string, AncientOffer> AncientOffers,
    IReadOnlyDictionary<string, NodeDanger> EncounterDanger,
    double RewardSkipRate);

public static class CommunityStats
{
    private static CommunityStatsData? _data;
    private static bool _loading;

    public static CommunityStatsData? Data => _data;

    public static CharCommunity? Character(string? charId)
    {
        if (_data == null || string.IsNullOrEmpty(charId)) return null;
        foreach (var c in _data.ByCharacter)
            if (c.Id == charId) return c;
        return null;
    }

    public static EventCommunity? Event(string? eventId)
    {
        if (_data == null || string.IsNullOrEmpty(eventId)) return null;
        foreach (var e in _data.Events)
            if (e.Id == eventId) return e;
        return null;
    }

    public static NodeDanger? Danger(int actIndex, string? nodeType)
    {
        if (_data == null || string.IsNullOrEmpty(nodeType)) return null;
        foreach (var a in _data.MapDanger)
            if (a.Act == actIndex)
                return a.Types.GetValueOrDefault(nodeType);
        return null;
    }

    public static RestChoice? Rest(string? choiceId)
    {
        if (_data == null || string.IsNullOrEmpty(choiceId)) return null;
        foreach (var r in _data.RestSites)
            if (r.Id == choiceId) return r;
        return null;
    }

    public static AncientOffer? Ancient(string? relicId) =>
        _data == null || string.IsNullOrEmpty(relicId)
            ? null
            : _data.AncientOffers.GetValueOrDefault(relicId);

    public static NodeDanger? Encounter(string? encounterId) =>
        _data == null || string.IsNullOrEmpty(encounterId)
            ? null
            : _data.EncounterDanger.GetValueOrDefault(encounterId);

    public static void EnsureLoaded()
    {
        if (_data != null || _loading) return;
        _loading = true;
        _ = LoadAsync();
    }

    private static async Task LoadAsync()
    {
        try
        {
            var d = await new SpireCodexClient().GetCommunityStatsAsync().ConfigureAwait(false);
            if (d != null && (d.ByCharacter.Count > 0 || d.MostRemoved.Count > 0))
            {
                _data = d;
                Diag($"loaded: {d.ByCharacter.Count} chars, {d.MostRemoved.Count} removed, {d.Events.Count} events");
            }
            else Diag("empty payload; will retry on next hover");
        }
        finally { _loading = false; }
    }

    private static void Diag(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "spire-codex-scores.log"),
                $"{System.DateTimeOffset.UtcNow:o}  [community] {msg}\n");
        }
        catch {  }
    }
}
