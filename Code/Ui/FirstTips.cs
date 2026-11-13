using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using SpireCodex.Api;

namespace SpireCodex.Ui;

public partial class FirstTips : CanvasLayer
{
    public sealed record Tip(string Id, string Title, string Body, (string Label, Action Act)[] Actions);

    private static FirstTips? _instance;
    private static HashSet<string>? _seen;

    private PanelContainer _panel = null!;
    private RichTextLabel _title = null!;
    private RichTextLabel _body = null!;
    private HBoxContainer _actions = null!;
    private Tip? _active;
    private Rect2 _anchor;
    private double _sinceOffer;
    private double _shownFor;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        tree.Root.CallDeferred(Node.MethodName.AddChild, new FirstTips { Name = "SpireCodexFirstTips" });
    }

    public static bool Seen(string id) => LoadSeen().Contains(id);

    public static void Offer(Tip tip, Rect2 anchor)
    {
        if (_instance is not { } t || Seen(tip.Id) || !CanShow()) return;
        if (t._active != null && t._active.Id != tip.Id) return;
        if (t._active == null)
        {
            t._active = tip;
            t._shownFor = 0;
            t.Fill(tip);
        }
        t._anchor = anchor;
        t._sinceOffer = 0;
    }

    public static void ResetAll()
    {
        _seen = new HashSet<string>();
        try { if (SeenPath() is { } p && File.Exists(p)) File.Delete(p); } catch { }
    }

    private static bool CanShow() =>
        WelcomeCard.Done && Consent.Answered && !DeckImagePanel.IsOpen;

    public override void _Ready()
    {
        _instance = this;
        Layer = 205;

        _panel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
        var s = new StyleBoxFlat { BgColor = Skin.Bg with { A = 0.97f }, BorderColor = Skin.Accent };
        s.SetBorderWidthAll(1);
        s.BorderWidthLeft = 3;
        s.SetCornerRadiusAll(8);
        s.ShadowColor = new Color(0, 0, 0, 0.5f);
        s.ShadowSize = 12;
        s.ContentMarginLeft = 14; s.ContentMarginRight = 14;
        s.ContentMarginTop = 10; s.ContentMarginBottom = 10;
        _panel.AddThemeStyleboxOverride("panel", s);
        Skin.ApplyFont(_panel);
        AddChild(_panel);

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        box.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(box);

        _title = Rich(14);
        box.AddChild(_title);
        _body = Rich(13);
        _body.AddThemeColorOverride("default_color", Skin.TextMuted);
        box.AddChild(_body);

        _actions = new HBoxContainer();
        _actions.AddThemeConstantOverride("separation", 6);
        box.AddChild(_actions);
    }

    public override void _Process(double delta)
    {
        if (_active == null) return;
        _sinceOffer += delta;
        if (!CanShow() || _sinceOffer > 0.4)
        {
            if (_shownFor >= 4) MarkSeen(_active.Id);
            Close();
            return;
        }
        _shownFor += delta;
        Place();
        _panel.Visible = true;
    }

    private void Fill(Tip tip)
    {
        Loc.Refresh();
        _title.Text = $"[b][color=#{Skin.Accent.ToHtml(false)}]{tip.Title}[/color][/b]";
        _body.Text = tip.Body;
        _panel.Size = Vector2.Zero;
        foreach (var c in _actions.GetChildren()) c.QueueFree();

        for (var i = 0; i < tip.Actions.Length; i++)
        {
            var (label, act) = tip.Actions[i];
            var b = new Button { Text = label };
            if (i == 0) Skin.Primary(b); else Skin.Secondary(b);
            b.AddThemeFontSizeOverride("font_size", 12);
            b.Pressed += () => { act(); Dismiss(); };
            _actions.AddChild(b);
        }

        var settings = new Button { Text = Loc.F("tip_settings", OverlayKeyName()), Flat = true };
        settings.AddThemeFontSizeOverride("font_size", 12);
        settings.AddThemeColorOverride("font_color", Skin.TextMuted);
        settings.AddThemeColorOverride("font_hover_color", Skin.AccentBright);
        settings.Pressed += () => { Dismiss(); DeckImagePanel.OpenSettings(); };
        _actions.AddChild(settings);
    }

    private void Place()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        var size = _panel.GetCombinedMinimumSize();
        var x = _anchor.Position.X + _anchor.Size.X / 2 - size.X / 2;
        var below = _anchor.End.Y + 12;
        var above = _anchor.Position.Y - size.Y - 12;
        var y = below + size.Y <= vp.Y - 8 ? below : above;
        _panel.Position = new Vector2(
            Mathf.Clamp(x, 8, Mathf.Max(8, vp.X - size.X - 8)),
            Mathf.Clamp(y, 8, Mathf.Max(8, vp.Y - size.Y - 8)));
    }

    private void Dismiss()
    {
        if (_active != null) MarkSeen(_active.Id);
        Close();
    }

    private void Close()
    {
        _active = null;
        _panel.Visible = false;
    }

    private static string OverlayKeyName() =>
        SpireCodexConfig.OverlayKey is var k and not HotKey.None ? k.ToString() : "F5";

    private static RichTextLabel Rich(int size)
    {
        var l = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(320, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        l.AddThemeColorOverride("default_color", Skin.Text);
        l.AddThemeFontSizeOverride("normal_font_size", size);
        l.AddThemeFontSizeOverride("bold_font_size", size);
        return l;
    }

    private static string? SeenPath()
    {
        try
        {
            return Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SpireCodex", "tips-seen");
        }
        catch { return null; }
    }

    private static HashSet<string> LoadSeen()
    {
        if (_seen != null) return _seen;
        _seen = new HashSet<string>();
        try
        {
            if (SeenPath() is { } p && File.Exists(p))
                foreach (var line in File.ReadAllLines(p))
                    if (line.Trim() is { Length: > 0 } id) _seen.Add(id);
        }
        catch { }
        return _seen;
    }

    private static void MarkSeen(string id)
    {
        if (!LoadSeen().Add(id)) return;
        try
        {
            if (SeenPath() is not { } p) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllLines(p, _seen!);
        }
        catch { }
    }
}

public static class Tips
{
    private static FirstTips.Tip Make(string id, string titleKey, string bodyKey, params (string, Action)[] actions)
        => new(id, Loc.T(titleKey), Loc.T(bodyKey), actions);

    private static (string, Action) GotIt() => (Loc.T("tip_gotit"), () => { });

    private static (string, Action) TurnOff(Feature f) => (Loc.T("tip_turn_off"), () => { f.Set(false); Features.Save(); });

    private static string New(Feature f) => Loc.F("tip_new", Loc.T(f.Name));

    public static FirstTips.Tip Plates() =>
        new("plates", New(Features.Plates), Loc.T("tip_plates_body"), new[] { GotIt(), TurnOff(Features.Plates) });

    public static FirstTips.Tip MapRoute() =>
        new("map_route", New(Features.MapRoute), Loc.T("tip_map_body"), new[] { GotIt(), TurnOff(Features.MapRoute) });

    public static FirstTips.Tip Meter() =>
        new("damage_meter", New(Features.DamageMeter), Loc.T("tip_meter_body"), new[] { GotIt(), TurnOff(Features.DamageMeter) });

    public static FirstTips.Tip EventsOn() =>
        new("events_on", Loc.T("deck_toggle_upcoming_events"), Loc.T("tip_events_on_body"),
            new[] { GotIt(), TurnOff(Features.UpcomingEvents) });

    public static FirstTips.Tip EventsOff() =>
        Make("events_off", "tip_events_off_title", "tip_events_off_body",
            (Loc.T("tip_keep_off"), () => { }),
            (Loc.T("tip_turn_on"), () => { Features.UpcomingEvents.Set(true); Features.Save(); }));
}
