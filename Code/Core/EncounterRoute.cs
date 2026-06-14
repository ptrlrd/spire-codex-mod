using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SpireCodex.Producer;

namespace SpireCodex.Core;

// Reads the act's pre-rolled encounter/event sequence from RunState.Act._rooms (a RoomSet),
// so the mod knows the SPECIFIC upcoming fights and events, not just node types. The act
// generates these once at start (ActModel.GenerateRooms, seeded) and consumes them by an
// index counter per type (RoomSet.normalEncountersVisited / eliteEncountersVisited /
// eventsVisited) - the lists are never mutated, so the sequence is deterministic and the
// "next" of each type is list[visited]. Confirmed against the decompiled sts2.dll.
//
// We expose the UPCOMING slice (from the visited counter onward) in consumption order, so
// the map's route DP can index the Kth upcoming monster/elite directly, and the spectator
// view can show real fight names.
internal static class EncounterRoute
{
    private const int MaxUpcoming = 12; // covers any single path's combat count with headroom

    // The act's display name (its region title). The member that holds it on ActModel can
    // shift between game versions, so try the localized title members first (the same
    // GetFormattedText path event titles use), then fall back to a prettified id so the field
    // is never empty when an act exists. Stays churn-resistant rather than breaking on a rename.
    public static string? ReadActName(object? state)
    {
        var act = Reflect.GetMember(state, "Act");
        if (act == null) return null;

        foreach (var member in new[] { "Title", "Name", "DisplayName" })
            if (LocText(Reflect.GetMember(act, member)) is { } loc) return loc;

        var id = Ids.Bare(Reflect.GetString(act, "Id"))
                 ?? Ids.Bare(Reflect.GetString(Reflect.GetMember(act, "Model"), "Id"));
        return string.IsNullOrEmpty(id) ? null : PrettyPlain(id);
    }

    // Title-case an id without dropping trailing tokens (unlike Pretty, which strips variant
    // markers - an act id like "ACT_1" must not lose its "1").
    private static string PrettyPlain(string id)
    {
        var parts = id.Split('_');
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) sb.Append(p.Substring(1).ToLowerInvariant());
        }
        return sb.Length > 0 ? sb.ToString() : id;
    }

    public static ActRoute? Read(object? state)
    {
        var act = Reflect.GetMember(state, "Act");
        var rooms = Reflect.GetMember(act, "_rooms");
        if (rooms == null) return null;

        var route = new ActRoute
        {
            Monsters = Upcoming(Reflect.GetMember(rooms, "normalEncounters"),
                                Reflect.GetInt(rooms, "normalEncountersVisited"), "monster"),
            Elites = Upcoming(Reflect.GetMember(rooms, "eliteEncounters"),
                              Reflect.GetInt(rooms, "eliteEncountersVisited"), "elite"),
            Events = UpcomingEvents(Reflect.GetMember(rooms, "events"),
                                    Reflect.GetInt(rooms, "eventsVisited"),
                                    VisitedEventIds(state)),
        };

        // Boss / Ancient are specific. The getters throw when unset (Reflect swallows that).
        if (EncounterRefOf(Reflect.GetMember(act, "BossEncounter"), "boss") is { } b) route.Boss = b;
        if (EncounterRefOf(Reflect.GetMember(act, "Ancient"), "ancient") is { } a) route.Ancient = a;

        if (route.Monsters.Count == 0 && route.Elites.Count == 0 && route.Boss == null) return null;
        return route;
    }

    // The next MaxUpcoming encounters from the visited index onward (no modulo wrap: we
    // don't show repeats past the end of the act's list).
    private static List<EncounterRef> Upcoming(object? listObj, int visited, string roomType)
    {
        var refs = new List<EncounterRef>();
        if (listObj is not IList list) return refs;
        for (var i = visited; i < list.Count && refs.Count < MaxUpcoming; i++)
            if (EncounterRefOf(list[i], roomType) is { } r) refs.Add(r);
        return refs;
    }

    // Upcoming events from the visited index onward, skipping any already seen this run.
    private static List<EncounterRef> UpcomingEvents(object? listObj, int visited, HashSet<string> seen)
    {
        var refs = new List<EncounterRef>();
        if (listObj is not IList list) return refs;
        for (var i = visited; i < list.Count && refs.Count < MaxUpcoming; i++)
        {
            var ev = list[i];
            var id = Ids.Bare(Reflect.GetString(ev, "Id"));
            if (id == null || seen.Contains(id)) continue;
            var name = LocText(Reflect.GetMember(ev, "Title")) ?? Pretty(id);
            refs.Add(new EncounterRef(id, name, "event", false));
        }
        return refs;
    }

    private static EncounterRef? EncounterRefOf(object? encounter, string fallbackType)
    {
        var id = Ids.Bare(Reflect.GetString(encounter, "Id"));
        if (id == null) return null;
        var type = Reflect.GetString(encounter, "RoomType")?.ToLowerInvariant() ?? fallbackType;
        return new EncounterRef(id, Pretty(id), type, Reflect.GetBool(encounter, "IsWeak"));
    }

    private static HashSet<string> VisitedEventIds(object? state)
    {
        var set = new HashSet<string>();
        if (Reflect.GetMember(state, "VisitedEventIds") is IEnumerable ids)
            foreach (var id in ids)
                if (Ids.Bare(id?.ToString()) is { } b) set.Add(b);
        return set;
    }

    private static string? LocText(object? locString)
    {
        var s = Reflect.CallString(locString, "GetFormattedText");
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    // "CORPSE_SLUGS_WEAK" -> "Corpse Slugs": drop a trailing variant marker, title-case the
    // rest. Encounters have no localized name, so this is the in-game label; the bare id
    // also rides to the backend/frontend which resolve a proper name.
    private static readonly HashSet<string> Variant = new()
        { "WEAK", "NORMAL", "ELITE", "BOSS", "A", "B", "C", "1", "2", "3" };

    private static string Pretty(string bareId)
    {
        var parts = bareId.Split('_');
        var end = parts.Length;
        if (end > 1 && Variant.Contains(parts[end - 1])) end--;
        var sb = new StringBuilder();
        for (var i = 0; i < end; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            var p = parts[i];
            if (p.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) sb.Append(p.Substring(1).ToLowerInvariant());
        }
        return sb.Length > 0 ? sb.ToString() : bareId;
    }
}
