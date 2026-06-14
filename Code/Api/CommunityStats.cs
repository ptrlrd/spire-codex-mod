using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// Per-character community headline numbers (id is upper-cased, e.g. "NECROBINDER").
public sealed record CharCommunity(string Id, string Name, int Runs, double WinRate, double Share);

// A most-removed card: pct = its share of all community removals.
public sealed record RemovedCard(string Id, string Name, double Pct);

// Per-event option pick stats: Id is the option key (upper-cased; staged repeats arrive as
// KEY_0, KEY_1...), Pct its share of all decisions in that event.
public sealed record EventOptionStat(string Id, string Label, int Count, double Pct);

public sealed record EventCommunity(string Id, string Name, int Total, IReadOnlyList<EventOptionStat> Options);

// Per map-node-type danger for one act: how much HP a visit costs on average and how often
// a run ends there. Types keyed lowercase ("monster", "elite", "boss", "unknown", ...).
public sealed record NodeDanger(int Visits, double AvgDmgPct, double DeathRate);

public sealed record ActDanger(int Act, IReadOnlyDictionary<string, NodeDanger> Types);

// A campfire action: Pct = share of all campfire decisions, WinRate = how often runs that
// chose it won, PctLowHp/PctHighHp = its share among players below / at-or-above 50% HP
// walking in. The nullable fields are absent on older backend payloads.
public sealed record RestChoice(
    string Id, string Label, double Pct, double? WinRate, double? PctLowHp, double? PctHighHp);

// A relic's record at Ancient 3-relic offers: taken TakeRate% of the Offered times.
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

// One-shot cache of /api/runs/community-stats for the in-game tips (character portrait
// hover, card-removal service hover). Null until loaded; consumers just skip their tip.
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

    // Danger for one node type in one act (0-indexed act, lowercase type), or null.
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

    // Community danger for a SPECIFIC encounter (bare id, e.g. "CORPSE_SLUGS_WEAK"), or
    // null when unknown / below the sample floor. Lets the route rate the exact fight.
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
            // An empty payload (server stats snapshot rebuilding) is "not loaded yet": leave
            // _data null so the next hover's EnsureLoaded retries instead of caching nothing.
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
        catch { /* ignore */ }
    }
}
