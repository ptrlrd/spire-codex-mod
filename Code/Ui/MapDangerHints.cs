using System;
using System.Collections;
using System.Collections.Generic;
using Godot;
using SpireCodex.Api;
using SpireCodex.Core;
using SpireCodex.Producer;

namespace SpireCodex.Ui;

// Community route guidance on the act map, all visual (no numbers):
//  - green rings trace the recommended route from the player's position to the boss;
//  - yellow rings flag nodes ahead that the community finds notably more challenging
//    (high avg HP lost / death rate), so the player can spot tough spots at a glance.
//
// The route is DYNAMIC: it minimizes community HP danger PLUS an opportunity cost tied to
// the player's resources, so it doesn't just "avoid all combat" and string together free
// shops you can't afford or rests you don't need (the safe-but-broke-3-shops problem). See
// RouteNodeCost / OpportunityCost. Danger data is the map_danger section of
// /api/runs/community-stats; non-combat types have no danger.
//
// Map access: NMapScreen._mapPointDictionary (MapCoord -> NMapPoint). Each NMapPoint has
// State (Travelable = an immediate choice, Traveled = already visited) and Point, the
// MapPoint model whose Children sets form the act DAG.
public partial class MapDangerHints : Node
{
    // A node ahead is "challenging" when its community danger cost clears this (avg HP%
    // lost + death-rate weight); catches elites and the tougher monster/unknown rooms.
    private const double ChallengeThreshold = 15.0;
    private static readonly Color RouteGreen = new(0.53f, 0.88f, 0.54f, 0.85f);
    private static readonly Color ChallengeYellow = new(0.93f, 0.80f, 0.30f, 0.92f);

    private double _accum;
    private double _searchAccum = 99; // search immediately on first tick
    private Node? _game;
    private Node? _mapScreen;
    private bool _sawTravelable; // had real travel choices this map-open (vs a mid-room peek)
    private int _traveledCount = -1; // Traveled-node count last tick (-1 = fresh map session)
    private HashSet<ulong>? _prevTravelable; // travel-choice node ids last tick (null = fresh)
    private bool _picked; // player committed to a node this session; freeze guidance until close

    private CanvasLayer _layer = null!;
    private readonly Dictionary<ulong, Panel> _rings = new();
    private readonly Dictionary<ulong, RichTextLabel> _labels = new(); // encounter names on the route
    private RichTextLabel? _eventBanner; // corner "upcoming events" readout (sequence, not node-pinned)

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; map danger hints not started");
            return;
        }
        var n = new MapDangerHints { Name = "SpireCodexMapDanger" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, n);
        MainFile.Logger.Info("map danger hints started");
    }

    public override void _Ready()
    {
        _layer = new CanvasLayer { Layer = 199 }; // below the card plates, above the map
        AddChild(_layer);
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < 0.12) return;
        _accum = 0;

        // No screen-name gate: the map opens OVER the previous room (the live screen still
        // says "combat"/"merchant" while you pick the next node), so gate on the map screen
        // node actually being visible. The tree search is throttled to once a second while
        // unresolved; once cached, per-tick cost is a visibility check.
        var snap = Producer.LiveStateProducer.Latest;
        if (!SpireCodexConfig.ShowMapDanger || snap is not { InRun: true })
        {
            ClearAll();
            return;
        }

        CardScan.FindOverlayContainer(ref _game); // resolves/caches the Game node
        if (_mapScreen != null && (!GodotObject.IsInstanceValid(_mapScreen) || !_mapScreen.IsInsideTree()))
            _mapScreen = null;
        if (_mapScreen == null)
        {
            _searchAccum += 0.12;
            if (_searchAccum < 1.0) { ClearAll(); return; }
            _searchAccum = 0;
            CardScan.FindCachedByType(_game, "NMapScreen", ref _mapScreen);
        }
        // Render only while the map is the genuinely-active front screen. Visible/IsOpen both
        // linger true on the map when ANOTHER screen covers it (the card library, inspect
        // card/relic, modals) or after leaving it, which leaked the rings onto those screens.
        // MapIsFront is the game's own "am I the current screen" test (the one NMapScreen uses to
        // hide its own UX). Plus stand down when the F5 overlay is up (it draws above us, so our
        // rings would otherwise show around/through it).
        if (_mapScreen is not Control { Visible: true } screen
            || !Reflect.GetBool(screen, "IsOpen")
            || !MapIsFront(screen)
            || DeckImagePanel.IsOpen)
        {
            ClearAll();
            return;
        }

        var actIndex = Math.Max(0, snap.Act - 1); // snapshot act is 1-indexed

        // Upcoming events, in order, in a corner readout. Honest sequence (NOT pinned to a
        // node): "your next event will be X" is true even though we can't say which ? it is.
        UpdateEventBanner(snap.Route);

        // Collect the visible points: model -> (ui node, state). Models are the shared
        // MapPoint instances whose Children reference each other, so reference keys work.
        // Alongside, over the WHOLE map (not just visible nodes, so scrolling can't perturb
        // these): count Traveled nodes and collect the ids of the current travel choices.
        var byModel = new Dictionary<object, (Control Ui, string State)>();
        var traveled = 0;
        var travelableIds = new HashSet<ulong>();
        if (Reflect.GetMember(screen, "_mapPointDictionary") is IDictionary points)
        {
            foreach (var value in points.Values)
            {
                if (value is not Control ui) continue;
                var state = Reflect.GetMember(ui, "State")?.ToString() ?? "";
                if (state == "Traveled") traveled++;
                else if (state == "Travelable") travelableIds.Add(ui.GetInstanceId());
                if (!ui.Visible) continue;
                if (Reflect.GetMember(ui, "Point") is not { } model) continue;
                byModel[model] = (ui, state);
            }
        }
        if (byModel.Count == 0) { ClearRoute(); return; }

        // Freeze guidance the moment the player commits to a node, BEFORE computing any rings,
        // so the jump never draws. Committing flips the chosen node out of "Travelable" (and
        // bumps the Traveled count); the route would otherwise re-solve among the leftover
        // siblings / the next row and the rings would visibly jump onto a different event. A
        // commit = a node that was a travel choice last tick is no longer one, OR the Traveled
        // count rose. Stays frozen until the map closes (ClearAll re-arms), as the map screen
        // lingers visible through the travel animation. New choices simply APPEARING never counts.
        if (_picked) { ClearRoute(); return; }
        var committed = _traveledCount >= 0 && traveled > _traveledCount;
        if (!committed && _prevTravelable != null)
            foreach (var id in _prevTravelable)
                if (!travelableIds.Contains(id)) { committed = true; break; }
        if (committed) { _picked = true; ClearRoute(); return; }
        _prevTravelable = travelableIds;
        _traveledCount = traveled;

        // The frontier = this turn's travel choices.
        var frontier = new List<object>();
        foreach (var (model, v) in byModel)
            if (v.State == "Travelable") frontier.Add(model);

        if (frontier.Count > 0)
        {
            _sawTravelable = true;
        }
        else
        {
            // No travel choices. Two very different cases:
            //  - We HAD choices this map-open and they just vanished: the player committed to a
            //    node and is travelling. Recomputing from the next position here is what makes
            //    the rings visibly jump forward onto the next event the instant you click. So
            //    just clear and let the next map draw fresh guidance. _sawTravelable stays set
            //    until the map fully closes (ClearAll), so we keep clearing through the travel
            //    animation instead of jumping.
            //  - We NEVER had choices (peeking the map mid-room, travel disabled): fall back to
            //    the children of the deepest traveled node so guidance still shows while planning.
            if (_sawTravelable) { ClearRoute(); return; }
            if (DeepestTraveled(byModel) is { } from)
                foreach (var child in ChildrenOf(from))
                    if (byModel.ContainsKey(child)) frontier.Add(child);
        }
        if (frontier.Count == 0) { ClearRoute(); return; }

        // Route scoring is dynamic to the player's resources: gold (shops are wasted when
        // broke) and HP (rests are wasted near full); and to the SPECIFIC upcoming fights
        // (snap.Route), so a known-deadly elite actually pushes the route away. The DP state
        // carries (monstersSeen, elitesSeen) so each combat node is priced by the exact
        // encounter it will be (encounters are consumed in visit order).
        var route = snap.Route;
        var gold = snap.Gold;
        var hpPct = snap.MaxHp > 0 ? (double)snap.CurrentHp / snap.MaxHp : 1.0;
        var ctx = new RouteCtx(route, actIndex, gold, hpPct);

        var memo = new Dictionary<(object, int, int), double>();
        var best = double.MaxValue;
        object? safest = null;
        foreach (var f in frontier)
        {
            var cost = RouteCost(f, 0, 0, memo, ctx);
            if (cost < best) { best = cost; safest = f; }
        }

        // Green: walk the recommended route to the act end, tracking the seen-counts so we
        // can both pick the min-cost child and label each combat node with its real fight.
        var green = new HashSet<object>();
        var labels = new Dictionary<object, string>();
        for (var (walk, wm, we) = (safest, 0, 0); walk != null && byModel.ContainsKey(walk);)
        {
            green.Add(walk);
            var type = PointType(walk);
            if (LabelFor(type, wm, we, route) is { } name) labels[walk] = name;

            var nm = wm + (type == "monster" ? 1 : 0);
            var ne = we + (type == "elite" ? 1 : 0);
            object? next = null;
            var nextCost = double.MaxValue;
            foreach (var child in ChildrenOf(walk))
            {
                if (!byModel.ContainsKey(child)) continue;
                var c = RouteCost(child, nm, ne, memo, ctx);
                if (c < nextCost) { nextCost = c; next = child; }
            }
            (walk, wm, we) = (next, nm, ne);
        }

        // Also label EVERY immediate choice (a crossroad), so the player can compare both
        // paths, not just the recommended one. A frontier node is the next of its type
        // (seen = 0). We do NOT label "?" (unknown) nodes: they have no fixed content (they
        // resolve by odds on entry - event / small fight / shop / treasure), so any specific
        // name would be a confidently-wrong guess. The next event still rides to the
        // spectator route panel as a sequence, where it isn't pinned to a node.
        foreach (var f in frontier)
            if (!labels.ContainsKey(f) && LabelFor(PointType(f), 0, 0, route) is { } n)
                labels[f] = n;

        var seenRings = new HashSet<ulong>();
        foreach (var model in green)
            if (byModel.TryGetValue(model, out var e))
            {
                seenRings.Add(e.Ui.GetInstanceId());
                UpdateRing(e.Ui, RouteGreen);
            }

        // Render every label (recommended route + all crossroad choices).
        var seenLabels = new HashSet<ulong>();
        foreach (var (model, name) in labels)
            if (byModel.TryGetValue(model, out var e) && seenLabels.Add(e.Ui.GetInstanceId()))
                UpdateLabel(e.Ui, name);

        // Yellow: challenging nodes reachable ahead, off the recommended route (the route
        // already steers you; yellow flags the tough spots it routes around).
        foreach (var model in Reachable(frontier))
        {
            if (green.Contains(model) || !NodeChallenging(model, actIndex)) continue;
            if (!byModel.TryGetValue(model, out var e)) continue;
            if (seenRings.Add(e.Ui.GetInstanceId())) UpdateRing(e.Ui, ChallengeYellow);
        }

        Prune(_rings, seenRings);
        Prune(_labels, seenLabels);
    }

    // The specific encounter name for a combat node at a given seen-count (monster/elite/
    // boss); null for non-combat nodes or when the route data isn't loaded.
    private static string? LabelFor(string? type, int mSeen, int eSeen, ActRoute? route) => type switch
    {
        "monster" => NameAt(route?.Monsters, mSeen),
        "elite" => NameAt(route?.Elites, eSeen),
        "boss" => route?.Boss?.Name,
        _ => null,
    };

    private static string? NameAt(List<EncounterRef>? seq, int idx)
        => seq != null && idx >= 0 && idx < seq.Count ? seq[idx].Name : null;

    private static string? PointType(object model)
        => Reflect.GetString(model, "PointType")?.ToLowerInvariant();

    // Every node reachable from the immediate choices, walking the act DAG forward.
    private static IEnumerable<object> Reachable(List<object> frontier)
    {
        var seen = new HashSet<object>();
        var queue = new Queue<object>(frontier);
        while (queue.Count > 0)
        {
            var m = queue.Dequeue();
            if (!seen.Add(m)) continue;
            foreach (var c in ChildrenOf(m)) queue.Enqueue(c);
        }
        return seen;
    }

    private static bool NodeChallenging(object model, int actIndex)
        => DangerCost(model, actIndex) >= ChallengeThreshold;

    // Constant across one map evaluation.
    private readonly record struct RouteCtx(ActRoute? Route, int ActIndex, int Gold, double HpPct);

    // Min cost of entering `model` (as the (mSeen+1)th monster / (eSeen+1)th elite on the
    // path) and taking the best line to the act end. Memoized on (node, mSeen, eSeen): a
    // combat node's cost depends on which upcoming encounter it resolves to.
    private double RouteCost(object model, int mSeen, int eSeen,
        Dictionary<(object, int, int), double> memo, RouteCtx ctx)
    {
        var key = (model, mSeen, eSeen);
        if (memo.TryGetValue(key, out var cached)) return cached;
        memo[key] = 0; // DAG, but guard re-entry anyway

        var type = PointType(model);
        var cost = NodeRouteCost(model, type, mSeen, eSeen, ctx);
        var nm = mSeen + (type == "monster" ? 1 : 0);
        var ne = eSeen + (type == "elite" ? 1 : 0);

        var bestChild = double.MaxValue;
        foreach (var child in ChildrenOf(model))
            bestChild = Math.Min(bestChild, RouteCost(child, nm, ne, memo, ctx));

        var total = cost + (bestChild == double.MaxValue ? 0 : bestChild);
        memo[key] = total;
        return total;
    }

    // A node's route cost: the SPECIFIC encounter's danger when we know the exact fight
    // (per-encounter community stats), else the per-type danger; plus the resource-aware
    // opportunity cost.
    private static double NodeRouteCost(object model, string? type, int mSeen, int eSeen, RouteCtx ctx)
    {
        var enc = type switch
        {
            "monster" => EncounterAt(ctx.Route?.Monsters, mSeen),
            "elite" => EncounterAt(ctx.Route?.Elites, eSeen),
            "boss" => ctx.Route?.Boss,
            _ => null,
        };
        var specific = enc != null ? CommunityStats.Encounter(enc.Id) : null;
        var danger = specific is { } d ? d.AvgDmgPct + d.DeathRate * 2.0 : DangerCost(model, ctx.ActIndex);
        return danger + OpportunityCost(model, ctx.Gold, ctx.HpPct);
    }

    private static EncounterRef? EncounterAt(List<EncounterRef>? seq, int idx)
        => seq != null && idx >= 0 && idx < seq.Count ? seq[idx] : null;

    // Pure community HP danger for a node type: avg HP% lost + death rate weighted in (each
    // death-rate point counts double). Non-combat types carry no danger. The per-type
    // fallback for the route, and the basis for the yellow challenge mark.
    private static double DangerCost(object model, int actIndex)
    {
        var type = Reflect.GetString(model, "PointType")?.ToLowerInvariant();
        if (type is not ("monster" or "elite" or "boss" or "ancient" or "unknown")) return 0;
        return CommunityStats.Danger(actIndex, type) is { } d ? d.AvgDmgPct + d.DeathRate * 2.0 : 0;
    }

    // A node is wasteful when it can't help the current player: a shop with no gold to
    // spend, a rest at near-full HP. Combats and treasure carry no opportunity cost (they
    // are the point of the run). Uses the CURRENT gold/HP, an approximation that
    // re-evaluates whenever the map opens, by which point gold/HP have updated.
    private static double OpportunityCost(object model, int gold, double hpPct)
    {
        var type = Reflect.GetString(model, "PointType")?.ToLowerInvariant();
        return type switch
        {
            // Under a card's price (~75) a shop is wasted; a little gold makes it mildly
            // useful; flush -> free. Keeps the route from sending a broke player to shops.
            "shop" or "merchant" => gold < 75 ? 8.0 : gold < 150 ? 3.0 : 0.0,
            // Near-full HP a rest's heal is wasted; the upgrade option keeps it from free.
            "restsite" or "rest" or "campfire" => hpPct > 0.85 ? 4.0 : 0.0,
            _ => 0.0,
        };
    }

    private static IEnumerable<object> ChildrenOf(object model)
    {
        if (Reflect.GetMember(model, "Children") is IEnumerable kids)
            foreach (var k in kids)
                if (k != null) yield return k;
    }

    // The traveled node with the highest row = where the player stands.
    private static object? DeepestTraveled(Dictionary<object, (Control Ui, string State)> byModel)
    {
        object? deepest = null;
        var deepestRow = int.MinValue;
        foreach (var (model, v) in byModel)
        {
            if (v.State != "Traveled") continue;
            var row = Reflect.GetMember(Reflect.GetMember(model, "coord"), "row") is int r ? r : 0;
            if (row > deepestRow) { deepestRow = row; deepest = model; }
        }
        return deepest;
    }

    private void UpdateRing(Control node, Color color)
    {
        var key = node.GetInstanceId();
        if (!_rings.TryGetValue(key, out var ring) || !GodotObject.IsInstanceValid(ring))
        {
            var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), DrawCenter = false };
            style.SetBorderWidthAll(3);
            style.SetCornerRadiusAll(999);
            ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            ring.AddThemeStyleboxOverride("panel", style);
            _layer.AddChild(ring);
            _rings[key] = ring;
        }

        // A node's role can change between ticks (route reroutes), so recolor each update.
        if (ring.GetThemeStylebox("panel") is StyleBoxFlat sb) sb.BorderColor = color;

        const float pad = 6f;
        var rect = node.GetGlobalRect();
        ring.Position = rect.Position - new Vector2(pad, pad);
        ring.Size = rect.Size + new Vector2(pad * 2, pad * 2);
    }

    // The specific encounter name, in a small pill above the node, for the recommended route.
    private void UpdateLabel(Control node, string text)
    {
        var key = node.GetInstanceId();
        if (!_labels.TryGetValue(key, out var label) || !GodotObject.IsInstanceValid(label))
        {
            var pill = new StyleBoxFlat { BgColor = new Color(0.06f, 0.07f, 0.09f, 0.82f) };
            pill.SetCornerRadiusAll(4);
            pill.ContentMarginLeft = 5; pill.ContentMarginRight = 5;
            pill.ContentMarginTop = 0; pill.ContentMarginBottom = 1;
            label = new RichTextLabel
            {
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            label.AddThemeStyleboxOverride("normal", pill);
            label.AddThemeFontSizeOverride("normal_font_size", 13);
            _layer.AddChild(label);
            _labels[key] = label;
        }

        label.Text = $"[center][color=#cfe8cf]{text}[/color][/center]";
        var size = label.Size;
        var rect = node.GetGlobalRect();
        label.Position = new Vector2(
            rect.Position.X + rect.Size.X * 0.5f - size.X * 0.5f,
            rect.Position.Y - size.Y - 3f); // above the node, clear of the ring
    }

    private static void Prune<T>(Dictionary<ulong, T> live, HashSet<ulong> seen) where T : Control
    {
        if (live.Count == 0) return;
        var stale = new List<ulong>();
        foreach (var k in live.Keys)
            if (!seen.Contains(k)) stale.Add(k);
        foreach (var k in stale)
            if (live.Remove(k, out var c)) c.QueueFree();
    }

    // The act's upcoming events in order, in a small corner pill. The sequence is real even
    // though we can't pin an event to a specific "?" node, so this is honest where a node
    // label would be a guess. Hidden when there are no upcoming events.
    private void UpdateEventBanner(ActRoute? route)
    {
        var events = route?.Events;
        if (!SpireCodexConfig.ShowUpcomingEvents || events == null || events.Count == 0)
        {
            HideEventBanner();
            return;
        }

        if (_eventBanner == null || !GodotObject.IsInstanceValid(_eventBanner))
        {
            var pill = new StyleBoxFlat { BgColor = new Color(0.06f, 0.07f, 0.09f, 0.88f) };
            pill.SetCornerRadiusAll(5);
            pill.ContentMarginLeft = 10; pill.ContentMarginRight = 10;
            pill.ContentMarginTop = 6; pill.ContentMarginBottom = 6;
            _eventBanner = new RichTextLabel
            {
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            _eventBanner.AddThemeStyleboxOverride("normal", pill);
            _eventBanner.AddThemeFontSizeOverride("normal_font_size", 14);
            _layer.AddChild(_eventBanner);
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("[color=#ffd34d][b]Upcoming events[/b][/color]");
        for (var i = 0; i < events.Count && i < 3; i++)
            sb.Append($"\n[color={(i == 0 ? "#e8e8e8" : "#9aa0a8")}]{events[i].Name}[/color]");
        _eventBanner.Text = sb.ToString();
        // Anchor to the bottom-right corner (size is known after FitContent lays out the
        // text; self-corrects within a frame of the text changing).
        var vp = _eventBanner.GetViewportRect().Size;
        var size = _eventBanner.Size;
        _eventBanner.Position = new Vector2(vp.X - size.X - 24f, vp.Y - size.Y - 24f);
        _eventBanner.Visible = true;
    }

    private void HideEventBanner()
    {
        if (_eventBanner != null && GodotObject.IsInstanceValid(_eventBanner)) _eventBanner.Visible = false;
    }

    // Clear the per-node route guidance (rings + labels) but leave the event banner, which
    // is valid whenever the map is open even if there's no navigable frontier this frame.
    private void ClearRoute()
    {
        if (_rings.Count > 0)
        {
            foreach (var r in _rings.Values) if (GodotObject.IsInstanceValid(r)) r.QueueFree();
            _rings.Clear();
        }
        if (_labels.Count > 0)
        {
            foreach (var l in _labels.Values) if (GodotObject.IsInstanceValid(l)) l.QueueFree();
            _labels.Clear();
        }
    }

    // The game's own "is the map the active screen" test. NMapScreen hides its own UX when it
    // isn't current (ActiveScreenContext.IsCurrent at NMapScreen.cs), which is exactly when
    // another screen covers it (card library, inspect card/relic, modals) while the map's
    // Visible/IsOpen stay true underneath. Returns true on any read failure so we never over-hide
    // (the IsOpen gate above still handles the leave-the-map case).
    private static bool MapIsFront(Node mapScreen)
    {
        try
        {
            var ascType = mapScreen.GetType().Assembly.GetType(
                "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext");
            var asc = Reflect.GetStatic(ascType, "Instance");
            return Reflect.CallWith(asc, "IsCurrent", mapScreen) is not false; // false only when positively not current
        }
        catch { return true; }
    }

    private void ClearAll()
    {
        // The map session is over (closed / not in a run): re-arm so the next map-open treats
        // its first travel choices as fresh, the commit detector re-baselines, and a genuine
        // mid-room peek still gets the fallback.
        _sawTravelable = false;
        _traveledCount = -1;
        _prevTravelable = null;
        _picked = false;
        ClearRoute();
        HideEventBanner();
    }
}
