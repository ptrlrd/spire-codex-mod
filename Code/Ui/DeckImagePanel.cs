using System;
using System.Collections.Generic;
using Godot;
using SpireCodex.Api;

namespace SpireCodex.Ui;

public partial class DeckImagePanel : CanvasLayer
{
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

    private static readonly string[] Tabs =
        { "deck_tab_leaderboard", "deck_tab_runs", "deck_tab_import", "deck_tab_settings", "deck_tab_about" };

    private static readonly (StatBracket Bracket, string Label, string Tip)[] BracketChoices =
    {
        (StatBracket.All, "deck_bracket_all", "deck_bracket_all_tip"),
        (StatBracket.A10, "deck_bracket_a10", "deck_bracket_a10_tip"),
        (StatBracket.A10_WR30, "deck_bracket_a10_wr30", "deck_bracket_a10_wr30_tip"),
        (StatBracket.A10_WR50, "deck_bracket_a10_wr50", "deck_bracket_a10_wr50_tip"),
        (StatBracket.A10_WR75, "deck_bracket_a10_wr75", "deck_bracket_a10_wr75_tip"),
    };

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

    private static readonly string[] StickClickNames =
    {
        "controller_l_stick_press", "controller_joystick_press", "controller_left_stick_press",
    };
    private static StringName? _stickClick;
    private static bool _stickClickResolved;

    private static readonly StringName BumperLeft = "controller_left_bumper";
    private static readonly StringName BumperRight = "controller_right_bumper";

    private VBoxContainer _content = null!;
    private Label _hint = null!;
    private Label? _backfillStatus;
    private Button? _importButton;
    private int _importSource = 1, _importTarget = 1;
    private bool _importArmed;
    private static DeckImagePanel? _instance;

    public static bool IsOpen => _instance is { Visible: true };
    private readonly List<Button> _tabButtons = new();
    private readonly List<Button> _bracketButtons = new();
    private int _tab;
    private int _loadToken;

    private int _lbSub;
    private List<BoardRun>? _a10;
    private List<BoardRun>? _daily;
    private List<RunSummary>? _wins;
    private List<RunSummary>? _runs;

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
        Layer = 220;

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
        Skin.ApplyFont(panel);
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

        if (SpireCodexConfig.OverlayPad == ControllerToggle.StickClick
            && SpireCodexConfig.ShowDeckView
            && StickClickAction() is { } stickClick
            && IsAction(@event, stickClick))
        {
            ToggleOverlay();
            GetViewport().SetInputAsHandled();
            return;
        }

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

        if (Visible && key.Keycode == Key.Tab)
        {
            SetTab((_tab + 1) % Tabs.Length);
            GetViewport().SetInputAsHandled();
            return;
        }

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

    private static bool IsAction(InputEvent e, StringName action) =>
        InputMap.HasAction(action) && e.IsActionPressed(action);

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

    public static void OpenOverlay()
        => Callable.From(() => { if (_instance is { Visible: false }) _instance.ToggleOverlay(); }).CallDeferred();

    public static void OpenSettings() => OpenOn(3);

    private static void OpenOn(int tab) => Callable.From(() =>
    {
        if (_instance is not { } panel) return;
        if (!panel.Visible) panel.ToggleOverlay();
        panel.SetTab(tab);
    }).CallDeferred();

    private void ToggleOverlay()
    {
        Visible = !Visible;
        if (Visible)
        {
            Loc.Refresh();
            for (var i = 0; i < _tabButtons.Count && i < Tabs.Length; i++)
                _tabButtons[i].Text = Loc.T(Tabs[i]);
            UpdateHint();
            _a10 = null; _daily = null; _wins = null; _runs = null;
            SetTab(_tab);
        }
    }

    private void UpdateHint()
    {
        var keyLabel = SpireCodexConfig.OverlayKey is var k and not HotKey.None ? k.ToString() : null;
        var padLabel = PadLabel(SpireCodexConfig.OverlayPad);
        string close;
        if (!string.IsNullOrEmpty(keyLabel) && !string.IsNullOrEmpty(padLabel))
            close = Loc.F("deck_hint_key_or_pad", keyLabel, padLabel);
        else
            close = keyLabel ?? padLabel ?? Loc.T("deck_hint_hotkey");
        _hint.Text = Loc.F("deck_hint_controls", close);
    }

    private static string? PadLabel(ControllerToggle t) => t switch
    {
        ControllerToggle.StickClick => Loc.T("deck_pad_r3l3"),
        _ => null,
    };

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

            if (r.Hash is { } hash)
                row.AddChild(ViewButton(Config.RunUrl(hash)));

            rowPanel.AddChild(row);
            _content.AddChild(rowPanel);
        }
    }

    private void BuildSettings()
    {
        _importButton = null;
        _importArmed = false;

        AboutHead(Loc.T("deck_settings_community_stats"));
        AboutText(Loc.T("deck_settings_community_stats_desc"));
        _content.AddChild(BuildBracketRow());

        AboutHead(Loc.T("deck_settings_run_tracking"));
        AboutText(Loc.T("deck_settings_run_tracking_desc"));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_upload_runs"), () => SpireCodexConfig.UploadRuns, v => SpireCodexConfig.UploadRuns = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_live_status"), () => SpireCodexConfig.ShareLiveStatus, v => SpireCodexConfig.ShareLiveStatus = v));
        _content.AddChild(BuildBackfillRow());

        AboutHead(Loc.T("deck_settings_onscreen"));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_damage_meter"), () => SpireCodexConfig.ShowDamageMeter, v => SpireCodexConfig.ShowDamageMeter = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_card_reward_hints"), () => SpireCodexConfig.ShowCardRewardHints, v => SpireCodexConfig.ShowCardRewardHints = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_hover_tips"), () => SpireCodexConfig.ShowHoverTips, v => SpireCodexConfig.ShowHoverTips = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_map_guidance"), () => SpireCodexConfig.ShowMapDanger, v => SpireCodexConfig.ShowMapDanger = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_upcoming_events"), () => SpireCodexConfig.ShowUpcomingEvents, v => SpireCodexConfig.ShowUpcomingEvents = v));
        _content.AddChild(SettingCheck(Loc.T("deck_toggle_post_run_card"), () => SpireCodexConfig.ShowPostRunCard, v => SpireCodexConfig.ShowPostRunCard = v));
        AboutText(Loc.T("deck_settings_onscreen_desc"));

        AboutHead(Loc.T("deck_settings_controls"));
        _content.AddChild(BuildHotkeyRow());
        _content.AddChild(BuildControllerRow());

        var replay = new Button { Text = Loc.T("deck_settings_show_welcome"), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        StyleSecondary(replay);
        replay.Pressed += () => { Visible = false; WelcomeCard.ShowAgain(); };
        _content.AddChild(replay);

        AboutHead(Loc.T("deck_settings_localization"));
        AboutText(Loc.T("deck_settings_localization_desc"));
    }

    private void BuildImport()
    {
        AboutHead(Loc.T("deck_settings_import"));
        AboutText(Loc.T("deck_settings_import_desc"));
        _content.AddChild(BuildImportRow());

        AboutHead(Loc.T("deck_import_credit_head"));
        AboutText(Loc.T("deck_import_credit"));
        _content.AddChild(LinkButton(Loc.T("deck_import_credit_link"), ImportCreditUrl));
    }

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

    private ImageTexture? _checkOn, _checkOff;
    private ImageTexture CheckIcon(bool on) => on ? (_checkOn ??= MakeCheckIcon(true)) : (_checkOff ??= MakeCheckIcon(false));
    private static ImageTexture MakeCheckIcon(bool check)
    {
        const int size = 22, border = 2, inset = 6;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(Accent);
        img.FillRect(new Rect2I(border, border, size - 2 * border, size - 2 * border), Field);
        if (check)
            img.FillRect(new Rect2I(inset, inset, size - 2 * inset, size - 2 * inset), Accent);
        return ImageTexture.CreateFromImage(img);
    }

    private Control BuildHotkeyRow()
    {
        var keys = new[] { HotKey.None, HotKey.F5, HotKey.F6, HotKey.F7, HotKey.F8, HotKey.F9, HotKey.F10, HotKey.F11, HotKey.F12 };
        return SettingDropdown(Loc.T("deck_setting_hotkey"),
            keys, k => k == HotKey.None ? Loc.T("deck_key_none") : k.ToString(),
            SpireCodexConfig.OverlayKey,
            k => { SpireCodexConfig.OverlayKey = k; PersistConfig(); UpdateHint(); });
    }

    private Control BuildControllerRow()
    {
        var pads = new[] { ControllerToggle.Off, ControllerToggle.StickClick };
        return SettingDropdown(Loc.T("deck_setting_controller"),
            pads, p => p == ControllerToggle.StickClick ? Loc.T("deck_pad_stick") : Loc.T("deck_pad_off"),
            SpireCodexConfig.OverlayPad,
            p => { SpireCodexConfig.OverlayPad = p; PersistConfig(); UpdateHint(); });
    }

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
        _backfillStatus = status;

        btn.Pressed += () =>
        {
            var ok = RunUploader.BackfillNow();
            status.Text = Loc.T(ok ? "deck_backfill_started" : "deck_backfill_need_uploads");
            status.AddThemeColorOverride("font_color", Accent);
            status.Visible = true;
        };
        return box;
    }

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

    private void DisarmImport()
    {
        _importArmed = false;
        if (_importButton is { } b && GodotObject.IsInstanceValid(b)) b.Text = Loc.T("deck_import_button");
    }

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

    private static void PersistConfig() => BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();

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
        catch {  }
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

    private static string FmtTime(int seconds)
    {
        if (seconds <= 0) return "-";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string FmtDate(string? iso)
        => DateTimeOffset.TryParse(iso, out var d) ? d.ToString("MMM d") : "";

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
