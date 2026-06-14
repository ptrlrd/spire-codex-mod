using System.Collections.Generic;
using Godot;

namespace SpireCodex.Core;

// Finds every card currently shown on a visible overlay screen (card rewards, choose-a-card,
// Neow bundles/packs, pack previews, inspect, pile views), with its bare id and on-screen
// bounds. Screens differ in structure, so instead of hardcoding node paths we walk the visible
// overlay screens and match by type:
//   - NCardHolder-derived nodes (NGridCardHolder on reward/choose rows, NPreviewCardHolder in
//     bundle previews): id from holder.CardModel, visuals from holder.CardNode.
//   - bare NCard nodes (inside NCardBundle on the two-packs screen): id from card.Model.
// The combat hand lives under the run scene, not OverlayScreensContainer, so it never matches.
internal static class CardScan
{
    private const string OverlayContainerPath = "RootSceneContainer/Run/GlobalUi/OverlayScreensContainer";

    // PlateAbove: render the rating plate on the card's TOP edge instead of the bottom
    // (shop cards show their gold price under the card, which the plate would cover).
    public readonly record struct FoundCard(Control Node, string Id, bool PlateAbove = false);

    // The overlay-screens container under the Game node, or null outside a run.
    public static Node? FindOverlayContainer(ref Node? cachedGame)
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null) return null;
        if (cachedGame != null && !GodotObject.IsInstanceValid(cachedGame)) cachedGame = null;
        if (cachedGame == null)
        {
            foreach (var child in tree.Root.GetChildren())
                if (child.Name.ToString() == "Game") { cachedGame = child; break; }
        }
        return cachedGame?.GetNodeOrNull(OverlayContainerPath);
    }

    // Only the TOPMOST visible overlay screen that actually shows cards gets plates: our
    // badge layer draws above the game's whole UI, so plates for a screen underneath would
    // clip through whatever is stacked on top of it (e.g. an inspect view over a reward row).
    // Walked top-down; a visible but card-less child (backdrops etc.) doesn't blank the rest.
    public static List<FoundCard> CollectSelectionCards(Node overlayContainer)
    {
        var result = new List<FoundCard>();
        var children = overlayContainer.GetChildren();
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (children[i] is not Control { Visible: true } c) continue;
            Walk(c, 0, result);
            if (result.Count > 0) return result; // topmost card-bearing screen wins
        }
        return result;
    }

    // Whether an overlay SCREEN is stacked over the room scene (used to hide the shop's
    // plates so they don't clip through it). Only actual screens count — the container can
    // hold other always-visible children — and merchant-owned screens never block the shop.
    public static bool OverlayBlocksShop(Node overlayContainer)
    {
        foreach (var child in overlayContainer.GetChildren())
        {
            if (child is not Control { Visible: true } c) continue;
            var n = c.GetType().Name;
            if (n.Contains("Screen") && !n.Contains("Merchant")) return true;
        }
        return false;
    }

    // Global rects of the game's active hover-tip boxes (NHoverTipSet parts under
    // NGame.HoverTipsContainer). Plates hide when they'd overlap one — our badge layer
    // draws above the game's whole UI, tooltips included.
    public static List<Rect2> ActiveHoverTipRects(Node? game)
    {
        var rects = new List<Rect2>();
        if (Reflect.GetMember(game, "HoverTipsContainer") is not Node container) return rects;
        foreach (var set in container.GetChildren())
        {
            if (set is not Control sc || !sc.Visible) continue;
            foreach (var part in set.GetChildren())
                if (part is Control pc && pc.Visible && pc.Size is { X: > 8, Y: > 8 })
                    rects.Add(pc.GetGlobalRect());
        }
        return rects;
    }

    // Diagnostic: the visible overlay children's type names, so plate gating is debuggable.
    public static string VisibleOverlayDescription(Node overlayContainer)
    {
        var names = new List<string>();
        foreach (var child in overlayContainer.GetChildren())
            if (child is Control { Visible: true } c)
                names.Add(c.GetType().Name);
        return string.Join(",", names);
    }

    private static void Walk(Node node, int depth, List<FoundCard> result)
    {
        if (depth > 12) return;
        foreach (var child in node.GetChildren())
        {
            if (child is Control c)
            {
                if (!c.Visible) continue;
                if (IsTypeInChain(c, "NCardHolder"))
                {
                    var card = Reflect.GetMember(c, "CardNode") as Control ?? c;
                    var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(c, "CardModel"), "Id"));
                    if (id != null) result.Add(new FoundCard(card, id));
                    continue; // the holder's inner NCard would double-count
                }
                if (IsTypeInChain(c, "NCard"))
                {
                    var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(c, "Model"), "Id"));
                    if (id != null) result.Add(new FoundCard(c, id));
                    continue;
                }
            }
            Walk(child, depth + 1, result);
        }
    }

    // The card's on-screen rect, taken from its frame texture; falls back to a union of the
    // card's visible TextureRect descendants. (NCards are zero-size Controls whose visuals are
    // children; the card origin is its centre.)
    public static Rect2? CardBounds(Control card)
    {
        if (Reflect.GetMember(card, "_frame") is Control frame && GodotObject.IsInstanceValid(frame))
        {
            var r = frame.GetGlobalRect();
            if (r.Size is { X: > 4, Y: > 4 }) return r;
        }

        var union = new Rect2();
        var any = false;
        UnionDescendants(card, ref union, ref any);
        return any ? union : null;
    }

    private static void UnionDescendants(Node node, ref Rect2 union, ref bool any)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Control c && c.Visible && c.Size is { X: > 4, Y: > 4 })
            {
                var r = c.GetGlobalRect();
                union = any ? union.Merge(r) : r;
                any = true;
            }
            UnionDescendants(child, ref union, ref any);
        }
    }

    private static bool IsTypeInChain(object o, string typeName)
    {
        for (var t = o.GetType(); t != null; t = t.BaseType)
            if (t.Name == typeName) return true;
        return false;
    }

    // --- Shop ------------------------------------------------------------------------
    // The merchant UI (NMerchantInventory) is a room scene, NOT an overlay screen, so it
    // needs its own scan root. Finding it walks the Game subtree once per shop visit
    // (callers gate on screen == "merchant"), then the cached node is re-walked per tick.
    private static Node? _shop;

    public static List<FoundCard> CollectShopCards(Node? game)
    {
        var result = new List<FoundCard>();
        if (game == null) return result;
        if (_shop != null && (!GodotObject.IsInstanceValid(_shop) || !_shop.IsInsideTree()))
            _shop = null;
        _shop ??= FindByTypeName(game, "NMerchantInventory", 0);
        if (_shop == null) return result;
        WalkShop(_shop, 0, result);
        return result;
    }

    private static void WalkShop(Node node, int depth, List<FoundCard> result)
    {
        if (depth > 10) return;
        foreach (var child in node.GetChildren())
        {
            if (child is Control c && IsTypeInChain(c, "NMerchantCard"))
            {
                if (!c.Visible) continue;
                var card = Reflect.GetMember(c, "_cardNode") as Control;
                var id = Ids.Bare(Reflect.GetString(Reflect.GetMember(card, "Model"), "Id"));
                if (card != null && id != null) result.Add(new FoundCard(card, id, PlateAbove: true));
                continue;
            }
            WalkShop(child, depth + 1, result);
        }
    }

    private static Node? FindByTypeName(Node node, string typeName, int depth)
    {
        if (depth > 12) return null;
        foreach (var child in node.GetChildren())
        {
            if (IsTypeInChain(child, typeName)) return child;
            var found = FindByTypeName(child, typeName, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    // Cached lookup of a game node by type name (e.g. the map screen). The cache is the
    // caller's, so each consumer revalidates and re-searches independently.
    public static Node? FindCachedByType(Node? root, string typeName, ref Node? cache)
    {
        if (cache != null && (!GodotObject.IsInstanceValid(cache) || !cache.IsInsideTree()))
            cache = null;
        if (cache == null && root != null)
            cache = FindByTypeName(root, typeName, 0);
        return cache;
    }
}

// "CARD.GRAVEBLAST (57340095)" -> "GRAVEBLAST": strip the trailing instance id and the
// type prefix so the value matches /api/runs/scores keys and CDN urls.
internal static class Ids
{
    internal static string? Bare(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var s = raw;
        var space = s.IndexOf(' ');
        if (space >= 0) s = s.Substring(0, space);
        var dot = s.IndexOf('.');
        if (dot >= 0) s = s.Substring(dot + 1);
        return s;
    }
}
