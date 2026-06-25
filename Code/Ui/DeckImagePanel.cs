using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SpireCodex.Api;
using SpireCodex.Core;
using SpireCodex.Producer;

namespace SpireCodex.Ui;

// The companion panel (default hotkey F5, rebindable), styled 1:1 with the Spire Codex Overwolf overlay's live panel
// (design tokens copied from overlay.css). Four tabs: Current Run (live dashboard - vitals,
// build quality, combat, reward/shop/event decision help, the act's road ahead, and the full
// deck/relics/potions with Codex tiers), Leaderboard, Runs, and About. Switch tabs by clicking
// or pressing Tab while open. Godot has no web view, so this rebuilds the overlay's look
// natively. The Current Run sections all read data the mod already captures each tick (the
// live snapshot + in-memory score/community caches), so they cost no extra network.
public partial class DeckImagePanel : CanvasLayer
{
    // Overwolf overlay design tokens (src/windows/in_game/overlay.css :root).
    private static readonly Color Bg = Hex("16181d");
    private static readonly Color BgSoft = Hex("1d2027");
    private static readonly Color BgSofter = Hex("242832");
    private static readonly Color Border = Hex("2c313c");
    private static readonly Color Text = Hex("e6e6e6");
    private static readonly Color TextMuted = Hex("c8ccd5");
    private static readonly Color Accent = Hex("d7a84a"); // brand gold
    private static readonly Color Good = Hex("4ec977");
    private static readonly Color Danger = Hex("c74b4b");

    private static readonly string[] Tabs = { "Current Run", "Leaderboard", "Runs", "Settings", "About" };

    // The community stat bracket choices shown in the Settings tab selector (label + tooltip).
    private static readonly (StatBracket Bracket, string Label, string Tip)[] BracketChoices =
    {
        (StatBracket.All, "All", "All runs"),
        (StatBracket.A10, "A10", "Ascension 10"),
        (StatBracket.A10_WR30, "A10 >30%", "Ascension 10, players above 30% win rate"),
        (StatBracket.A10_WR50, "A10 >50%", "Ascension 10, players above 50% win rate"),
        (StatBracket.A10_WR75, "A10 >75%", "Ascension 10, players above 75% win rate"),
    };

    // Links (ripped from the Overwolf about page).
    private const string SiteUrl = "https://spire-codex.com";
    private const string GithubUrl = "https://github.com/ptrlrd/spire-codex";
    private const string DiscordUrl = "https://discord.gg/uged4qFufK";
    private const string OverlayUrl = "https://overwolf.com/app/ptrlrd-spire_codex";
    private const string ScoringUrl = "https://spire-codex.com/leaderboards/scoring";
    private const string PatreonUrl = "https://www.patreon.com/cw/SpireCodex";

    private PanelContainer _panel = null!;
    private bool _dragging;
    private Vector2 _dragOffset;

    // The game's stick-click ("peek") action. STS2 routes the controller through Steam Input
    // and emits this as a synthetic action; it's also the native left-stick-click binding when
    // Steam Input is off. Listening for the action (not a raw joypad button) is the only thing
    // that reaches the mod while Steam Input is active, which is the default.
    private static readonly StringName StickClickAction = "controller_joystick_press";

    // L1 / R1 bumpers (also synthetic actions under Steam Input) cycle the panel's tabs while
    // it's open — the controller mirror of the Tab key.
    private static readonly StringName BumperLeft = "controller_left_bumper";
    private static readonly StringName BumperRight = "controller_right_bumper";

    private VBoxContainer _content = null!;
    private Label _hint = null!;
    private readonly List<Button> _tabButtons = new();
    private readonly List<Button> _bracketButtons = new(); // Settings tab stat-bracket selector
    private int _tab;
    private int _loadToken; // guards against a stale async fetch populating the wrong tab

    private int _lbSub; // leaderboard sub-board: 0 = Fast Wins (A10), 1 = Daily Climb, 2 = Your Standing
    private List<BoardRun>? _a10;
    private List<BoardRun>? _daily;
    private List<RunSummary>? _wins;
    private List<RunSummary>? _runs;

    private static readonly string[] LbSub = { "Fast Wins (A10)", "Daily Climb", "Your Standing" };

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; deck image panel not started");
            return;
        }
        var p = new DeckImagePanel { Name = "SpireCodexDeckImages" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, p);
        MainFile.Logger.Info("deck image panel started");
    }

    public override void _Ready()
    {
        // Top of every mod overlay so the open F5 panel is never clipped by the card-score
        // plates (200), map hints (199), run card (150), or consent prompt (210). Harmless when
        // closed: the layer is hidden (Visible=false), so the plates render normally then.
        Layer = 220;

        // Floating panel (not docked) so the player can drag it anywhere. Fixed size; the
        // ScrollContainer inside handles overflow. Height tracks the screen with a sane cap.
        var vp = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920, 1080);
        var width = 560f;
        var height = Mathf.Clamp(vp.Y - 80f, 360f, 760f);
        var panel = new PanelContainer
        {
            Position = new Vector2(28, 40),
            CustomMinimumSize = new Vector2(width, height),
            Size = new Vector2(width, height),
        };
        _panel = panel;
        var style = new StyleBoxFlat { BgColor = Bg, BorderColor = Border };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(10);
        style.ShadowColor = new Color(0, 0, 0, 0.6f);
        style.ShadowSize = 24;
        style.ContentMarginLeft = 0; style.ContentMarginRight = 0;
        style.ContentMarginTop = 0; style.ContentMarginBottom = 0;
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 0);
        panel.AddChild(root);

        root.AddChild(BuildHeader());
        root.AddChild(BuildTabBar());

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        root.AddChild(scroll);

        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _content.AddThemeConstantOverride("separation", 10);
        var pad = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        pad.AddThemeConstantOverride("margin_left", 12);
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddThemeConstantOverride("margin_top", 4);
        pad.AddThemeConstantOverride("margin_bottom", 14);
        pad.AddChild(_content);
        scroll.AddChild(pad);

        Visible = false;
    }

    private Control BuildHeader()
    {
        var header = new PanelContainer();
        // The header doubles as the drag handle: press starts a drag, the rest is tracked in
        // _Input so the cursor can leave the bar mid-drag without dropping it.
        header.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                _dragging = true;
                _dragOffset = _panel.GetGlobalMousePosition() - _panel.GlobalPosition;
                header.AcceptEvent();
            }
        };
        var hs = new StyleBoxFlat { BgColor = BgSoft, BorderColor = Accent };
        hs.BorderWidthBottom = 2;
        hs.CornerRadiusTopLeft = 10; hs.CornerRadiusTopRight = 10;
        hs.ContentMarginLeft = 14; hs.ContentMarginRight = 14;
        hs.ContentMarginTop = 12; hs.ContentMarginBottom = 12;
        header.AddThemeStyleboxOverride("panel", hs);

        var row = new HBoxContainer();
        var brand = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        brand.AddThemeFontSizeOverride("normal_font_size", 18);
        brand.Text = "[color=#d7a84a][b]Spire[/b][/color] [color=#e6e6e6][b]Codex[/b][/color]";
        row.AddChild(brand);

        _hint = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hint.AddThemeColorOverride("font_color", TextMuted);
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        UpdateHint();
        row.AddChild(_hint);

        header.AddChild(row);
        return header;
    }

    private Control BuildTabBar()
    {
        var bar = new PanelContainer();
        var bs = new StyleBoxFlat { BgColor = BgSoft, BorderColor = Border };
        bs.BorderWidthBottom = 1;
        bs.ContentMarginLeft = 8; bs.ContentMarginRight = 8;
        bs.ContentMarginTop = 4; bs.ContentMarginBottom = 4;
        bar.AddThemeStyleboxOverride("panel", bs);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        for (var i = 0; i < Tabs.Length; i++)
        {
            var idx = i;
            var b = new Button { Text = Tabs[i], Flat = true };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Pressed += () => SetTab(idx);
            _tabButtons.Add(b);
            row.AddChild(b);
        }
        bar.AddChild(row);
        return bar;
    }

    public override void _Process(double delta)
    {
        if (Visible && !SpireCodexConfig.ShowDeckView) Visible = false;
    }

    public override void _Input(InputEvent @event)
    {
        // Drag tracking. The header's GuiInput starts the drag; motion + release are caught
        // here so the panel keeps following even when the cursor outruns the header bar.
        if (_dragging)
        {
            if (@event is InputEventMouseMotion)
            {
                DragTo(_panel.GetGlobalMousePosition() - _dragOffset);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            {
                _dragging = false;
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        // Controller toggle. STS2 routes the pad through Steam Input and emits synthetic input
        // ACTIONS (never InputEventJoypadButton), so we listen for the game's stick-click
        // action; this also matches the native joypad binding when Steam Input is off.
        if (SpireCodexConfig.OverlayPad == ControllerToggle.StickClick
            && SpireCodexConfig.ShowDeckView
            && @event.IsActionPressed(StickClickAction))
        {
            ToggleOverlay();
            GetViewport().SetInputAsHandled();
            return;
        }

        // While the panel is open, the bumpers cycle tabs (controller mirror of Tab).
        if (Visible && @event.IsActionPressed(BumperRight))
        {
            SetTab((_tab + 1) % Tabs.Length);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (Visible && @event.IsActionPressed(BumperLeft))
        {
            SetTab((_tab - 1 + Tabs.Length) % Tabs.Length);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        // Cycle tabs with Tab while the panel is open.
        if (Visible && key.Keycode == Key.Tab)
        {
            SetTab((_tab + 1) % Tabs.Length);
            GetViewport().SetInputAsHandled();
            return;
        }

        // Number keys pick the leaderboard sub-board while on that tab.
        if (Visible && _tab == 1 && key.Keycode is Key.Key1 or Key.Key2 or Key.Key3)
        {
            SetLbSub((int)(key.Keycode - Key.Key1));
            GetViewport().SetInputAsHandled();
            return;
        }

        var toggle = SpireCodexConfig.OverlayKeycode;
        if (SpireCodexConfig.ShowDeckView && toggle != Key.None && key.Keycode == toggle)
        {
            ToggleOverlay();
            GetViewport().SetInputAsHandled();
        }
    }

    // Flip the panel's visibility and, when opening, refresh the hint and drop cached feeds so
    // each open shows fresh data. Shared by the keyboard hotkey and the controller binding.
    private void ToggleOverlay()
    {
        Visible = !Visible;
        if (Visible)
        {
            UpdateHint(); // reflect the current configured hotkey (it may have been rebound)
            _a10 = null; _daily = null; _wins = null; _runs = null; // refresh each open
            SetTab(_tab);
        }
    }

    // The close-hint shows the player's actual configured deck-view hotkey, not a hardcoded
    // key, so it stays correct if they rebind it in the mod settings.
    private void UpdateHint()
    {
        var keyLabel = SpireCodexConfig.OverlayKey is var k and not HotKey.None ? k.ToString() : null;
        var padLabel = PadLabel(SpireCodexConfig.OverlayPad); // e.g. "R3/L3"
        string close;
        if (!string.IsNullOrEmpty(keyLabel) && !string.IsNullOrEmpty(padLabel))
            close = $"{keyLabel} or ({padLabel})";
        else
            close = keyLabel ?? padLabel ?? "hotkey";
        _hint.Text = $"Drag to move  ·  Tab / L1·R1 to switch  ·  {close} to close";
    }

    // Short controller-binding label for the close hint (null when the pad toggle is off).
    private static string? PadLabel(ControllerToggle t) => t switch
    {
        ControllerToggle.StickClick => "R3/L3",
        _ => null,
    };

    // Move the panel to pos, keeping it fully on-screen (uses the live viewport size so it
    // still clamps correctly after a window resize).
    private void DragTo(Vector2 pos)
    {
        var vp = GetViewport().GetVisibleRect().Size;
        var size = _panel.Size;
        pos.X = Mathf.Clamp(pos.X, 0f, Mathf.Max(0f, vp.X - size.X));
        pos.Y = Mathf.Clamp(pos.Y, 0f, Mathf.Max(0f, vp.Y - size.Y));
        _panel.GlobalPosition = pos;
    }

    private void SetTab(int tab)
    {
        _tab = tab;
        _loadToken++;
        for (var i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].AddThemeColorOverride("font_color", i == tab ? Accent : TextMuted);

        foreach (var c in _content.GetChildren()) c.QueueFree();
        switch (tab)
        {
            case 0: BuildCurrentRun(); break;
            case 1: BuildLeaderboard(); break;
            case 2: BuildRuns(); break;
            case 3: BuildSettings(); break;
            case 4: BuildAbout(); break;
        }
    }

    // ---- Current Run tab ------------------------------------------------------------

    private void BuildCurrentRun()
    {
        // Version warnings (relocated from the retired F9 overlay).
        if (ModVersion.UpdateAvailable is { } up)
            AddWarn($"Update {up} available — {ModVersion.UpdateUrl ?? "spire-codex.com"}");
        if (ModVersion.Sts2Untested)
            AddWarn("Game build is newer than tested; stats may misread.");

        var s = LiveStateProducer.Latest;
        if (s is not { Status: "ok", InRun: true })
        {
            AddEmpty(_content, "Not in a run. Start a run to see your live stats here.");
            return;
        }

        _content.AddChild(SectionHeader($"{Pretty(s.Character)} · Ascension {s.Ascension}", s.TotalFloor));
        AddCharWinRates(s.Character);

        var info = InfoLabel();
        info.Text =
            $"[color=#e6e6e6]Act {s.Act}{(string.IsNullOrEmpty(s.ActName) ? "" : $" · {s.ActName}")} · floor {s.ActFloor} · [i]{s.Screen}[/i][/color]\n" +
            $"[color=#c74b4b]HP {s.CurrentHp}/{s.MaxHp}[/color]" +
            (s.Block > 0 ? $"   [color=#5fcde0]Block {s.Block}[/color]" : "") +
            $"   [color=#d7a84a]Gold {s.Gold}[/color]" +
            (s.Combat != null ? $"   [color=#c8ccd5]Energy {s.Energy}/{s.MaxEnergy}[/color]" : "") + "\n" +
            $"[color=#c8ccd5]Deck {s.DeckSize} · Relics {s.RelicCount} · Potions {s.PotionCount}" +
            (string.IsNullOrEmpty(s.Seed) ? "" : $" · Seed {s.Seed}") + "[/color]";
        _content.AddChild(info);

        BuildDeckSummary(s.Deck);

        // Decision-help sections that only apply on the current screen.
        if (s.Combat is { } c && c.Enemies.Count > 0) BuildCombat(c);
        if (s.CardReward.Count > 0) BuildRewardHelper(s.CardReward);
        if (s.Shop is { } shop) BuildShop(shop);
        if (s.Event is { } ev) BuildEvent(ev);

        // The act's seed-determined road ahead, with per-encounter community danger.
        if (s.Route is { } route) BuildRoute(route);

        // Full live loadout (the panel is named for the deck; show the real thing).
        if (s.Deck.Count > 0) BuildDeckList(s.Deck);
        if (s.Relics.Count > 0) BuildRelicList(s.Relics);
        if (s.Potions.Count > 0) BuildPotionList(s.Potions);
    }

    // Your win rate as this character (from local .run history) next to the community's.
    private void AddCharWinRates(string? character)
    {
        if (string.IsNullOrEmpty(character)) return;
        var mine = LocalStats.For(character);
        var community = CommunityStats.Character(character);
        if (mine == null && community == null) return;

        var parts = new List<string>();
        if (mine != null)
            parts.Add($"[color=#c8ccd5]You[/color] [color=#e6e6e6][b]{mine.WinRate:0.0}%[/b][/color] [color=#9aa3b2]({mine.Runs} runs)[/color]");
        if (community != null)
            parts.Add($"[color=#c8ccd5]Community[/color] [color=#e6e6e6][b]{community.WinRate:0.0}%[/b][/color] [color=#9aa3b2]({community.Runs:N0} runs)[/color]");

        var l = InfoLabel();
        l.AddThemeFontSizeOverride("normal_font_size", 12);
        l.Text = string.Join("    ", parts);
        _content.AddChild(l);
    }

    // One-line build-quality read: average Codex tier across rated cards, plus the strongest
    // and weakest card. Derived entirely from the in-memory score cache.
    private void BuildDeckSummary(List<DeckEntry> deck)
    {
        double sum = 0; var n = 0;
        string? bestId = null; var bestScore = double.MinValue;
        string? worstId = null; var worstScore = double.MaxValue;
        foreach (var d in deck)
        {
            if (CodexScores.Card(d.Id) is not { Picks: > 0 } sc) continue;
            sum += sc.Score; n++;
            if (sc.Score > bestScore) { bestScore = sc.Score; bestId = d.Id; }
            if (sc.Score < worstScore) { worstScore = sc.Score; worstId = d.Id; }
        }
        if (n == 0) return;

        var tier = Ranks.Tier(sum / n);
        var l = InfoLabel();
        l.AddThemeFontSizeOverride("normal_font_size", 12);
        l.Text =
            $"[color=#c8ccd5]Build[/color]  [color=#{TierHex(tier)}][b]{tier}[/b][/color] [color=#9aa3b2]avg ({n} rated)[/color]" +
            (bestId != null ? $"    [color=#c8ccd5]Best[/color] [color=#86e08a]{Pretty(bestId)}[/color]" : "") +
            (worstId != null && worstId != bestId ? $"    [color=#c8ccd5]Weakest[/color] [color=#e08a86]{Pretty(worstId)}[/color]" : "");
        _content.AddChild(l);
    }

    // ---- Current Run: live combat ---------------------------------------------------

    private void BuildCombat(CombatSnapshot c)
    {
        _content.AddChild(SectionHeader(
            c.Turn is { } t ? $"Combat · turn {t}" : "Combat", c.Enemies.Count(e => e.IsAlive)));
        foreach (var e in c.Enemies)
        {
            if (!e.IsAlive) continue;
            var name = string.IsNullOrEmpty(e.Name) ? Pretty(e.Id) : e.Name;
            var intent = e.Intents.Count > 0 ? IntentText(e.Intents) : "";
            // An enemy lining up an attack gets a reddish name so the threat reads at a glance.
            var nameColor = e.IntendsToAttack ? "#f0a0a0" : "#e6e6e6";
            var row = InfoLabel();
            row.Text =
                $"[color={nameColor}][b]{name}[/b][/color]  [color=#c74b4b]{e.CurrentHp}/{e.MaxHp}[/color]" +
                (e.Block > 0 ? $" [color=#5fcde0]+{e.Block}[/color]" : "") +
                (intent != "" ? $"   [color=#d7a84a]{intent}[/color]" : "") +
                (e.Powers.Count > 0 ? $"\n[color=#9aa3b2]{string.Join(", ", e.Powers.Select(Pretty))}[/color]" : "");
            _content.AddChild(row);
        }
    }

    // ---- Current Run: reward / shop / event decision help ---------------------------

    private void BuildRewardHelper(List<string> offered)
    {
        PersonalStats.EnsureLoaded(); // the player's own pick history, when signed in
        _content.AddChild(SectionHeader("Card reward", offered.Count));
        // Match the in-world plates' best pick when known, else the highest Codex Score on offer.
        var best = RewardContext.BestCardId;
        if (string.IsNullOrEmpty(best))
        {
            var top = double.MinValue;
            foreach (var id in offered)
                if (CodexScores.Card(id) is { } sc && sc.Score > top) { top = sc.Score; best = id; }
        }

        foreach (var id in offered.OrderByDescending(id => CodexScores.Card(id)?.Score ?? -1))
        {
            var sc = CodexScores.Card(id);
            var tier = ScoreTier(sc);
            var flag = id == best ? " [color=#ffd34d][b]Best pick[/b][/color]" : "";
            var stat = sc is { Picks: > 0 } ? $"  [color=#9aa3b2]Score {sc.Score:0} · {sc.WinRate:0.0}% win[/color]" : "";
            var you = PersonalStats.Card(id) is { Offered: > 0 } u ? $"  [color=#7fb0ff]you {(int)System.Math.Round(u.Picked * 100.0 / u.Offered)}%[/color]" : "";
            var row = InfoLabel();
            row.Text = (tier != null ? $"[color=#{TierHex(tier)}][b]{tier}[/b][/color]  " : "") +
                       $"[color=#e6e6e6]{Pretty(id)}[/color]{flag}{stat}{you}";
            _content.AddChild(row);
        }
    }

    private void BuildShop(ShopInfo shop)
    {
        var stocked = shop.Cards.Concat(shop.Relics).Concat(shop.Potions).Count(i => i.Stocked && i.Id != null);
        _content.AddChild(SectionHeader("Shop", stocked));
        AddShopRow("Cards", shop.Cards, CodexScores.Card);
        AddShopRow("Relics", shop.Relics, CodexScores.Relic);
        AddShopRow("Potions", shop.Potions, CodexScores.Potion);
        if (shop.Removal is { Stocked: true } rm)
        {
            var l = InfoLabel();
            l.Text = $"[color=#c8ccd5]Card removal[/color]   [color=#d7a84a]{rm.Cost}g[/color]";
            _content.AddChild(l);
        }
    }

    private void AddShopRow(string label, List<ShopItemInfo> items, Func<string, EntityScore?> score)
    {
        var stocked = items.Where(i => i.Stocked && i.Id != null).ToList();
        if (stocked.Count == 0) return;
        var head = new Label { Text = label.ToUpperInvariant() };
        head.AddThemeColorOverride("font_color", TextMuted);
        head.AddThemeFontSizeOverride("font_size", 10);
        _content.AddChild(head);
        foreach (var i in stocked)
        {
            var tier = ScoreTier(score(i.Id!));
            var row = InfoLabel();
            row.Text = (tier != null ? $"[color=#{TierHex(tier)}][b]{tier}[/b][/color]  " : "") +
                       $"[color=#e6e6e6]{Pretty(i.Id)}[/color]   [color=#d7a84a]{i.Cost}g[/color]" +
                       (i.OnSale ? "   [color=#4ec977]50% off[/color]" : "");
            _content.AddChild(row);
        }
    }

    private void BuildEvent(EventInfo ev)
    {
        _content.AddChild(SectionHeader(ev.Title ?? "Event", ev.Options.Count));
        if (!string.IsNullOrEmpty(ev.Prompt))
        {
            var p = InfoLabel();
            p.AddThemeColorOverride("default_color", TextMuted);
            p.AddThemeFontSizeOverride("normal_font_size", 13);
            p.Text = ev.Prompt;
            _content.AddChild(p);
        }
        var comm = CommunityStats.Event(ev.Id);
        foreach (var o in ev.Options)
        {
            var pct = comm != null ? EventOptionPct(comm, o.Key) : null;
            var pctText = pct is { } v ? $"   [color=#9aa3b2]{v:0}% pick[/color]" : "";
            var state = o.Chosen ? "   [color=#9aa3b2](chosen)[/color]"
                : o.Locked ? "   [color=#9aa3b2](locked)[/color]" : "";
            var color = o.Locked ? "#9aa3b2" : "#e6e6e6";
            var row = InfoLabel();
            row.Text = $"[color={color}]• {o.Text}[/color]{state}{pctText}";
            _content.AddChild(row);
        }
    }

    // Community pick rate for an event option, matched by the key's last segment and summing
    // staged repeats (KEY_0, KEY_1...), mirroring the native event hover tip.
    private static double? EventOptionPct(EventCommunity comm, string textKey)
    {
        if (comm.Total <= 0 || string.IsNullOrEmpty(textKey)) return null;
        var key = textKey.Substring(textKey.LastIndexOf('.') + 1).ToUpperInvariant();
        var count = -1;
        foreach (var o in comm.Options) if (o.Id == key) { count = o.Count; break; }
        if (count < 0)
        {
            count = 0;
            foreach (var o in comm.Options)
                if (o.Id.StartsWith(key + "_", StringComparison.Ordinal)) count += o.Count;
            if (count == 0) return null;
        }
        return count * 100.0 / comm.Total;
    }

    // ---- Current Run: route preview -------------------------------------------------

    private void BuildRoute(ActRoute route)
    {
        var count = route.Monsters.Count + route.Elites.Count + route.Events.Count
                    + (route.Boss != null ? 1 : 0) + (route.Ancient != null ? 1 : 0);
        if (count == 0) return;
        _content.AddChild(SectionHeader("Coming this act", count));
        foreach (var m in route.Monsters) AddRouteLine("Fight", m, "#e6e6e6");
        foreach (var e in route.Elites) AddRouteLine("Elite", e, "#e0b070");
        foreach (var ev in route.Events) AddRouteLine("Event", ev, "#6bd3c7");
        if (route.Boss is { } boss) AddRouteLine("Boss", boss, "#c74b4b");
        if (route.Ancient is { } anc) AddRouteLine("Ancient", anc, "#b58cff");
    }

    private void AddRouteLine(string kind, EncounterRef enc, string color)
    {
        var name = string.IsNullOrEmpty(enc.Name) ? Pretty(enc.Id) : enc.Name;
        var row = InfoLabel();
        row.Text = $"[color=#9aa3b2]{kind}[/color]  [color={color}]{name}[/color]{DangerSuffix(enc.Id)}";
        _content.AddChild(row);
    }

    // " 28% HP · 4% deaths" - community danger for a specific encounter (percent scale), or
    // "" when below the sample floor / unknown.
    private static string DangerSuffix(string id)
    {
        if (CommunityStats.Encounter(id) is not { } d) return "";
        return $"   [color=#c8ccd5]{d.AvgDmgPct:0}% HP[/color]" +
               (d.DeathRate > 0 ? $" [color=#c74b4b]· {d.DeathRate:0.#}% deaths[/color]" : "");
    }

    // ---- Current Run: live loadout (deck / relics / potions) ------------------------

    private void BuildDeckList(List<DeckEntry> deck)
    {
        _content.AddChild(SectionHeader("Deck", deck.Count));
        // Collapse duplicates by (id, upgraded, enchantment); keep first-seen order for the sort.
        var groups = new Dictionary<string, (DeckEntry Entry, int Count)>();
        var order = new List<string>();
        foreach (var d in deck)
        {
            var key = $"{d.Id}|{d.Upgraded}|{d.Enchantment}";
            if (groups.TryGetValue(key, out var g)) groups[key] = (g.Entry, g.Count + 1);
            else { groups[key] = (d, 1); order.Add(key); }
        }

        var rows = order.Select(k => groups[k]).ToList();
        rows.Sort((a, b) =>
        {
            var sa = CodexScores.Card(a.Entry.Id)?.Score ?? -1;
            var sb = CodexScores.Card(b.Entry.Id)?.Score ?? -1;
            return sa != sb ? sb.CompareTo(sa) : string.Compare(a.Entry.Id, b.Entry.Id, StringComparison.Ordinal);
        });

        var flow = ChipFlow();
        foreach (var (entry, n) in rows)
        {
            var tier = ScoreTier(CodexScores.Card(entry.Id));
            var name = Pretty(entry.Id) + (entry.Upgraded ? "[color=#86e08a]+[/color]" : "");
            if (!string.IsNullOrEmpty(entry.Enchantment)) name += $" [color=#b58cff]{Pretty(entry.Enchantment)}[/color]";
            if (n > 1) name += $" [color=#9aa3b2]×{n}[/color]";
            flow.AddChild(Chip(name, tier));
        }
        _content.AddChild(flow);
    }

    private void BuildRelicList(List<RelicEntry> relics)
    {
        _content.AddChild(SectionHeader("Relics", relics.Count));
        var flow = ChipFlow();
        foreach (var r in relics)
            flow.AddChild(Chip(Pretty(r.Id), ScoreTier(CodexScores.Relic(r.Id))));
        _content.AddChild(flow);
    }

    private void BuildPotionList(List<PotionEntry> potions)
    {
        _content.AddChild(SectionHeader("Potions", potions.Count));
        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var p in potions)
        {
            if (counts.TryGetValue(p.Id, out var n)) counts[p.Id] = n + 1;
            else { counts[p.Id] = 1; order.Add(p.Id); }
        }
        var flow = ChipFlow();
        foreach (var id in order)
        {
            var name = Pretty(id) + (counts[id] > 1 ? $" [color=#9aa3b2]×{counts[id]}[/color]" : "");
            flow.AddChild(Chip(name, ScoreTier(CodexScores.Potion(id))));
        }
        _content.AddChild(flow);
    }

    private HFlowContainer ChipFlow()
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);
        return flow;
    }

    // A compact entity chip: name (BBCode) with a tier-colored border and tier-letter prefix.
    private Control Chip(string text, string? tier)
    {
        var panel = new PanelContainer();
        var box = new StyleBoxFlat { BgColor = BgSofter, BorderColor = tier != null ? TierColor(tier) : Border };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(6);
        box.ContentMarginLeft = 8; box.ContentMarginRight = 8;
        box.ContentMarginTop = 3; box.ContentMarginBottom = 3;
        panel.AddThemeStyleboxOverride("panel", box);

        var rich = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rich.AddThemeFontSizeOverride("normal_font_size", 12);
        rich.AddThemeColorOverride("default_color", Text);
        rich.Text = (tier != null ? $"[color=#{TierHex(tier)}][b]{tier}[/b][/color] " : "") + text;
        panel.AddChild(rich);
        return panel;
    }

    // Tier letter for an entity score (Elo tier for rated cards, else the Score tier), or null
    // when the entity has too few picks to rate.
    private static string? ScoreTier(EntityScore? sc) =>
        sc is { Picks: > 0 } ? CodexScores.EloTier(sc.Elo) ?? Ranks.Tier(sc.Score) : null;

    private static string TierHex(string tier) => tier switch
    {
        "S" => "ffd34d", "A" => "86e08a", "B" => "6bd3c7",
        "C" => "e8e3d6", "D" => "e0b070", _ => "e08a86",
    };

    private static Color TierColor(string tier) => Hex(TierHex(tier));

    // "16x2 · buff" - the enemy's upcoming intent(s).
    private static string IntentText(List<IntentInfo> intents)
    {
        var parts = new List<string>();
        foreach (var i in intents)
            parts.Add(i.Damage is { } d
                ? (i.Hits is { } h && h > 1 ? $"{d}x{h}" : d.ToString())
                : i.Type);
        return string.Join(" · ", parts);
    }

    // ---- Leaderboards tab -----------------------------------------------------------

    // The Leaderboards tab is itself a submenu: a sub-nav row over the active sub-board.
    private void BuildLeaderboard()
    {
        _content.AddChild(BuildLbSubNav());
        switch (_lbSub)
        {
            case 0:
                ShowBoard(_a10, b => _a10 = b,
                    () => RunFeeds.LeaderboardAsync("fastest", minAscension: 10, limit: 25),
                    "Fastest wins · Ascension 10+", metricTime: true);
                break;
            case 1:
                ShowBoard(_daily, b => _daily = b,
                    () => RunFeeds.LeaderboardAsync("highest_ascension", gameMode: "daily", today: true, limit: 25),
                    "Today's daily climb", metricTime: false);
                break;
            case 2:
                ShowStanding();
                break;
        }
    }

    private Control BuildLbSubNav()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        for (var i = 0; i < LbSub.Length; i++)
        {
            var idx = i;
            var b = new Button { Text = $"{i + 1}. {LbSub[i]}", Flat = true };
            b.AddThemeFontSizeOverride("font_size", 12);
            b.AddThemeColorOverride("font_color", i == _lbSub ? Accent : TextMuted);
            b.Pressed += () => SetLbSub(idx);
            row.AddChild(b);
        }
        return row;
    }

    private void SetLbSub(int sub)
    {
        _lbSub = sub;
        _loadToken++;
        Clear();
        BuildLeaderboard();
    }

    // Render a cached board, or show "Loading…" and fetch it, re-rendering when it lands.
    private void ShowBoard(List<BoardRun>? cache, Action<List<BoardRun>> store,
        Func<System.Threading.Tasks.Task<List<BoardRun>>> fetch, string title, bool metricTime)
    {
        if (cache != null) { RenderBoard(cache, title, metricTime); return; }
        AddEmpty(_content, "Loading…");
        var token = _loadToken;
        _ = LoadAsync(fetch(), b =>
        {
            store(b);
            if (_tab == 1 && token == _loadToken) { Clear(); BuildLeaderboard(); }
        });
    }

    private void RenderBoard(List<BoardRun> board, string title, bool metricTime)
    {
        _content.AddChild(SectionHeader(title, board.Count));
        if (board.Count == 0) { AddEmpty(_content, "No runs on this board yet."); return; }

        var grid = LbGrid(6);
        HeaderCells(grid, "#", "Player", "Character", "Asc", metricTime ? "Time" : "Floors", "");
        foreach (var r in board)
        {
            Cell(grid, r.Rank.ToString(), TextMuted);
            Cell(grid, r.Player, Text);
            Cell(grid, Pretty(r.Character), Text);
            Cell(grid, "A" + r.Ascension, TextMuted);
            Cell(grid, metricTime ? FmtTime(r.RunTime) : r.Floors.ToString(), Accent);
            grid.AddChild(r.Hash is { } h ? ViewButton(Config.RunUrl(h)) : new Control());
        }
        _content.AddChild(grid);
    }

    // Your Standing: your winning runs, each with its live GLOBAL rank on the fastest board
    // (filled in async per run) + a View link.
    private void ShowStanding()
    {
        if (_wins != null) { RenderStanding(_wins); return; }
        if (string.IsNullOrEmpty(Config.SteamId))
        {
            AddEmpty(_content, "Sign-in pending; your standing will appear here.");
            return;
        }
        AddEmpty(_content, "Loading your standing…");
        var token = _loadToken;
        _ = LoadAsync(RunFeeds.PlayerWinsAsync(Config.SteamId, 60), w =>
        {
            _wins = w;
            if (_tab == 1 && _lbSub == 2 && token == _loadToken) { Clear(); BuildLeaderboard(); }
        });
    }

    private void RenderStanding(List<RunSummary> wins)
    {
        _content.AddChild(SectionHeader("Your wins · global rank", wins.Count));
        if (wins.Count == 0)
        {
            AddEmpty(_content, "No wins uploaded yet. Win a run with tracking on to rank here.");
            return;
        }

        var grid = LbGrid(5);
        HeaderCells(grid, "Rank", "Character", "Asc", "Time", "");
        var shown = 0;
        foreach (var w in wins)
        {
            if (shown++ >= 12) break;
            var rankCell = new Label { Text = "#…" };
            rankCell.AddThemeColorOverride("font_color", Accent);
            rankCell.AddThemeFontSizeOverride("font_size", 13);
            grid.AddChild(rankCell);
            Cell(grid, Pretty(w.Character), Text);
            Cell(grid, "A" + w.Ascension, TextMuted);
            Cell(grid, FmtTime(w.RunTime), Text);
            grid.AddChild(w.Hash is { } h ? ViewButton(Config.RunUrl(h)) : new Control());

            var token = _loadToken;
            _ = LoadAsync(RunFeeds.RunRankAsync(w.Hash), rank =>
            {
                if (token == _loadToken && GodotObject.IsInstanceValid(rankCell))
                    rankCell.Text = rank.HasValue ? $"#{rank}" : "—";
            });
        }
        _content.AddChild(grid);
    }

    private GridContainer LbGrid(int columns)
    {
        var grid = new GridContainer { Columns = columns, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 6);
        return grid;
    }

    // ---- Runs tab -------------------------------------------------------------------

    private void BuildRuns()
    {
        if (_runs != null) { RenderRuns(_runs); return; }
        if (string.IsNullOrEmpty(Config.SteamId)) { AddEmpty(_content, "Sign-in pending; your runs will appear here."); return; }
        AddEmpty(_content, "Loading your runs…");
        var token = _loadToken;
        _ = LoadAsync(RunFeeds.RecentRunsAsync(Config.SteamId, 20), runs =>
        {
            _runs = runs;
            if (_tab == 2 && token == _loadToken) { Clear(); RenderRuns(runs); }
        });
    }

    private void RenderRuns(List<RunSummary> runs)
    {
        _content.AddChild(SectionHeader("Your recent runs", runs.Count));
        if (runs.Count == 0) { AddEmpty(_content, "No uploaded runs yet. Turn on run tracking to see them here."); return; }

        foreach (var r in runs)
        {
            var rowPanel = new PanelContainer();
            var rs = new StyleBoxFlat { BgColor = BgSoft, BorderColor = Border };
            rs.SetBorderWidthAll(1);
            rs.SetCornerRadiusAll(6);
            rs.ContentMarginLeft = 10; rs.ContentMarginRight = 10;
            rs.ContentMarginTop = 6; rs.ContentMarginBottom = 6;
            rowPanel.AddThemeStyleboxOverride("panel", rs);

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);

            var line = new RichTextLabel
            {
                BbcodeEnabled = true, FitContent = true, ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.Off,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            line.AddThemeFontSizeOverride("normal_font_size", 13);
            var result = r.Abandoned
                ? "[color=#9aa3b2]Abandoned[/color]"
                : r.Win ? "[color=#4ec977]Victory[/color]"
                : $"[color=#c74b4b]Death[/color][color=#9aa3b2] · {Pretty(r.KilledBy)}[/color]";
            line.Text =
                $"[color=#e6e6e6][b]{Pretty(r.Character)}[/b][/color]   A{r.Ascension}   {result}\n" +
                $"[color=#9aa3b2]{r.Floors} floors · {FmtTime(r.RunTime)} · {FmtDate(r.Date)}[/color]";
            row.AddChild(line);

            // "View" opens the run's public page in the browser (alt-tabs out of the game).
            if (r.Hash is { } hash)
                row.AddChild(ViewButton(Config.RunUrl(hash)));

            rowPanel.AddChild(row);
            _content.AddChild(rowPanel);
        }
    }

    // ---- Settings tab ---------------------------------------------------------------

    // In-overlay mirror of the mod settings: pick the community stat bracket and toggle the
    // on-screen surfaces. Writes the same SpireCodexConfig the BaseLib menu does, and persists
    // it the same way (the auto-property setters don't save on their own).
    private void BuildSettings()
    {
        AboutHead("Community stats");
        AboutText("Which players the shown win-rates and pick-rates are drawn from. Higher brackets follow more competitive players. Applies to plates and hover tips.");
        _content.AddChild(BuildBracketRow());

        AboutHead("On-screen");
        _content.AddChild(SettingToggle("Damage meter", () => SpireCodexConfig.ShowDamageMeter, v => SpireCodexConfig.ShowDamageMeter = v));
        _content.AddChild(SettingToggle("Card reward hints", () => SpireCodexConfig.ShowCardRewardHints, v => SpireCodexConfig.ShowCardRewardHints = v));
        _content.AddChild(SettingToggle("Hover tips", () => SpireCodexConfig.ShowHoverTips, v => SpireCodexConfig.ShowHoverTips = v));
        _content.AddChild(SettingToggle("Map guidance", () => SpireCodexConfig.ShowMapDanger, v => SpireCodexConfig.ShowMapDanger = v));
        _content.AddChild(SettingToggle("Upcoming events", () => SpireCodexConfig.ShowUpcomingEvents, v => SpireCodexConfig.ShowUpcomingEvents = v));
        _content.AddChild(SettingToggle("Post-run card", () => SpireCodexConfig.ShowPostRunCard, v => SpireCodexConfig.ShowPostRunCard = v));

        AboutText("These also live in the game's mod settings menu; changes here save the same way. Drag the damage meter to reposition it.");
    }

    // A row of selectable bracket buttons; the active one is gold. Clicking sets the config and
    // persists; the producer applies it to the score cache on its next tick.
    private Control BuildBracketRow()
    {
        _bracketButtons.Clear();
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var (bracket, label, tip) in BracketChoices)
        {
            var pick = bracket;
            var b = new Button { Text = label, TooltipText = tip };
            b.AddThemeFontSizeOverride("font_size", 13);
            b.AddThemeStyleboxOverride("normal", ButtonBox(BgSofter, Border));
            b.AddThemeStyleboxOverride("hover", ButtonBox(Border, Accent));
            b.AddThemeStyleboxOverride("pressed", ButtonBox(Border, Accent));
            b.Pressed += () => { SpireCodexConfig.Stats = pick; PersistConfig(); RefreshBracketRow(); };
            _bracketButtons.Add(b);
            flow.AddChild(b);
        }
        RefreshBracketRow();
        return flow;
    }

    private void RefreshBracketRow()
    {
        for (var i = 0; i < _bracketButtons.Count && i < BracketChoices.Length; i++)
            _bracketButtons[i].AddThemeColorOverride(
                "font_color", BracketChoices[i].Bracket == SpireCodexConfig.Stats ? Accent : TextMuted);
    }

    // A label + On/Off button bound to a bool config field. Clicking flips and persists it.
    private Control SettingToggle(string label, Func<bool> get, Action<bool> set)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var name = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        name.AddThemeColorOverride("font_color", Text);
        name.AddThemeFontSizeOverride("font_size", 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(name);

        var b = new Button { CustomMinimumSize = new Vector2(54, 0) };
        b.AddThemeFontSizeOverride("font_size", 13);
        b.AddThemeStyleboxOverride("normal", ButtonBox(BgSofter, Border));
        b.AddThemeStyleboxOverride("hover", ButtonBox(Border, Accent));
        b.AddThemeStyleboxOverride("pressed", ButtonBox(Border, Accent));
        void Paint()
        {
            var on = get();
            b.Text = on ? "On" : "Off";
            b.AddThemeColorOverride("font_color", on ? Good : TextMuted);
        }
        b.Pressed += () => { set(!get()); PersistConfig(); Paint(); };
        Paint();
        row.AddChild(b);
        return row;
    }

    // BaseLib's config auto-properties don't save on set, so persist after a UI change. Same call
    // ConsentPrompt uses (the registered instance's immediate Save) — deliberately not the
    // debounced variant, since mixing Save() and SaveDebounced() on one config can deadlock.
    private static void PersistConfig() => BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();

    // ---- About tab ------------------------------------------------------------------

    private void BuildAbout()
    {
        var brand = InfoLabel();
        brand.AddThemeFontSizeOverride("normal_font_size", 20);
        brand.Text = "[color=#d7a84a][b]Spire[/b][/color] [color=#e6e6e6][b]Codex[/b][/color]" +
                     "  [color=#c8ccd5]— the comprehensive Slay the Spire 2 database[/color]";
        _content.AddChild(brand);

        var links = new HBoxContainer();
        links.AddThemeConstantOverride("separation", 8);
        links.AddChild(LinkButton("spire-codex.com", SiteUrl));
        links.AddChild(LinkButton("GitHub", GithubUrl));
        links.AddChild(LinkButton("Discord", DiscordUrl));
        links.AddChild(LinkButton("Patreon", PatreonUrl));
        _content.AddChild(links);

        var note = InfoLabel();
        note.Text = "[color=#c8ccd5]This mod is the in-game companion to the Spire Codex " +
                    "[/color][color=#d7a84a]Overwolf overlay[/color][color=#c8ccd5] — the same " +
                    "database and live run tracking, native to the game.[/color]";
        _content.AddChild(note);
        _content.AddChild(LinkButton("Download the Overwolf overlay", OverlayUrl));

        AboutHead("What it is");
        AboutText("Spire Codex is a searchable, always-up-to-date database of every card, relic, " +
                  "potion, monster, event, power, ascension level, and game mechanic. The data is " +
                  "reverse-engineered directly from the game files, so it tracks every patch within " +
                  "hours of release.");

        AboutHead("How the data is built");
        AboutText("Each patch: PCK extraction pulls the card art and assets, ILSpy decompiles the " +
                  "game DLL, 22 Python parsers extract structured data, SmartFormat resolves the " +
                  "descriptions, Spine renders the characters and monsters, and changelog diffs " +
                  "capture every change.");

        AboutHead("How scoring works");
        AboutText("Two community numbers, computed straight from uploaded runs (not opinion): the " +
                  "Codex Score (0-100, win-rate based) and Codex Elo (head-to-head strength from " +
                  "reward-screen picks). Full writeup with worked examples at " +
                  "spire-codex.com/leaderboards/scoring.");
        _content.AddChild(LinkButton("Read the scoring writeup", ScoringUrl));

        AboutHead("Support the project");
        AboutText("Spire Codex is free. If it helps your runs, you can support ongoing development " +
                  "and the server costs on Patreon — every bit keeps the data and updates coming.");
        _content.AddChild(LinkButton("Support on Patreon", PatreonUrl));

        AboutHead("Feedback & feature requests");
        AboutText("Always open to feedback — please let me know if you love or hate the mod, or want " +
                  "any features. The Discord is the fastest way to reach me.");
        _content.AddChild(LinkButton("Join the Discord", DiscordUrl));
    }

    private void AboutHead(string title)
    {
        var l = new Label { Text = title.ToUpperInvariant() };
        l.AddThemeColorOverride("font_color", Accent);
        l.AddThemeFontSizeOverride("font_size", 12);
        _content.AddChild(l);
    }

    private void AboutText(string text)
    {
        var l = InfoLabel();
        l.AddThemeColorOverride("default_color", TextMuted);
        l.Text = text;
        _content.AddChild(l);
    }

    // ---- shared rendering helpers ---------------------------------------------------

    private RichTextLabel InfoLabel()
    {
        var l = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeFontSizeOverride("normal_font_size", 14);
        l.AddThemeColorOverride("default_color", Text);
        return l;
    }

    private async System.Threading.Tasks.Task LoadAsync<T>(System.Threading.Tasks.Task<T> task, Action<T> onDone)
    {
        try { var r = await task.ConfigureAwait(false); Callable.From(() => onDone(r)).CallDeferred(); }
        catch { /* leave the loading text */ }
    }

    private void Clear() { foreach (var c in _content.GetChildren()) c.QueueFree(); }

    private Control SectionHeader(string title, int count)
    {
        var head = new HBoxContainer();
        var label = new Label { Text = title.ToUpperInvariant() };
        label.AddThemeColorOverride("font_color", TextMuted);
        label.AddThemeFontSizeOverride("font_size", 12);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(label);
        head.AddChild(CountPill(count));
        return head;
    }

    private Control CountPill(int count)
    {
        var pill = new PanelContainer();
        var ps = new StyleBoxFlat { BgColor = BgSofter, BorderColor = Border };
        ps.SetBorderWidthAll(1);
        ps.SetCornerRadiusAll(999);
        ps.ContentMarginLeft = 7; ps.ContentMarginRight = 7;
        ps.ContentMarginTop = 1; ps.ContentMarginBottom = 1;
        pill.AddThemeStyleboxOverride("panel", ps);
        var l = new Label { Text = count.ToString() };
        l.AddThemeColorOverride("font_color", TextMuted);
        l.AddThemeFontSizeOverride("font_size", 11);
        pill.AddChild(l);
        return pill;
    }

    private void HeaderCells(GridContainer grid, params string[] headers)
    {
        foreach (var h in headers)
        {
            var l = new Label { Text = h.ToUpperInvariant() };
            l.AddThemeColorOverride("font_color", TextMuted);
            l.AddThemeFontSizeOverride("font_size", 10);
            grid.AddChild(l);
        }
    }

    private void Cell(GridContainer grid, string text, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", 13);
        grid.AddChild(l);
    }

    // An accent button that opens a URL in the browser. Relies on the overlay receiving
    // mouse clicks (Godot GUI controls get input priority over gameplay).
    private Button LinkButton(string label, string url)
    {
        var b = new Button { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        b.AddThemeFontSizeOverride("font_size", 13);
        b.AddThemeColorOverride("font_color", Accent);
        b.AddThemeColorOverride("font_hover_color", Text);
        b.AddThemeStyleboxOverride("normal", ButtonBox(BgSofter, Border));
        b.AddThemeStyleboxOverride("hover", ButtonBox(Border, Accent));
        b.AddThemeStyleboxOverride("pressed", ButtonBox(Border, Accent));
        b.Pressed += () => OS.ShellOpen(url);
        return b;
    }

    private Button ViewButton(string url)
    {
        var b = LinkButton("View", url);
        b.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        b.AddThemeFontSizeOverride("font_size", 12);
        return b;
    }

    private static StyleBoxFlat ButtonBox(Color bg, Color border)
    {
        var s = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(4);
        s.ContentMarginLeft = 12; s.ContentMarginRight = 12;
        s.ContentMarginTop = 4; s.ContentMarginBottom = 4;
        return s;
    }

    private void AddEmpty(Control into, string text)
    {
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", TextMuted);
        l.AddThemeFontSizeOverride("font_size", 12);
        into.AddChild(l);
    }

    private void AddWarn(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeColorOverride("font_color", Hex("e0a020"));
        l.AddThemeFontSizeOverride("font_size", 12);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _content.AddChild(l);
    }

    // ---- formatting -----------------------------------------------------------------

    private static string FmtTime(int seconds)
    {
        if (seconds <= 0) return "-";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string FmtDate(string? iso)
        => DateTimeOffset.TryParse(iso, out var d) ? d.ToString("MMM d") : "";

    // "THE_INSATIABLE_BOSS" -> "The Insatiable Boss"; null -> "?".
    private static string Pretty(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "?";
        var parts = id.Split('_');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
        return string.Join(' ', parts);
    }

    private static Color Hex(string rgb) => new(
        System.Convert.ToInt32(rgb.Substring(0, 2), 16) / 255f,
        System.Convert.ToInt32(rgb.Substring(2, 2), 16) / 255f,
        System.Convert.ToInt32(rgb.Substring(4, 2), 16) / 255f);
}
