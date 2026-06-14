using System;
using System.Collections;
using System.Collections.Generic;

namespace SpireCodex.Core;

// The current act's map as plain data, for the live spectator view: every node with its
// type, every edge, the path traveled so far, and where the player stands. Read from
// RunState.Map (ActMap) + VisitedMapCoords + CurrentMapCoord on the main thread (called
// from Sts2Access.ReadSnapshot, throttled here); PresencePublisher serializes Latest
// onto heartbeats from its background loop, so the swap is a single volatile reference.
public sealed class MapGraphData
{
    public string Key = "";              // seed|act: identity of the static graph
    public int Act;                      // 1-indexed, matching the snapshot
    public List<object[]> Nodes = new(); // [col, row, "monster"]
    public List<int[]> Edges = new();    // [col, row, childCol, childRow]
    public List<int[]> Path = new();     // visited coords, in travel order
    public int[]? Pos;                   // current coord, null between nodes
}

public static class MapExport
{
    private static volatile MapGraphData? _latest;
    private static DateTimeOffset _lastUpdate = DateTimeOffset.MinValue;

    public static MapGraphData? Latest => _latest;

    // Called every producer tick (~10x/s); does real work at most once a second. The
    // static graph (nodes/edges) is rebuilt only when the seed|act key changes; path
    // and position refresh on every update.
    public static void Update(object state, Producer.Snapshot snap)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastUpdate).TotalSeconds < 1.0) return;
            _lastUpdate = now;

            var map = Reflect.GetMember(state, "Map");
            if (map == null || map.GetType().Name == "NullActMap") { _latest = null; return; }

            var key = $"{snap.Seed}|{snap.Act}";
            var prev = _latest;
            var g = new MapGraphData { Key = key, Act = snap.Act };

            if (prev != null && prev.Key == key)
            {
                g.Nodes = prev.Nodes; // static per act; reuse
                g.Edges = prev.Edges;
            }
            else
            {
                BuildGraph(map, g);
            }

            if (Reflect.GetMember(state, "VisitedMapCoords") is IEnumerable visited)
                foreach (var c in visited)
                    if (CoordOf(c) is { } v) g.Path.Add(v);
            g.Pos = CoordOf(Reflect.GetMember(state, "CurrentMapCoord"));

            _latest = g;
        }
        catch { /* map export is best-effort; presence just omits it */ }
    }

    private static void BuildGraph(object map, MapGraphData g)
    {
        var seen = new HashSet<(int, int)>();

        void AddPoint(object? point)
        {
            if (point == null) return;
            if (CoordOf(Reflect.GetMember(point, "coord")) is not { } c) return;
            var type = Reflect.GetString(point, "PointType")?.ToLowerInvariant() ?? "unknown";
            if (!seen.Add((c[0], c[1]))) return;
            g.Nodes.Add(new object[] { c[0], c[1], type });
            if (Reflect.GetMember(point, "Children") is IEnumerable kids)
                foreach (var kid in kids)
                    if (kid != null && CoordOf(Reflect.GetMember(kid, "coord")) is { } k)
                        g.Edges.Add(new[] { c[0], c[1], k[0], k[1] });
        }

        // GetAllMapPoints may exclude the synthetic start/boss points (the map screen
        // adds those to its dictionary separately), so union them in explicitly.
        if (map.GetType().GetMethod("GetAllMapPoints")?.Invoke(map, null) is IEnumerable all)
            foreach (var p in all)
                AddPoint(p);
        AddPoint(Reflect.GetMember(map, "StartingMapPoint"));
        AddPoint(Reflect.GetMember(map, "BossMapPoint"));
        AddPoint(Reflect.GetMember(map, "SecondBossMapPoint"));
    }

    // MapCoord (struct with col/row fields), possibly boxed from MapCoord?.
    private static int[]? CoordOf(object? coord)
    {
        if (coord == null) return null;
        if (Reflect.GetMember(coord, "col") is int c && Reflect.GetMember(coord, "row") is int r)
            return new[] { c, r };
        return null;
    }
}
