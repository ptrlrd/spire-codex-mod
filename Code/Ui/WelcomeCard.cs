using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SpireCodex.Api;

namespace SpireCodex.Ui;

public partial class WelcomeCard : CanvasLayer
{
    private const int StepWelcome = 0, StepStats = 1, StepTracking = 2, StepOverlay = 3, StepSettings = 4, StepCount = 5;
    private const float Pad = 6f, Gap = 16f;

    private static WelcomeCard? _instance;
    private static bool? _done;

    private readonly ColorRect[] _dim = new ColorRect[4];
    private Panel _ring = null!;
    private PanelContainer _callout = null!;
    private VBoxContainer _box = null!;
    private readonly List<(Features.Preset Preset, PanelContainer Row)> _presetRows = new();
    private readonly List<(StatBracket Bracket, PanelContainer Row)> _bracketRows = new();
    private HBoxContainer _dots = null!;
    private int _step;
    private double _sinceCheck;
    private bool _settled;
    private bool _skipTracking;

    public static bool Done => _done ??= SeenMarkerExists();

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; welcome tour not started");
            return;
        }
        tree.Root.CallDeferred(Node.MethodName.AddChild, new WelcomeCard { Name = "SpireCodexWelcome" });
    }

    public static void ShowAgain() => Callable.From(() => _instance?.Open()).CallDeferred();

    public override void _Ready()
    {
        _instance = this;
        Layer = 225;

        for (var i = 0; i < _dim.Length; i++)
        {
            _dim[i] = new ColorRect { Color = new Color(0, 0, 0, 0.62f), MouseFilter = Control.MouseFilterEnum.Stop };
            AddChild(_dim[i]);
        }

        _ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        var rs = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = Skin.Accent };
        rs.SetBorderWidthAll(2);
        rs.SetCornerRadiusAll(8);
        _ring.AddThemeStyleboxOverride("panel", rs);
        AddChild(_ring);

        _callout = new PanelContainer();
        _callout.AddThemeStyleboxOverride("panel", Skin.CardBox());
        Skin.ApplyFont(_callout);
        AddChild(_callout);

        _box = new VBoxContainer();
        _box.AddThemeConstantOverride("separation", 12);
        _callout.AddChild(_box);

        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (Visible) { Layout(); return; }
        if (_settled) return;
        _sinceCheck += delta;
        if (_sinceCheck < 1.0) return;
        _settled = true;
        if (!Done) Open();
    }

    private void Open()
    {
        Loc.Refresh();
        Visible = true;
        _skipTracking = Consent.Answered;
        Go(StepWelcome);
    }

    private void Go(int step)
    {
        if (step == StepStats && SkipStats()) step = StepTracking;
        if (step == StepTracking && _skipTracking) step = StepOverlay;
        _step = step;
        if (step == StepOverlay) DeckImagePanel.OpenTab(DeckImagePanel.TabLeaderboard);
        if (step == StepSettings) DeckImagePanel.OpenTab(DeckImagePanel.TabSettings);
        Build();
        Layout();
    }

    private void Finish()
    {
        Visible = false;
        if (!Done) WriteSeenMarker();
        _done = true;
        DeckImagePanel.CloseOverlay();
    }

    private static bool SkipStats() =>
        !SpireCodexConfig.ShowCardRewardHints && !SpireCodexConfig.ShowHoverTips;

    private Rect2? Spotlight() => _step switch
    {
        StepOverlay => DeckImagePanel.TabBarRect,
        StepSettings => DeckImagePanel.TabRect(DeckImagePanel.TabSettings),
        _ => null,
    };

    private void Layout()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        var size = _callout.GetCombinedMinimumSize();

        if (Spotlight() is not { } r)
        {
            _ring.Visible = false;
            Dim(0, new Rect2(Vector2.Zero, vp));
            for (var i = 1; i < 4; i++) _dim[i].Visible = false;
            _callout.Position = (vp - size) / 2;
            return;
        }

        var hole = r.Grow(Pad);
        Dim(0, new Rect2(0, 0, vp.X, hole.Position.Y));
        Dim(1, new Rect2(0, hole.End.Y, vp.X, vp.Y - hole.End.Y));
        Dim(2, new Rect2(0, hole.Position.Y, hole.Position.X, hole.Size.Y));
        Dim(3, new Rect2(hole.End.X, hole.Position.Y, vp.X - hole.End.X, hole.Size.Y));
        _ring.Position = hole.Position;
        _ring.Size = hole.Size;
        _ring.Visible = true;

        var maxX = Mathf.Max(Gap, vp.X - size.X - Gap);
        var maxY = Mathf.Max(Gap, vp.Y - size.Y - Gap);
        Vector2 pos;
        if (hole.End.X + Gap + size.X <= vp.X)
            pos = new Vector2(hole.End.X + Gap, hole.Position.Y);
        else if (hole.Position.X - Gap - size.X >= 0)
            pos = new Vector2(hole.Position.X - Gap - size.X, hole.Position.Y);
        else
            pos = new Vector2(hole.Position.X, hole.End.Y + Gap);
        _callout.Position = new Vector2(Mathf.Clamp(pos.X, Gap, maxX), Mathf.Clamp(pos.Y, Gap, maxY));
    }

    private void Dim(int i, Rect2 r)
    {
        _dim[i].Position = r.Position;
        _dim[i].Size = new Vector2(Mathf.Max(0, r.Size.X), Mathf.Max(0, r.Size.Y));
        _dim[i].Visible = true;
    }

    private void Build()
    {
        foreach (var c in _box.GetChildren()) c.QueueFree();
        _presetRows.Clear();
        _bracketRows.Clear();
        var wide = _step is StepWelcome or StepStats or StepTracking;
        _box.CustomMinimumSize = new Vector2(wide ? 560 : 340, 0);
        _callout.Size = Vector2.Zero;

        var top = new HBoxContainer();
        _dots = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        _dots.AddThemeConstantOverride("separation", 5);
        FillDots();
        top.AddChild(_dots);
        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        top.AddChild(spacer);
        top.AddChild(DeckImagePanel.CloseButton(Finish));
        _box.AddChild(top);

        switch (_step)
        {
            case StepWelcome:
                Title(Loc.T("style_title"));
                var presets = new HBoxContainer();
                presets.AddThemeConstantOverride("separation", 8);
                presets.AddChild(PresetRow(Features.Preset.Track, "style_track", "style_track_desc"));
                presets.AddChild(PresetRow(Features.Preset.Stats, "style_stats", "style_stats_desc"));
                presets.AddChild(PresetRow(Features.Preset.Full, "style_full", "style_full_desc"));
                _box.AddChild(presets);
                Footer((Loc.T("tour_next"), () => Go(StepStats)));
                RefreshPresets();
                break;

            case StepStats:
                Title(Loc.T("tour_stats_title"));
                Body(Loc.T("deck_settings_community_stats_desc"));
                var brackets = new VBoxContainer();
                brackets.AddThemeConstantOverride("separation", 6);
                foreach (var (bracket, label, tip) in DeckImagePanel.BracketChoices)
                    brackets.AddChild(BracketRow(bracket, label, tip));
                _box.AddChild(brackets);
                Footer((Loc.T("tour_next"), () => Go(StepTracking)));
                RefreshBrackets();
                break;

            case StepTracking:
                Title(Loc.T("tour_tracking_title"));
                Body(Loc.T("tour_tracking_body"));
                _box.AddChild(Skin.SectionHead(Loc.T("tour_tracking_more")));
                var details = Rich(13);
                details.AddThemeColorOverride("default_color", Skin.TextMuted);
                details.Text = Loc.T("tour_tracking_details");
                _box.AddChild(details);
                Footer((Loc.T("consent_keepoff"), KeepOff), (Loc.T("consent_turnon"), TurnOn));
                break;

            case StepOverlay:
                Title(Loc.T("tour_overlay_title"));
                Body(Loc.F("tour_overlay_body", OverlayKeyName()));
                Footer((Loc.T("tour_next"), () => Go(StepSettings)));
                break;

            default:
                Title(Loc.T("tour_settings_title"));
                Body(Loc.T("tour_settings_body"));
                Footer((Loc.T("tour_discord"), () => OS.ShellOpen(DeckImagePanel.DiscordUrl)), (Loc.T("tour_jump_in"), Finish));
                break;
        }
    }

    private void TurnOn()
    {
        SpireCodexConfig.UploadRuns = true;
        Features.Save();
        Consent.Grant();
        MainFile.Logger.Info("run tracking turned on from the tour");
        Go(StepOverlay);
    }

    private void KeepOff()
    {
        SpireCodexConfig.UploadRuns = false;
        SpireCodexConfig.UploadReplays = false;
        Features.Save();
        Consent.Decline();
        MainFile.Logger.Info("run tracking kept off from the tour");
        Go(StepOverlay);
    }

    private void FillDots()
    {
        foreach (var c in _dots.GetChildren())
        {
            _dots.RemoveChild(c);
            c.QueueFree();
        }
        for (var i = 0; i < StepCount; i++)
        {
            if (i == StepStats && SkipStats()) continue;
            if (i == StepTracking && _skipTracking) continue;
            var dot = new Panel { CustomMinimumSize = new Vector2(i == _step ? 18 : 7, 7) };
            var s = new StyleBoxFlat { BgColor = i == _step ? Skin.Accent : Skin.Border };
            s.SetCornerRadiusAll(999);
            dot.AddThemeStyleboxOverride("panel", s);
            _dots.AddChild(dot);
        }
    }

    private void Title(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeColorOverride("font_color", Skin.Text);
        l.AddThemeFontSizeOverride("font_size", 20);
        _box.AddChild(l);
    }

    private void Body(string text)
    {
        var l = Rich(14);
        l.AddThemeColorOverride("default_color", Skin.TextMuted);
        l.Text = text;
        _box.AddChild(l);
    }

    private void Footer(params (string Label, Action Act)[] actions)
    {
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 10);
        if (_step != StepSettings)
        {
            var skip = new Button { Text = Loc.T("tour_skip"), Flat = true };
            skip.AddThemeFontSizeOverride("font_size", 12);
            skip.AddThemeColorOverride("font_color", Skin.TextMuted);
            skip.AddThemeColorOverride("font_hover_color", Skin.Text);
            skip.Pressed += Finish;
            foot.AddChild(skip);
        }
        foot.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        for (var i = 0; i < actions.Length; i++)
        {
            var (label, act) = actions[i];
            var b = new Button { Text = label };
            if (i == actions.Length - 1) Skin.Primary(b); else Skin.Secondary(b);
            b.Pressed += () => act();
            foot.AddChild(b);
        }
        _box.AddChild(foot);
    }

    private Control PresetRow(Features.Preset preset, string nameKey, string descKey)
    {
        var row = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        var v = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 2);
        var name = new Label { Text = Loc.T(nameKey), MouseFilter = Control.MouseFilterEnum.Ignore };
        name.AddThemeColorOverride("font_color", Skin.Text);
        name.AddThemeFontSizeOverride("font_size", 15);
        v.AddChild(name);
        var desc = Skin.Muted(Loc.T(descKey));
        desc.MouseFilter = Control.MouseFilterEnum.Ignore;
        desc.CustomMinimumSize = new Vector2(150, 0);
        v.AddChild(desc);
        row.AddChild(v);
        row.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) return;
            Features.Apply(preset);
            RefreshPresets();
            row.AcceptEvent();
        };
        _presetRows.Add((preset, row));
        return row;
    }

    private Control BracketRow(StatBracket bracket, string labelKey, string tipKey)
    {
        var row = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        var h = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", 12);
        var name = new Label
        {
            Text = Loc.T(labelKey),
            CustomMinimumSize = new Vector2(130, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        name.AddThemeColorOverride("font_color", Skin.Text);
        name.AddThemeFontSizeOverride("font_size", 15);
        h.AddChild(name);
        var desc = Skin.Muted(Loc.T(tipKey), 13);
        desc.MouseFilter = Control.MouseFilterEnum.Ignore;
        desc.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        h.AddChild(desc);
        row.AddChild(h);
        row.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) return;
            SpireCodexConfig.Stats = bracket;
            Features.Save();
            RefreshBrackets();
            row.AcceptEvent();
        };
        _bracketRows.Add((bracket, row));
        return row;
    }

    private void RefreshBrackets()
    {
        foreach (var (bracket, row) in _bracketRows)
        {
            if (!GodotObject.IsInstanceValid(row)) continue;
            var active = bracket == SpireCodexConfig.Stats;
            row.AddThemeStyleboxOverride("panel", Skin.RowBox(active));
            if (row.GetChild(0).GetChild(0) is Label name)
                name.AddThemeColorOverride("font_color", active ? Skin.Accent : Skin.Text);
        }
    }

    private void RefreshPresets()
    {
        FillDots();
        var current = Features.Current();
        foreach (var (preset, row) in _presetRows)
        {
            if (!GodotObject.IsInstanceValid(row)) continue;
            row.AddThemeStyleboxOverride("panel", Skin.RowBox(preset == current));
            if (row.GetChild(0).GetChild(0) is Label name)
                name.AddThemeColorOverride("font_color", preset == current ? Skin.Accent : Skin.Text);
        }
    }

    private static string OverlayKeyName() =>
        SpireCodexConfig.OverlayKey is var k and not HotKey.None ? k.ToString() : "F5";

    private static RichTextLabel Rich(int size)
    {
        var l = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeColorOverride("default_color", Skin.Text);
        l.AddThemeFontSizeOverride("normal_font_size", size);
        l.AddThemeFontSizeOverride("bold_font_size", size);
        return l;
    }

    private static string? MarkerPath()
    {
        try
        {
            return Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SpireCodex", "welcome-seen");
        }
        catch { return null; }
    }

    private static bool SeenMarkerExists() => MarkerPath() is { } p && File.Exists(p);

    private static void WriteSeenMarker()
    {
        try
        {
            if (MarkerPath() is not { } p) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, DateTimeOffset.UtcNow.ToString("o"));
        }
        catch { }
    }
}
