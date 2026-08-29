using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SpireCodex.Api;
using SpireCodex.Core;

namespace SpireCodex.Ui;

// A tier · win% plate centred just below each card on any card-selection overlay screen
// (rewards, choose-a-card, Neow bundles/packs, pack previews, pile views). Card discovery is
// type-based via Core/CardScan, so new screen layouts work without hardcoded node paths.
//
// The richer hover stats are rendered by the game's own native hover-tip widget (see
// Core/NativeHoverTips.cs); this class only owns the persistent at-a-glance plate.
public partial class CardRewardHints : Node
{
    private double _accum;
    private Node? _game;
    private Theme? _gameTheme;

    // Diagnostics: log a one-line summary at most once per second, plus whenever it
    // changes, so we can see at runtime why a plate does or does not paint.
    private double _diagAccum;
    private string _lastDiag = "";

    private CanvasLayer _badgeLayer = null!;
    private readonly Dictionary<ulong, RichTextLabel> _badges = new();

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; card-reward hints not started");
            return;
        }
        var n = new CardRewardHints { Name = "SpireCodexCardHints" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, n);
        MainFile.Logger.Info("card-reward hints started");
    }

    public override void _Ready()
    {
        // (font applied per-badge via Skin.ApplyFont below)

        // High layer so plates draw above the card-selection screen itself, which sits on
        // the game's overlay-screen canvas (above the normal layer-0 UI).
        _badgeLayer = new CanvasLayer { Layer = 200 };
        AddChild(_badgeLayer);
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < 0.08) return;
        _accum = 0;
        _diagAccum += 0.08;

        if (!SpireCodexConfig.ShowCardRewardHints) { ClearBadges(); return; }

        var container = CardScan.FindOverlayContainer(ref _game);
        if (container == null)
        {
            DiagThrottled($"container=null game={(_game != null)} scoresLoaded={CodexScores.Loaded}");
            ClearBadges();
            return;
        }

        var cards = CardScan.CollectSelectionCards(container, out var screenType);

        // No cards on the topmost screen? It may be a relic choice (the ancient 3-relic offer).
        // Plates there turn a hover-and-remember comparison into an at-a-glance one.
        var relicScreen = false;
        if (cards.Count == 0)
        {
            cards = CardScan.CollectSelectionRelics(container);
            relicScreen = cards.Count > 0;
        }

        // The shop is a room scene, not an overlay screen; scan it while we're in one so
        // shop cards get plates (and the best-buy flag) too — but not while an overlay
        // screen is stacked above the shop, or its plates would clip through that overlay.
        var inShop = Producer.LiveStateProducer.Latest?.Screen == "merchant";
        if (cards.Count == 0 && inShop && !CardScan.OverlayBlocksShop(container))
            cards.AddRange(CardScan.CollectShopCards(_game));

        if (cards.Count == 0)
        {
            DiagThrottled("cards=0"
                + (inShop ? $" ovl=[{CardScan.VisibleOverlayDescription(container)}]" : ""));
            ClearBadges();
            return;
        }

        // A best pick only means anything when the screen is OFFERING cards. On a deck
        // screen the same badge would be read as "remove this" or "upgrade this", and
        // Codex Elo answers neither question: it's how good the card is, which on a removal
        // screen is precisely backwards. Shop cards are a draft too (which to buy).
        var isDraft = relicScreen || inShop || CardScan.IsDraftScreen(screenType);

        var bestId = !isDraft ? null : relicScreen ? BestRelic(cards) : BestPick(cards);
        if (!isDraft) ScreenDiag(screenType);

        // Skipping competes for the same decision, and its Elo comes from the same fit, so
        // it's compared directly against the best card on offer. When skip wins, nothing on
        // screen is worth taking and no card is flagged best.
        var skip = relicScreen || !isDraft ? null : CodexScores.Skip;
        var skipWins = skip is { } sk && bestId != null
            && CodexScores.Card(bestId)?.Elo is { } bestElo && sk.Elo > bestElo;
        RewardContext.SkipWins = skipWins;
        if (skipWins) bestId = null;

        RewardContext.BestRelicId = relicScreen ? bestId : null;
        RewardContext.BestCardId = relicScreen ? null : bestId;

        var mouse = GetViewport().GetMousePosition();
        var seen = new HashSet<ulong>();
        var bounded = 0; var withScore = 0;

        // Active game tooltips: plates that would overlap one hide instead of clipping it.
        var tipRects = CardScan.ActiveHoverTipRects(_game);

        foreach (var found in cards)
        {
            var bounds = CardScan.CardBounds(found.Node);
            if (bounds is not { } rect) continue;
            bounded++;
            if (Score(found) is { Picks: > 0 }) withScore++;

            // While this card is hovered its native hover-tip shows the full stats and the
            // enlarged card sits over the plate, so hide the plate to avoid it clipping
            // through the tip.
            var hovered = rect.HasPoint(mouse);
            seen.Add(found.Node.GetInstanceId());
            UpdateBadge(found.Node.GetInstanceId(), found, rect, hovered, found.Id == bestId, found.PlateAbove, tipRects);
        }

        // The skip verdict hangs off the skip button itself, exactly like a card's plate,
        // and is drawn EVERY time the button is on screen. Showing it only when skipping
        // won made the advice look like it had appeared out of nowhere, and gave no way to
        // tell "don't skip" from "the mod has no opinion".
        if (isDraft && !relicScreen)
        {
            var skipBtn = CardScan.FindSkipButton(container);
            if (skip != null && skipBtn != null)
            {
                var r = skipBtn.GetGlobalRect();
                if (r.Size is not { X: > 4, Y: > 4 } && CardScan.CardBounds(skipBtn) is { } fb) r = fb;
                if (r.Size is { X: > 4, Y: > 4 })
                {
                    UpdateSkipBadge(r, skip, skipWins, tipRects);
                    seen.Add(SkipBadgeKey);
                }
            }
            SkipDiag(skip != null, skipBtn, container, screenType);
        }

        DiagThrottled($"cards={cards.Count} bounded={bounded} withScore={withScore} badges={_badges.Count}"
            + (inShop ? $" ovl=[{CardScan.VisibleOverlayDescription(container)}]" : ""));
        PruneBadges(seen);
    }

    // The community numbers for one found item, from whichever score set it belongs to.
    private static EntityScore? Score(CardScan.FoundCard f) =>
        f.IsRelic ? CodexScores.Relic(f.Id) : CodexScores.Card(f.Id);

    // Best relic on a relic-choice screen. Relics carry no Elo (they're never a card-reward
    // draft), so this is the highest Codex Score, tie-broken by sample size — the same
    // comparison a player makes by hovering each one in turn and remembering the win rates.
    private static string? BestRelic(List<CardScan.FoundCard> relics)
    {
        string? best = null;
        double bestScore = double.MinValue, bestPicks = -1;
        foreach (var f in relics)
        {
            if (CodexScores.Relic(f.Id) is not { Picks: > 0 } sc) continue;
            if (sc.Score > bestScore || (sc.Score == bestScore && sc.Picks > bestPicks))
            {
                best = f.Id; bestScore = sc.Score; bestPicks = sc.Picks;
            }
        }
        return best;
    }

    // Best pick = highest Codex Elo among the offered cards (revealed community preference);
    // when none of them carries an Elo, fall back to the Codex Score. Tie-break by picks.
    // Only Elo-rated cards compete when any Elo exists (the scales aren't comparable).
    private static string? BestPick(List<CardScan.FoundCard> cards)
    {
        var anyElo = false;
        foreach (var f in cards)
            if (CodexScores.Card(f.Id) is { Picks: > 0, Elo: not null }) { anyElo = true; break; }

        string? best = null;
        double bestKey = double.MinValue, bestPicks = -1;
        foreach (var f in cards)
        {
            if (CodexScores.Card(f.Id) is not { Picks: > 0 } sc) continue;
            double key;
            if (anyElo)
            {
                if (sc.Elo is not { } elo) continue;
                key = elo;
            }
            else key = sc.Score;
            if (key > bestKey || (key == bestKey && sc.Picks > bestPicks))
            {
                best = f.Id; bestKey = key; bestPicks = sc.Picks;
            }
        }
        return best;
    }

    private static readonly Color BestGold = new(1.00f, 0.827f, 0.302f);

    // Reserved badge key for the skip banner, which hangs off the card row rather than any
    // one node, so it has no instance id of its own.
    private const ulong SkipBadgeKey = ulong.MaxValue;

    // One line, only when the outcome changes, into the scores log (which is on in release,
    // unlike DiagThrottled). Says which of the two halves is missing when no plate appears:
    // the community data, or the button node itself.
    // Names any screen we show plates on but withhold a best pick from. If a genuine draft
    // screen ever turns up here, it just needs adding to CardScan.DraftScreens.
    private static string _lastScreenDiag = "";
    private static void ScreenDiag(string? screen)
    {
        var msg = $"no best-pick on screen={screen ?? "?"} (not a reward draft)";
        if (msg == _lastScreenDiag) return;
        _lastScreenDiag = msg;
        CodexScores.DiagPublic(msg);
    }

    private static string _lastSkipDiag = "";
    private static void SkipDiag(bool hasData, Control? btn, Node container, string? screen)
    {
        var msg = $"skip plate: screen={screen ?? "?"} data={hasData} button={(btn != null ? btn.GetType().Name : "NOT FOUND")}";
        if (btn == null) msg += $" | buttons on screen=[{CardScan.DescribeButtons(container)}]";
        if (msg == _lastSkipDiag) return;
        _lastSkipDiag = msg;
        CodexScores.DiagPublic(msg);
    }

    // Left+right content margins on the plate stylebox (MakePlate uses 8 each), so the
    // measured text width has room and the glyphs are not flush against the border.
    private const float PlatePadding = 18f;

    private static readonly Color SkipYes = new(0.525f, 0.878f, 0.541f); // A-tier green
    private static readonly Color SkipNo = new(0.878f, 0.541f, 0.525f);  // F-tier red

    // The plate under the skip button. Green with the community skip rate for this act when
    // skipping beats everything offered, red "don't skip this round" when it doesn't.
    private void UpdateSkipBadge(Rect2 row, SkipScore skip, bool skipWins, List<Rect2> tipRects)
    {
        if (!_badges.TryGetValue(SkipBadgeKey, out var badge) || !GodotObject.IsInstanceValid(badge))
        {
            badge = new RichTextLabel
            {
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            badge.AddThemeStyleboxOverride("normal", MakePlate());
            badge.AddThemeFontSizeOverride("normal_font_size", 19);
            Skin.ApplyFont(badge);
            _badgeLayer.AddChild(badge);
            _badges[SkipBadgeKey] = badge;
        }

        var accent = skipWins ? SkipYes : SkipNo;
        if (badge.GetThemeStylebox("normal") is StyleBoxFlat plate)
        {
            plate.BorderColor = accent;
            plate.SetBorderWidthAll(skipWins ? 3 : 2);
            plate.CornerRadiusTopLeft = 0; plate.CornerRadiusTopRight = 0;
            plate.CornerRadiusBottomLeft = 7; plate.CornerRadiusBottomRight = 7;
            plate.BorderWidthTop = 0; // merges into the button's bottom edge, like a card plate
        }

        // Act-specific rate: skipping runs 20% in Act 1 and 43% by Act 3, so the flat
        // average would misrepresent the case at both ends.
        var act = Producer.LiveStateProducer.Latest?.Act ?? 0;
        var rate = act > 0 ? skip.RateForAct(act) : skip.SkipRate;
        var hex = skipWins ? "#86e08a" : "#e08a86";
        badge.Text = skipWins
            ? $"[center][color={hex}][b]{Loc.F("plate_skip_rate", rate)}[/b][/color][/center]"
            : $"[center][color={hex}][b]{Loc.T("plate_skip_dont")}[/b][/color][/center]";

        // Size to the TEXT, not to the button: the skip affordance is a wide control and
        // stretching the plate across it left a long empty box around a dozen characters.
        //
        // GetContentWidth() reflects the text we just set, whereas Size is still last
        // frame's. That mismatch is what threw the centring off: the box was POSITIONED
        // from one width and DRAWN at another, so it drifted whenever the verdict flipped
        // between the short green label and the longer red one. Deriving the width once
        // and using that same value for both the size and the position keeps it centred
        // no matter which label is showing.
        var content = badge.GetContentWidth() + PlatePadding;
        var width = Mathf.Max(content, 110f);
        badge.CustomMinimumSize = new Vector2(width, 0);
        badge.Size = new Vector2(width, badge.Size.Y);
        badge.Position = new Vector2(
            Mathf.Round(row.Position.X + row.Size.X * 0.5f - width * 0.5f),
            Mathf.Round(row.Position.Y + row.Size.Y - 2f));

        var blocked = false;
        var r = new Rect2(badge.Position, new Vector2(width, badge.Size.Y));
        foreach (var t in tipRects)
            if (t.Intersects(r)) { blocked = true; break; }
        badge.Visible = !blocked;
    }

    private void UpdateBadge(ulong key, CardScan.FoundCard found, Rect2 cardRect, bool hovered, bool isBest, bool above, List<Rect2> tipRects)
    {
        var sc = Score(found);
        if (sc == null || sc.Picks == 0)
        {
            if (_badges.Remove(key, out var dead)) dead.QueueFree();
            return;
        }

        // Tier letters are Codex Score grades, matching the website. Elo remains the
        // preference signal used for Best Pick, but it has no S-F grade on the metrics table.
        var tier = Ranks.Tier(sc.Score);
        var tierColor = isBest ? BestGold : TierColor(tier);

        if (!_badges.TryGetValue(key, out var badge) || !GodotObject.IsInstanceValid(badge))
        {
            badge = new RichTextLabel
            {
                BbcodeEnabled = true,
                FitContent = true,
                ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            badge.AddThemeStyleboxOverride("normal", MakePlate());
            badge.AddThemeFontSizeOverride("normal_font_size", 19);
            Skin.ApplyFont(badge);
            _badgeLayer.AddChild(badge);
            _badges[key] = badge;
        }

        // Tier-coloured frame so the rating itself carries the accent. The plate hangs flush
        // off the card's bottom edge (square top corners) like a base extending from it —
        // or, when `above` (shop: the price sits under the card), mirrored on the top edge.
        // The edge touching the card has NO border so the plate merges into the card frame
        // instead of reading as a separate box. The best pick gets a thicker gold frame.
        if (badge.GetThemeStylebox("normal") is StyleBoxFlat plate)
        {
            plate.BorderColor = tierColor;
            plate.SetBorderWidthAll(isBest ? 3 : 2);
            plate.CornerRadiusTopLeft = above ? 7 : 0;
            plate.CornerRadiusTopRight = above ? 7 : 0;
            plate.CornerRadiusBottomLeft = above ? 0 : 7;
            plate.CornerRadiusBottomRight = above ? 0 : 7;
            plate.BorderWidthTop = above ? (isBest ? 3 : 2) : 0;
            plate.BorderWidthBottom = above ? 0 : (isBest ? 3 : 2);
        }

        // Tier letter (Elo-based when rated) + win%. The raw Elo number lives in the
        // hovertip only — on the plate it stretched the box.
        var best = isBest ? $"[color=#ffd34d][b]{Loc.T("plate_best")}[/b][/color]  " : "";
        badge.Text =
            $"[center]{best}[color={TierHex(tier)}][b]{tier}[/b][/color]" +
            $"  [color=#5b636c]·[/color]  [color=#eae5d8]{sc.WinRate:0}%[/color][/center]";

        // A plate roughly the width of the card art, centred, abutting the card edge so
        // its border merges with the card frame (slight overlap).
        var width = Mathf.Max(cardRect.Size.X * 0.78f, 96f);
        badge.CustomMinimumSize = new Vector2(width, 0);
        badge.Size = new Vector2(width, badge.Size.Y);
        var x = cardRect.Position.X + cardRect.Size.X * 0.5f - width * 0.5f;
        var y = above
            ? cardRect.Position.Y - badge.Size.Y + 2f
            : cardRect.Position.Y + cardRect.Size.Y - 2f;
        badge.Position = new Vector2(x, y);

        // Hide while this card is hovered (the tip shows the full stats) and whenever the
        // plate would overlap an active game tooltip (we draw above the tooltip layer).
        var blocked = false;
        var plateRect = new Rect2(badge.Position, new Vector2(width, badge.Size.Y));
        foreach (var r in tipRects)
            if (r.Intersects(plateRect)) { blocked = true; break; }
        badge.Visible = !hovered && !blocked;
    }

    private void PruneBadges(HashSet<ulong> seen)
    {
        if (_badges.Count == 0) return;
        var stale = new List<ulong>();
        foreach (var key in _badges.Keys)
            if (!seen.Contains(key)) stale.Add(key);
        foreach (var key in stale)
            if (_badges.Remove(key, out var b)) b.QueueFree();
    }

    private void ClearBadges()
    {
        // Also drop the best-pick ids: every "nothing to show" path comes through here, and a
        // stale BestRelicId would put a Best Pick line on relics in the player's own inventory
        // (the relic hovertip fires anywhere, not just on the ancient screen).
        RewardContext.BestCardId = null;
        RewardContext.BestRelicId = null;
        RewardContext.SkipWins = false;
        foreach (var b in _badges.Values) if (GodotObject.IsInstanceValid(b)) b.QueueFree();
        _badges.Clear();
    }

    // Slate plate matching the game's hover-tip look: dark fill, square top corners (so it
    // sits flush under the card) and rounded bottom. Border colour is set per-tier per frame.
    private static StyleBoxFlat MakePlate()
    {
        var plate = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.14f, 0.97f),
            BorderColor = new Color(0.32f, 0.37f, 0.43f, 1f),
        };
        plate.SetBorderWidthAll(2);
        plate.BorderWidthTop = 3;
        plate.CornerRadiusTopLeft = 0; plate.CornerRadiusTopRight = 0;
        plate.CornerRadiusBottomLeft = 7; plate.CornerRadiusBottomRight = 7;
        plate.ContentMarginLeft = 8; plate.ContentMarginRight = 8;
        plate.ContentMarginTop = 2; plate.ContentMarginBottom = 4;
        return plate;
    }

    private static Color TierColor(string tier) => tier switch
    {
        "S" => new Color(1.00f, 0.827f, 0.302f),
        "A" => new Color(0.525f, 0.878f, 0.541f),
        "B" => new Color(0.420f, 0.827f, 0.780f),
        "C" => new Color(0.910f, 0.890f, 0.839f),
        "D" => new Color(0.878f, 0.690f, 0.439f),
        _ => new Color(0.878f, 0.541f, 0.525f),
    };

    private static string TierHex(string tier) => tier switch
    {
        "S" => "#ffd34d",
        "A" => "#86e08a",
        "B" => "#6bd3c7",
        "C" => "#e8e3d6",
        "D" => "#e0b070",
        _ => "#e08a86",
    };

    // Log a summary at most once per second, and immediately whenever it changes.
    private void DiagThrottled(string msg)
    {
        if (!Config.DumpIntrospection) return; // dev-only; off in release (was 10+ temp-file writes/sec)
        var changed = msg != _lastDiag;
        if (!changed && _diagAccum < 1.0) return;
        _diagAccum = 0;
        _lastDiag = msg;
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "spire-codex-cardhints.log"),
                $"{DateTimeOffset.UtcNow:o}  {msg}\n");
        }
        catch { /* ignore */ }
    }
}
