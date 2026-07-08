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

    // Per-visited-node reveal: [col, row, room_type, encounter_id]. The actual room type +
    // encounter/event id the game recorded when each node was entered, so the spectator map
    // can light up each circle with what was really there (and resolve "?" nodes). Grows as
    // the player walks; encounter_id is null for shop/rest/treasure. Rides every beat.
    public List<object?[]> Reveals = new();
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
            BuildReveals(state, g);

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

    // What each visited node actually was. RunState.MapPointHistory is a per-act list of
    // entries indexed by row (the game's own GetHistoryEntryFor does MapPointHistory[act][row]),
    // and each entry's Rooms[0] holds the resolved RoomType + the encounter/event ModelId
    // recorded on entry. Join that against the visited coords (Path, which carry the column) by
    // row to emit [col, row, room_type, encounter_id] per visited circle. The resolved RoomType
    // is what a "?" node became, which the unpositioned route pool can't tell you.
    private static void BuildReveals(object state, MapGraphData g)
    {
        try
        {
            var actIndex = Reflect.GetInt(state, "CurrentActIndex");
            if (Reflect.GetMember(state, "MapPointHistory") is not IList acts
                || actIndex < 0 || actIndex >= acts.Count || acts[actIndex] is not IList rows)
                return;

            foreach (var coord in g.Path)
            {
                var row = coord[1];
                if (row < 0 || row >= rows.Count) continue;
                var entry = rows[row];
                var room0 = Reflect.GetMember(entry, "Rooms") is IList rl && rl.Count > 0 ? rl[0] : null;
                var type = Reflect.GetString(room0, "RoomType")?.ToLowerInvariant()
                           ?? Reflect.GetString(entry, "MapPointType")?.ToLowerInvariant()
                           ?? "unknown";
                var encId = Ids.Bare(Reflect.GetString(room0, "ModelId"));
                g.Reveals.Add(new object?[] { coord[0], row, type, encId });
            }
        }
        catch { /* reveals are best-effort; the rest of the map still ships */ }
    }

    // ---- Per-floor run history (the game's "previous floor" hover data) ---------------------
    // Built from RunState.MapPointHistory (per-act, row-indexed) joined with each entry's
    // PlayerStats (HP/gold/damage/turns/rewards/skipped). Kept separate from the act-map above so
    // it survives screen/act changes (it reads MapPointHistory off RunState, not the ActMap, which
    // blanks between rooms). Throttled to once a second and keyed on the run seed so a fresh run
    // clears the previous one's floors instead of serving them stale for a beat.
    private static volatile List<Producer.FloorSummary> _history = new();
    private static DateTimeOffset _historyAt = DateTimeOffset.MinValue;
    private static string? _historyKey;

    private const int RewardCap = 24; // bound a shop's full stock / degenerate floors on the wire

    public static List<Producer.FloorSummary> ReadHistory(object state, Producer.Snapshot snap)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var key = snap.Seed;
            var newRun = key != _historyKey; // rebuild immediately on a new run, don't serve stale
            if (!newRun && (now - _historyAt).TotalSeconds < 1.0) return _history;
            _historyAt = now;
            _historyKey = key;

            var list = new List<Producer.FloorSummary>();
            if (Reflect.GetMember(state, "MapPointHistory") is not IList acts) { _history = list; return list; }

            // Skip the floor the player is standing on (the game's hover only shows cleared nodes).
            var curAct = Reflect.GetInt(state, "CurrentActIndex", -1);
            var curRow = CoordOf(Reflect.GetMember(state, "CurrentMapCoord")) is { } cc ? cc[1] : -1;

            // Global floor number = within-act row (0-based index + 1) + all prior acts' floors,
            // matching NMapPoint's own numbering (coord.row + 1 + sum of earlier acts' counts).
            var priorFloors = 0;
            for (var a = 0; a < acts.Count; a++)
            {
                if (acts[a] is not IList rows) continue;
                for (var j = 0; j < rows.Count; j++)
                {
                    if (a == curAct && j == curRow) continue;
                    if (ReadFloor(rows[j], j + 1 + priorFloors, a + 1) is { } f)
                        list.Add(f);
                }
                priorFloors += rows.Count;
            }

            _history = list;
            return list;
        }
        catch { return _history; } // best-effort; keep the last good history on a read error
    }

    private static Producer.FloorSummary? ReadFloor(object? entry, int floor, int act)
    {
        if (entry == null) return null;

        var rooms = Reflect.GetMember(entry, "Rooms") as IList;
        var room0 = rooms != null && rooms.Count > 0 ? rooms[0] : null;

        // Prefer the resolved MapPointType (Shop/RestSite/Elite/...); fall back to the room's
        // RoomType (which is what an unresolved "?" node records as).
        var type = Reflect.GetString(entry, "MapPointType")?.ToLowerInvariant();
        if (string.IsNullOrEmpty(type) || type == "unknown")
            type = Reflect.GetString(room0, "RoomType")?.ToLowerInvariant() ?? "unknown";

        // Enemy/event id from the first room (null for shop/rest/treasure), as the hover uses.
        var encId = Ids.Bare(Reflect.GetString(room0, "ModelId"));

        // Turns from whichever room recorded a combat (0 on non-combat rooms).
        var turns = 0;
        if (rooms != null)
            foreach (var r in rooms)
                turns = Math.Max(turns, Reflect.GetInt(r, "TurnsTaken"));

        var f = new Producer.FloorSummary
        {
            Floor = floor,
            Act = act,
            Type = type ?? "unknown",
            EncounterId = encId,
            Turns = turns > 0 ? turns : (int?)null,
        };

        if (LocalStats(Reflect.GetMember(entry, "PlayerStats") as IList) is { } pe)
        {
            f.Hp = Reflect.GetInt(pe, "CurrentHp");
            f.MaxHp = Reflect.GetInt(pe, "MaxHp");
            f.Gold = Reflect.GetInt(pe, "CurrentGold");
            var dmg = Reflect.GetInt(pe, "DamageTaken");
            var heal = Reflect.GetInt(pe, "HpHealed");
            var spent = Reflect.GetInt(pe, "GoldSpent");
            var gained = Reflect.GetInt(pe, "GoldGained");
            f.DamageTaken = dmg > 0 ? dmg : (int?)null;
            f.Healed = heal > 0 ? heal : (int?)null;
            f.GoldSpent = spent > 0 ? spent : (int?)null;
            f.GoldGained = gained > 0 ? gained : (int?)null;
            ReadRewards(pe, f);
        }

        return f;
    }

    // The local player's stats entry for this floor. Solo: the sole entry. Co-op: match PlayerId
    // against the local net id, falling back to the first entry.
    private static object? LocalStats(IList? players)
    {
        if (players == null || players.Count == 0) return null;
        if (players.Count == 1) return players[0];
        if (LocalPlayer.NetId is { } local)
            foreach (var p in players)
                try { if (Reflect.GetMember(p, "PlayerId") is { } id && Convert.ToUInt64(id) == local) return p; }
                catch { /* mismatched id type: skip */ }
        return players[0];
    }

    private static void ReadRewards(object pe, Producer.FloorSummary f)
    {
        // Cards taken this floor: a combat pick, an event grant, or a shop purchase all land in
        // CardsGained (the game records only the non-picked choices as "skipped").
        foreach (var c in Enumerate(Reflect.GetMember(pe, "CardsGained")))
            AddReward(f.Rewards, "card", Ids.Bare(Reflect.GetString(c, "Id")));
        // Cards offered but not taken (the reward's other two, or a shop's unbought cards).
        foreach (var ch in Enumerate(Reflect.GetMember(pe, "CardChoices")))
            if (!Reflect.GetBool(ch, "wasPicked"))
                AddReward(f.Skipped, "card", Ids.Bare(Reflect.GetString(Reflect.GetMember(ch, "Card"), "Id")));
        // Relic / potion choices split by whether they were taken (covers shop stock too).
        SplitChoices(Reflect.GetMember(pe, "RelicChoices"), "relic", f);
        SplitChoices(Reflect.GetMember(pe, "PotionChoices"), "potion", f);
    }

    private static void SplitChoices(object? choices, string kind, Producer.FloorSummary f)
    {
        foreach (var mc in Enumerate(choices))
            AddReward(Reflect.GetBool(mc, "wasPicked") ? f.Rewards : f.Skipped,
                kind, Ids.Bare(Reflect.GetString(mc, "choice")));
    }

    private static void AddReward(List<Producer.FloorReward> into, string kind, string? id)
    {
        if (!string.IsNullOrEmpty(id) && into.Count < RewardCap)
            into.Add(new Producer.FloorReward(kind, id!));
    }

    private static IEnumerable Enumerate(object? c) => c as IEnumerable ?? System.Array.Empty<object>();

    // MapCoord (struct with col/row fields), possibly boxed from MapCoord?.
    private static int[]? CoordOf(object? coord)
    {
        if (coord == null) return null;
        if (Reflect.GetMember(coord, "col") is int c && Reflect.GetMember(coord, "row") is int r)
            return new[] { c, r };
        return null;
    }
}
