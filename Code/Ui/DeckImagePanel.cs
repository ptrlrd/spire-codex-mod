using System;
using System.Collections.Generic;
using Godot;
using SpireCodex.Api;

namespace SpireCodex.Ui;

// The companion panel (default hotkey F5, rebindable), styled with the unified Spire Codex
// palette (brand gold #ffd34d on warm dark cards, shared with the FTUE cards and the extractor
// mod). Five tabs: Leaderboard, Runs, Import, Settings, and About. Switch tabs by clicking or pressing
// Tab while open (the shoulder bumpers on a controller). The live in-run dashboard is handled by
// the Spire Codex Overwolf overlay; this native panel focuses on rankings, your run history,
// in-overlay settings, and info.
public partial class DeckImagePanel : CanvasLayer
{
    // Unified Spire Codex palette: warm card surfaces + brand gold #ffd34d + warm off-white text,
    // shared with the FTUE cards and the extractor mod so the whole thing reads as one product.
    // The tokens live in Skin (one source of truth for every surface); aliased here so the rest
    // of this file reads unchanged.
    private static readonly Color Bg = Skin.Bg;
    private static readonly Color BgSoft = Skin.BgSoft;
    private static readonly Color BgSofter = Skin.BgSofter;
    private static readonly Color Border = Skin.Border;
    private static readonly Color Text = Skin.Text;
    private static readonly Color TextMuted = Skin.TextMuted;
    private static readonly Color Accent = Skin.Accent;
    private static readonly Color AccentBright = Skin.AccentBright;
    private static readonly Color AccentDim = Skin.AccentDim;
    private static readonly Color Field = Skin.Field;
    private static readonly Color Good = Skin.Good;
    private static readonly Color Danger = Skin.Danger;

    // Loc KEYS (not text): resolved to on-screen strings at the render site (BuildTabBar), since
    // the array is built at class load when the language may not be ready yet.
    private static readonly string[] Tabs =
        { "deck_tab_leaderboard", "deck_tab_runs", "deck_tab_import", "deck_tab_settings", "deck_tab_about" };

    // The community stat bracket choices shown in the Settings tab selector (Label/Tip hold loc
    // KEYS, resolved via Loc.T at the render site in BuildBracketRow).
    private static readonly (StatBracket Bracket, string Label, string Tip)[] BracketChoices =
    {
        (StatBracket.All, "deck_bracket_all", "deck_bracket_all_tip"),
        (StatBracket.A10, "deck_bracket_a10", "deck_bracket_a10_tip"),
        (StatBracket.A10_WR30, "deck_bracket_a10_wr30", "deck_bracket_a10_wr30_tip"),
        (StatBracket.A10_WR50, "deck_bracket_a10_wr50", "deck_bracket_a10_wr50_tip"),
        (StatBracket.A10_WR75, "deck_bracket_a10_wr75", "deck_bracket_a10_wr75_tip"),
    };

    // Links (ripped from the Overwolf about page).
    private const string SiteUrl = "https://spire-codex.com";
    private const string GithubUrl = "https://github.com/ptrlrd/spire-codex";
    private const string DiscordUrl = "https://discord.gg/uged4qFufK";
    private const string OverlayUrl = "https://overwolf.com/app/ptrlrd-spire_codex";
    private const string ScoringUrl = "https://spire-codex.com/leaderboards/scoring";
    private const string PatreonUrl = "https://www.patreon.com/cw/SpireCodex";
    private const string ImportCreditUrl = "https://github.com/Ind-E/ImportVanillaSaves";

    private PanelContainer _panel = null!;
    private bool _dragging;
    private Vector2 _dragOffset;

    // The game's stick-click ("peek") action. STS2 routes the controller through Steam Input
    // and emits this as a synthetic action; it's also the native left-stick-click binding when
    // Steam Input is off. Listening for the action (not a raw joypad button) is the only thing
    // that reaches the mod while Steam Input is active, which is the default.
    //
    // The game renames these: v0.109.1 turned controller_joystick_press into
    // controller_l_stick_press. So resolve against the live InputMap rather than hard-coding one
    // name, newest first. A rename then costs a lookup instead of silently killing the binding.
    private static readonly string[] StickClickNames =
    {
        "controller_l_stick_press", "controller_joystick_press", "controller_left_stick_press",
    };
    private static StringName? _stickClick;
    private static bool _stickClickResolved;

    // L1 / R1 bumpers (also synthetic actions under Steam Input) cycle the panel's tabs while
    // it's open — the controller mirror of the Tab key.
    private static readonly StringName BumperLeft = "controller_left_bumper";
    private static readonly StringName BumperRight = "controller_right_bumper";

    private VBoxContainer _content = null!;
    private Label _hint = null!;
    private Label? _backfillStatus; // the Settings "Backfill past runs" progress line, when built
    private Button? _importButton;  // the Import tab's action button, when built
    private int _importSource = 1, _importTarget = 1;
    private bool _importArmed;      // second press of Import actually writes
    private static DeckImagePanel? _instance; // for WelcomeCard's "Open it now"

    // True while the F5 overlay (the card/deck display) is open, so other on-map surfaces (the
    // map danger route) can stand down rather than drawing their rings around/through it.
    public static bool IsOpen => _instance is { Visible: true };
    private readonly List<Button> _tabButtons = new();
    private readonly List<Button> _bracketButtons = new(); // Settings tab stat-bracket selector
    private int _tab;
    private int _loadToken; // guards against a stale async fetch populating the wrong tab

    private int _lbSub; // leaderboard sub-board: 0 = Fast Wins (A10), 1 = Daily Climb, 2 = Your Standing
    private List<BoardRun>? _a10;
    private List<BoardRun>? _daily;
    private List<RunSummary>? _wins;
    private List<RunSummary>? _runs;

    // Loc KEYS (not text): resolved via Loc.T at the render site (BuildLbSubNav).
    private static readonly string[] LbSub = { "deck_lbsub_fast_wins", "deck_lbsub_daily_climb", "deck_lbsub_your_standing" };

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
        _instance = this;
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
        Skin.ApplyFont(panel); // Kreon for the whole panel; it never adopted a theme before
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
        brand.AddThemeFontSizeOverride("bold_font_size", 18);
        brand.Text = Loc.T("deck_brand_wordmark");
        row.AddChild(brand);

        _hint = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _hint.AddThemeColorOverride("font_color", TextMuted);
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.VerticalAlignment = VerticalAlignment.Center;
        UpdateHint();
        row.AddChild(_hint);

        // Close affordance in the corner. The hotkey still works, but a visible X is what people
        // reach for first, and it does not depend on remembering which key they bound.
        row.AddChild(CloseButton(() => { if (Visible) ToggleOverlay(); }));

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
            var b = new Button { Text = Loc.T(Tabs[i]), Flat = true };
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

        // Live "Backfill past runs" progress on the Settings tab: a running count/percentage while
        // it uploads, then a one-line summary. Driven from RunUploader so it reflects the auto
        // first-enable backfill too, not only a button press.
        if (Visible && _tab == 3 && _backfillStatus is { } s && GodotObject.IsInstanceValid(s))
        {
            if (RunUploader.BackfillActive)
            {
                var total = RunUploader.BackfillTotal;
                var done = RunUploader.BackfillDone;
                var pct = total > 0 ? done * 100 / total : 0;
                s.Text = Loc.F("deck_backfill_progress", done, total, pct);
                s.AddThemeColorOverride("font_color", Accent);
                s.Visible = true;
            }
            else if (RunUploader.BackfillHasRun)
            {
                s.Text = Loc.F("deck_backfill_done", RunUploader.BackfillAdded, RunUploader.BackfillDuplicate);
                s.AddThemeColorOverride("font_color", Good);
                s.Visible = true;
            }
        }
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
            && StickClickAction() is { } stickClick
            && IsAction(@event, stickClick))
        {
            ToggleOverlay();
            GetViewport().SetInputAsHandled();
            return;
        }

        // While the panel is open, the bumpers cycle tabs (controller mirror of Tab).
        if (Visible && IsAction(@event, BumperRight))
        {
            SetTab((_tab + 1) % Tabs.Length);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (Visible && IsAction(@event, BumperLeft))
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
        if (Visible && _tab == 0 && key.Keycode is Key.Key1 or Key.Key2 or Key.Key3)
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

    // The X that closes a surface: quiet until hovered, then brand gold. Plain capital X rather
    // than a dingbat so it renders in Kreon instead of falling back to another face.
    internal static Button CloseButton(Action onPressed)
    {
        var b = new Button { Text = "X", Flat = true, TooltipText = Loc.T("deck_close") };
        b.AddThemeFontSizeOverride("font_size", 15);
        b.AddThemeColorOverride("font_color", TextMuted);
        b.AddThemeColorOverride("font_hover_color", Accent);
        b.AddThemeColorOverride("font_pressed_color", AccentDim);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.Pressed += onPressed;
        return b;
    }

    // Matches an action only when the game actually defines it. Godot logs an error for every
    // lookup of an unknown action, and _Input runs on every event, so an action the game has
    // renamed would otherwise spam the log thousands of times a session (v0.109.1 did exactly
    // that: 1794 lines in one run). HasAction is a dictionary hit, so the guard is free.
    private static bool IsAction(InputEvent e, StringName action) =>
        InputMap.HasAction(action) && e.IsActionPressed(action);

    // The stick-click action under whatever name this game build uses, or null when it defines
    // none of them (the pad toggle then does nothing instead of erroring on every event).
    private static StringName? StickClickAction()
    {
        if (_stickClickResolved) return _stickClick;
        _stickClickResolved = true;
        foreach (var name in StickClickNames)
        {
            if (!InputMap.HasAction(name)) continue;
            _stickClick = name;
            MainFile.Logger.Info($"controller: stick-click action is '{name}'");
            return _stickClick;
        }
        MainFile.Logger.Info("controller: no known stick-click action in this build; pad toggle disabled");
        return null;
    }

    // Open the overlay from outside (the welcome card's "Open it now"). No-op if already open.
    public static void OpenOverlay()
        => Callable.From(() => { if (_instance is { Visible: false }) _instance.ToggleOverlay(); }).CallDeferred();

    // Open straight onto the Settings tab (the main-menu entry). Switches tabs even when the
    // panel is already up, so the entry always lands where it says it will.
    public static void OpenSettings() => OpenOn(3);

    private static void OpenOn(int tab) => Callable.From(() =>
    {
        if (_instance is not { } panel) return;
        if (!panel.Visible) panel.ToggleOverlay();
        panel.SetTab(tab);
    }).CallDeferred();

    // Flip the panel's visibility and, when opening, refresh the hint and drop cached feeds so
    // each open shows fresh data. Shared by the keyboard hotkey and the controller binding.
    private void ToggleOverlay()
    {
        Visible = !Visible;
        if (Visible)
        {
            // Re-read the game language each open, so a language change (or a first open before
            // the game's LocManager was ready at boot) is reflected. Re-apply the tab labels,
            // which are built once and otherwise wouldn't pick up the refreshed language.
            Loc.Refresh();
            for (var i = 0; i < _tabButtons.Count && i < Tabs.Length; i++)
                _tabButtons[i].Text = Loc.T(Tabs[i]);
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
            close = Loc.F("deck_hint_key_or_pad", keyLabel, padLabel);
        else
            close = keyLabel ?? padLabel ?? Loc.T("deck_hint_hotkey");
        _hint.Text = Loc.F("deck_hint_controls", close);
    }

    // Short controller-binding label for the close hint (null when the pad toggle is off).
    private static string? PadLabel(ControllerToggle t) => t switch
    {
        ControllerToggle.StickClick => Loc.T("deck_pad_r3l3"),
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

        // Version nudges (relocated from the retired Current Run tab): shown atop whichever tab is
        // open so an available update or an untested game build is never missed.
        if (ModVersion.UpdateAvailable is { } up)
            AddWarn(Loc.F("deck_update_available", up, ModVersion.UpdateUrl ?? "spire-codex.com"));
        if (ModVersion.Sts2Untested)
            AddWarn(Loc.T("deck_warn_untested"));

        switch (tab)
        {
            case 0: BuildLeaderboard(); break;
            case 1: BuildRuns(); break;
            case 2: BuildImport(); break;
            case 3: BuildSettings(); break;
            case 4: BuildAbout(); break;
        }
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
                    Loc.T("deck_board_fastest_wins"), metricTime: true);
                break;
            case 1:
                ShowBoard(_daily, b => _daily = b,
                    () => RunFeeds.LeaderboardAsync("highest_ascension", gameMode: "daily", today: true, limit: 25),
                    Loc.T("deck_board_daily_climb"), metricTime: false);
                break;
            case 2:
                ShowStanding();
                break;
        }
    }

    private Control BuildLbSubNav()
    {
        // Segmented sub-tabs: one clearly-clickable pill per board, the active one gold-bordered,
        // so players click across the boards instead of scrolling. Number keys 1-3 still switch.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        for (var i = 0; i < LbSub.Length; i++)
        {
            var idx = i;
            var active = i == _lbSub;
            var b = new Button { Text = Loc.F("deck_lb_subnav_item", i + 1, Loc.T(LbSub[i])) };
            b.AddThemeFontSizeOverride("font_size", 13);
            b.AddThemeColorOverride("font_color", active ? Accent : TextMuted);
            b.AddThemeColorOverride("font_hover_color", AccentBright);
            b.AddThemeStyleboxOverride("normal", ButtonBox(active ? Border : BgSofter, active ? Accent : Border));
            b.AddThemeStyleboxOverride("hover", ButtonBox(Border, Accent));
            b.AddThemeStyleboxOverride("pressed", ButtonBox(Border, Accent));
            b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
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
        AddEmpty(_content, Loc.T("deck_loading"));
        var token = _loadToken;
        _ = LoadAsync(fetch(), b =>
        {
            store(b);
            if (_tab == 0 && token == _loadToken) { Clear(); BuildLeaderboard(); }
        });
    }

    private void RenderBoard(List<BoardRun> board, string title, bool metricTime)
    {
        _content.AddChild(SectionHeader(title, board.Count));
        if (board.Count == 0) { AddEmpty(_content, Loc.T("deck_board_empty")); return; }

        var grid = LbGrid(6);
        HeaderCells(grid, "#", Loc.T("deck_col_player"), Loc.T("deck_col_character"), Loc.T("deck_col_asc"), metricTime ? Loc.T("deck_col_time") : Loc.T("deck_col_floors"), "");
        foreach (var r in board)
        {
            Cell(grid, r.Rank.ToString(), TextMuted);
            Cell(grid, r.Player, Text);
            Cell(grid, CharName(r.Character), Text);
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
            AddEmpty(_content, Loc.T("deck_standing_signin_pending"));
            return;
        }
        AddEmpty(_content, Loc.T("deck_standing_loading"));
        var token = _loadToken;
        _ = LoadAsync(RunFeeds.PlayerWinsAsync(Config.SteamId, 60), w =>
        {
            _wins = w;
            if (_tab == 0 && _lbSub == 2 && token == _loadToken) { Clear(); BuildLeaderboard(); }
        });
    }

    private void RenderStanding(List<RunSummary> wins)
    {
        _content.AddChild(SectionHeader(Loc.T("deck_standing_title"), wins.Count));
        if (wins.Count == 0)
        {
            AddEmpty(_content, Loc.T("deck_standing_empty"));
            return;
        }

        var grid = LbGrid(5);
        HeaderCells(grid, Loc.T("deck_col_rank"), Loc.T("deck_col_character"), Loc.T("deck_col_asc"), Loc.T("deck_col_time"), "");
        var shown = 0;
        foreach (var w in wins)
        {
            if (shown++ >= 12) break;
            var rankCell = new Label { Text = "#…" };
            rankCell.AddThemeColorOverride("font_color", Accent);
            rankCell.AddThemeFontSizeOverride("font_size", 13);
            grid.AddChild(rankCell);
            Cell(grid, CharName(w.Character), Text);
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
        if (string.IsNullOrEmpty(Config.SteamId)) { AddEmpty(_content, Loc.T("deck_runs_signin_pending")); return; }
        AddEmpty(_content, Loc.T("deck_runs_loading"));
        var token = _loadToken;
        _ = LoadAsync(RunFeeds.RecentRunsAsync(Config.SteamId, 20), runs =>
        {
            _runs = runs;
            if (_tab == 1 && token == _loadToken) { Clear(); RenderRuns(runs); }
        });
    }

    private void RenderRuns(List<RunSummary> runs)
    {
        _content.AddChild(SectionHeader(Loc.T("deck_runs_title"), runs.Count));
        if (runs.Count == 0) { AddEmpty(_content, Loc.T("deck_runs_empty")); return; }

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
                ? Loc.T("deck_result_abandoned")
                : r.Win ? Loc.T("deck_result_victory")
                : Loc.F("deck_result_death", EncName(r.KilledBy));
            line.Text =
                $"[color=#e8e3d6][b]{CharName(r.Character)}[/b][/color]   A{r.Ascension}   {result}\n" +
                Loc.F("deck_runs_meta", r.Floors, FmtTime(r.RunTime), FmtDate(r.Date));
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
        _importButton = null;
        _importArmed = false; // the tab is rebuilt from scratch, so never re-enter armed

        // Community stat bracket.
        AboutHead(Loc.T("deck_settings_community_stats"));
        AboutText(Loc.T("deck_settings_community_stats_desc"));
        _content.AddChild(BuildBracketRow());

        // Run tracking (privacy). Turning uploads on here re-triggers the consent disclosure when
        // it was never granted, exactly as flipping it in the game's own options menu does.
        AboutHead(Loc.T("deck_settings_run_tracking"));
        AboutText(Loc.T("deck_settings_run_tracking_desc"));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_upload_runs"), () => SpireCodexConfig.UploadRuns, v => SpireCodexConfig.UploadRuns = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_live_status"), () => SpireCodexConfig.ShareLiveStatus, v => SpireCodexConfig.ShareLiveStatus = v));
        _content.AddChild(BuildBackfillRow());

        // On-screen surfaces.
        AboutHead(Loc.T("deck_settings_onscreen"));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_damage_meter"), () => SpireCodexConfig.ShowDamageMeter, v => SpireCodexConfig.ShowDamageMeter = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_card_reward_hints"), () => SpireCodexConfig.ShowCardRewardHints, v => SpireCodexConfig.ShowCardRewardHints = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_hover_tips"), () => SpireCodexConfig.ShowHoverTips, v => SpireCodexConfig.ShowHoverTips = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_map_guidance"), () => SpireCodexConfig.ShowMapDanger, v => SpireCodexConfig.ShowMapDanger = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_upcoming_events"), () => SpireCodexConfig.ShowUpcomingEvents, v => SpireCodexConfig.ShowUpcomingEvents = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_post_run_card"), () => SpireCodexConfig.ShowPostRunCard, v => SpireCodexConfig.ShowPostRunCard = v));
        AboutText(Loc.T("deck_settings_onscreen_desc"));

        // Controls: the overlay hotkey + controller button, now configurable right here instead of
        // only in the game's mod options menu.
        AboutHead(Loc.T("deck_settings_controls"));
        _content.AddChild(BuildHotkeyRow());
        _content.AddChild(BuildControllerRow());

        // Replay the first-run welcome card.
        var replay = new Button { Text = Loc.T("deck_settings_show_welcome"), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        StyleSecondary(replay);
        replay.Pressed += () => { Visible = false; WelcomeCard.ShowAgain(); };
        _content.AddChild(replay);

        // Localization note: there's no setting to change here (the mod follows the game's
        // language automatically), just a heads-up so players know the behaviour exists.
        AboutHead(Loc.T("deck_settings_localization"));
        AboutText(Loc.T("deck_settings_localization_desc"));
    }

    // ---- Import tab -----------------------------------------------------------------

    // Bringing an unmodded save across. Modded STS2 writes to its own steam/<id>/modded/ tree, so
    // first-time modders land on an empty account; this copies the vanilla one over. Its own tab
    // rather than a Settings section: it is a one-off task people go looking for, not a toggle.
    private void BuildImport()
    {
        AboutHead(Loc.T("deck_settings_import"));
        AboutText(Loc.T("deck_settings_import_desc"));
        _content.AddChild(BuildImportRow());

        // Credit where it's due: Ind-E's ImportVanillaSaves worked this out first.
        AboutHead(Loc.T("deck_import_credit_head"));
        AboutText(Loc.T("deck_import_credit"));
        _content.AddChild(LinkButton(Loc.T("deck_import_credit_link"), ImportCreditUrl));
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
            var b = new Button { Text = Loc.T(label), TooltipText = Loc.T(tip) };
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

    // A gold checkbox bound to a bool config field (the extractor mod's box style: a gold ring
    // with a solid gold fill when on). Clicking flips and persists it immediately.
    private Control SettingCheck(string label, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Text = label, ButtonPressed = get() };
        cb.AddThemeFontSizeOverride("font_size", 14);
        cb.AddThemeColorOverride("font_color", Text);
        cb.AddThemeColorOverride("font_hover_color", AccentBright);
        cb.AddThemeColorOverride("font_pressed_color", Text);
        cb.AddThemeConstantOverride("h_separation", 10);
        cb.AddThemeIconOverride("unchecked", CheckIcon(false));
        cb.AddThemeIconOverride("checked", CheckIcon(true));
        cb.AddThemeIconOverride("unchecked_disabled", CheckIcon(false));
        cb.AddThemeIconOverride("checked_disabled", CheckIcon(true));
        cb.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        cb.Toggled += on => { set(on); PersistConfig(); };
        return cb;
    }

    // Cached checkbox icons (built once): a gold-bordered square, dark interior, gold block when on.
    private ImageTexture? _checkOn, _checkOff;
    private ImageTexture CheckIcon(bool on) => on ? (_checkOn ??= MakeCheckIcon(true)) : (_checkOff ??= MakeCheckIcon(false));
    private static ImageTexture MakeCheckIcon(bool check)
    {
        const int size = 22, border = 2, inset = 6;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(Accent);                                                                      // gold ring
        img.FillRect(new Rect2I(border, border, size - 2 * border, size - 2 * border), Field); // dark interior
        if (check)
            img.FillRect(new Rect2I(inset, inset, size - 2 * inset, size - 2 * inset), Accent); // gold fill
        return ImageTexture.CreateFromImage(img);
    }

    // Overlay hotkey picker (F5-F12, or None to unbind the key).
    private Control BuildHotkeyRow()
    {
        var keys = new[] { HotKey.None, HotKey.F5, HotKey.F6, HotKey.F7, HotKey.F8, HotKey.F9, HotKey.F10, HotKey.F11, HotKey.F12 };
        return SettingDropdown(Loc.T("deck_setting_hotkey"),
            keys, k => k == HotKey.None ? Loc.T("deck_key_none") : k.ToString(),
            SpireCodexConfig.OverlayKey,
            k => { SpireCodexConfig.OverlayKey = k; PersistConfig(); UpdateHint(); });
    }

    // Controller-button picker for the same overlay toggle (Off / stick-click).
    private Control BuildControllerRow()
    {
        var pads = new[] { ControllerToggle.Off, ControllerToggle.StickClick };
        return SettingDropdown(Loc.T("deck_setting_controller"),
            pads, p => p == ControllerToggle.StickClick ? Loc.T("deck_pad_stick") : Loc.T("deck_pad_off"),
            SpireCodexConfig.OverlayPad,
            p => { SpireCodexConfig.OverlayPad = p; PersistConfig(); UpdateHint(); });
    }

    // "Backfill past runs" action: manually upload the player's existing local run history now.
    // No-op with a hint if uploads/consent aren't on yet; otherwise kicks the backfill (background).
    private Control BuildBackfillRow()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);

        var btn = new Button { Text = Loc.T("deck_backfill_button"), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        StyleSecondary(btn);
        box.AddChild(btn);

        var status = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Visible = false,
        };
        status.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(status);
        _backfillStatus = status; // _Process drives the live count/percentage from here

        btn.Pressed += () =>
        {
            // On success the running count is taken over by _Process; here just show the kickoff
            // line (or the "turn uploads on" hint when it's a no-op).
            var ok = RunUploader.BackfillNow();
            status.Text = Loc.T(ok ? "deck_backfill_started" : "deck_backfill_need_uploads");
            status.AddThemeColorOverride("font_color", Accent);
            status.Visible = true;
        };
        return box;
    }

    // "Import vanilla saves": pick a vanilla profile and a modded profile, then copy the first
    // onto the second (unlocks, ancient stats, prefs, run history). Two-step confirm because it
    // overwrites the target; the vanilla side is only ever read.
    private Control BuildImportRow()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);

        var profiles = new[] { 1, 2, 3 };
        box.AddChild(SettingDropdown(Loc.T("deck_import_source"), profiles,
            i => Loc.F("deck_import_profile", i), _importSource,
            i => { _importSource = i; DisarmImport(); }));
        box.AddChild(SettingDropdown(Loc.T("deck_import_target"), profiles,
            i => Loc.F("deck_import_profile", i), _importTarget,
            i => { _importTarget = i; DisarmImport(); }));

        var btn = new Button { Text = Loc.T("deck_import_button"), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        StyleSecondary(btn);
        box.AddChild(btn);
        _importButton = btn;

        var status = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Visible = false,
        };
        status.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(status);

        btn.Pressed += () =>
        {
            void Say(string text, Color color)
            {
                status.Text = text;
                status.AddThemeColorOverride("font_color", color);
                status.Visible = true;
            }

            if (Core.SaveImport.RunInProgress()) { DisarmImport(); Say(Loc.T("deck_import_in_run"), Danger); return; }
            if (!Core.SaveImport.VanillaProfileHasData(_importSource))
            {
                DisarmImport();
                Say(Loc.F("deck_import_no_source", _importSource), Danger);
                return;
            }

            // First press arms and warns, second press does it.
            if (!_importArmed)
            {
                _importArmed = true;
                btn.Text = Loc.T("deck_import_confirm");
                Say(Loc.F("deck_import_warn", _importTarget), Accent);
                return;
            }
            DisarmImport();

            var runs = Core.SaveImport.Import(_importSource, _importTarget);
            if (runs < 0) { Say(Loc.T("deck_import_failed"), Danger); return; }

            // The menu still shows the pre-import profile, so reload it. On a miss (game rename)
            // the copy is still on disk and a restart picks it up.
            if (Core.SaveImport.ReloadMainMenu())
            {
                Say(Loc.F("deck_import_done", _importSource, _importTarget, runs), Good);
                Visible = false;
            }
            else
            {
                Say(Loc.F("deck_import_done_restart", _importSource, _importTarget, runs), Good);
            }
        };
        return box;
    }

    // Back to the unarmed state after a profile change or a completed/aborted import.
    private void DisarmImport()
    {
        _importArmed = false;
        if (_importButton is { } b && GodotObject.IsInstanceValid(b)) b.Text = Loc.T("deck_import_button");
    }

    // A label + OptionButton bound to an enum config field, styled to match the panel chrome.
    private Control SettingDropdown<T>(string label, T[] options, Func<T, string> name, T current, Action<T> set)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var nameLabel = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        nameLabel.AddThemeColorOverride("font_color", Text);
        nameLabel.AddThemeFontSizeOverride("font_size", 13);
        nameLabel.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(nameLabel);

        var opt = new OptionButton();
        opt.AddThemeFontSizeOverride("font_size", 13);
        opt.AddThemeColorOverride("font_color", Text);
        opt.AddThemeColorOverride("font_hover_color", AccentBright);
        opt.AddThemeColorOverride("font_focus_color", Text);
        opt.AddThemeStyleboxOverride("normal", ButtonBox(BgSofter, Border));
        opt.AddThemeStyleboxOverride("hover", ButtonBox(Border, Accent));
        opt.AddThemeStyleboxOverride("pressed", ButtonBox(Border, Accent));
        opt.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var selected = 0;
        for (var i = 0; i < options.Length; i++)
        {
            opt.AddItem(name(options[i]), i);
            if (EqualityComparer<T>.Default.Equals(options[i], current)) selected = i;
        }
        opt.Select(selected);
        opt.ItemSelected += idx => set(options[(int)idx]);
        row.AddChild(opt);
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
        brand.Text = Loc.T("deck_brand_wordmark") + Loc.T("deck_about_tagline");
        _content.AddChild(brand);

        var links = new HBoxContainer();
        links.AddThemeConstantOverride("separation", 8);
        links.AddChild(LinkButton("spire-codex.com", SiteUrl));
        links.AddChild(LinkButton("GitHub", GithubUrl));
        links.AddChild(LinkButton("Discord", DiscordUrl));
        links.AddChild(LinkButton("Patreon", PatreonUrl));
        _content.AddChild(links);

        var note = InfoLabel();
        note.Text = Loc.T("deck_about_companion");
        _content.AddChild(note);

        // Prominent gold CTA piping players to the Overwolf overlay for the live in-run dashboard
        // (this panel no longer duplicates it), and a natural place to advertise the overlay.
        var overlayCta = new Button { Text = Loc.T("deck_about_download_overlay"), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        StylePrimary(overlayCta);
        overlayCta.Pressed += () => OS.ShellOpen(OverlayUrl);
        _content.AddChild(overlayCta);

        AboutHead(Loc.T("deck_about_whatitis"));
        AboutText(Loc.T("deck_about_whatitis_body"));

        AboutHead(Loc.T("deck_about_data"));
        AboutText(Loc.T("deck_about_data_body"));

        AboutHead(Loc.T("deck_about_scoring"));
        AboutText(Loc.T("deck_about_scoring_body"));
        _content.AddChild(LinkButton(Loc.T("deck_about_read_scoring"), ScoringUrl));

        AboutHead(Loc.T("deck_about_support"));
        AboutText(Loc.T("deck_about_support_body"));
        _content.AddChild(LinkButton(Loc.T("deck_about_support_patreon"), PatreonUrl));

        AboutHead(Loc.T("deck_about_feedback"));
        AboutText(Loc.T("deck_about_feedback_body"));
        _content.AddChild(LinkButton(Loc.T("deck_about_join_discord"), DiscordUrl));
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
        var b = LinkButton(Loc.T("deck_view"), url);
        b.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        b.AddThemeFontSizeOverride("font_size", 12);
        return b;
    }

    private static StyleBoxFlat ButtonBox(Color bg, Color border) => Skin.ButtonBox(bg, border);

    // Extractor-mod button kit (ported so the two mods read as one family). Primary = a filled
    // gold pill with dark text, for the single main action on a view. Secondary = a dark pill with
    // a gold border, for supporting actions.
    private void StylePrimary(Button b) => Skin.Primary(b);

    private void StyleSecondary(Button b) => Skin.Secondary(b);

    private static StyleBoxFlat KitBox(Color bg, Color border) => Skin.KitBox(bg, border);

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
    // Game terms resolve to the game's own localized name (via its loc tables), each falling back
    // to a prettified id when the game has no entry, so a miss reads the same as before. Only the
    // two the Leaderboard/Runs tabs need remain: the character and the encounter that ended a run.
    private static string CharName(string? id) => Loc.CharacterName(id) ?? Pretty(id);
    private static string EncName(string? id) => Loc.EncounterName(id) ?? Pretty(id);

    private static string Pretty(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "?";
        var parts = id.Split('_');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
        return string.Join(' ', parts);
    }

    private static Color Hex(string rgb) => Skin.Hex(rgb);
}
