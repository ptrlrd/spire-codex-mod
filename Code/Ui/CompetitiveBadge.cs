using Godot;
using SpireCodex.Core;

namespace SpireCodex.Ui;

public partial class CompetitiveBadge : Node
{
    private double _accum;
    private CanvasLayer _layer = null!;
    private Control? _panel;
    private TextureRect? _icon;
    private Control? _anchor;
    private int _anchorWait;
    private static ImageTexture? _logo;
    private PanelContainer? _tip;
    private Label? _tipLabel;
    private string _tipText = "";
    private Competitive.State _shown = Competitive.State.Off;
    private static ImageTexture? _check, _cross;
    private CanvasItem? _seedLabel;
    private int _seedSearchWait;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; competitive badge not started");
            return;
        }
        var n = new CompetitiveBadge { Name = "SpireCodexCompetitive" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, n);
    }

    public override void _Ready()
    {
        _layer = new CanvasLayer { Layer = 198 };
        AddChild(_layer);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) Competitive.NoteQuit();
    }

    public override void _Process(double delta)
    {
        Hover();
        _accum += delta;
        if (_accum < 0.5) return;
        _accum = 0;

        Competitive.Sync();
        HideSeed();
        var verdict = Competitive.Current();
        if (verdict.State == Competitive.State.Off)
        {
            if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.Visible = false;
            _tipText = "";
            return;
        }

        EnsurePanel();
        if (verdict.State != _shown)
        {
            _icon!.Texture = verdict.State == Competitive.State.Cleared
                ? (_check ??= MakeIcon(true))
                : (_cross ??= MakeIcon(false));
            _shown = verdict.State;
        }
        _tipText = Describe(verdict);

        Place();
    }

    private void Place()
    {
        var panel = _panel!;
        var inRun = Producer.LiveStateProducer.Latest is { InRun: true };
        if (inRun && (_anchor == null || !GodotObject.IsInstanceValid(_anchor)) && _anchorWait-- <= 0)
        {
            _anchorWait = 3;
            _anchor = GetTree().Root.FindChild("RoomIcons", true, false) as Control;
        }
        if (inRun && _anchor != null && GodotObject.IsInstanceValid(_anchor))
        {
            panel.Visible = _anchor.IsVisibleInTree();
            var rect = _anchor.GetGlobalRect();
            var right = rect.End.X;
            if (_anchor.GetParent()?.GetNodeOrNull<Control>("Modifiers") is { } extra && extra.IsVisibleInTree())
                right = Mathf.Max(right, extra.GetGlobalRect().End.X);
            var side = rect.Size.Y * 0.56f;
            panel.Size = new Vector2(side, side);
            panel.Position = new Vector2(right + side * 0.3f, rect.Position.Y + (rect.Size.Y - side) / 2f);
            return;
        }
        var view = panel.GetViewportRect().Size;
        var scale = view.Y / 1080f;
        var size = 45f * scale;
        panel.Size = new Vector2(size, size);
        panel.Position = new Vector2((view.X - size) / 2f, (80f * scale - size) / 2f);
        panel.Visible = true;
    }

    private void Hover()
    {
        var over = _tipText.Length > 0 && _panel != null && GodotObject.IsInstanceValid(_panel)
            && _panel.Visible && _panel.GetGlobalRect().HasPoint(_panel.GetGlobalMousePosition());
        if (!over)
        {
            if (_tip != null && GodotObject.IsInstanceValid(_tip)) _tip.Visible = false;
            return;
        }
        if (_tip == null || !GodotObject.IsInstanceValid(_tip))
        {
            var style = new StyleBoxFlat
            {
                BgColor = Skin.Bg,
                BorderColor = Skin.Accent,
            };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(5);
            style.SetContentMarginAll(10);
            _tip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            _tip.AddThemeStyleboxOverride("panel", style);
            _tipLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
            _tip.AddChild(_tipLabel);
            _layer.AddChild(_tip);
        }
        var view = _panel!.GetViewportRect().Size;
        var scale = view.Y / 1080f;
        _tipLabel!.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(16f * scale));
        _tipLabel.AddThemeColorOverride("font_color", _shown == Competitive.State.Cleared
            ? Skin.Good : Skin.Danger);
        if (_tipLabel.Text != _tipText) _tipLabel.Text = _tipText;
        _tip.ResetSize();
        var rect = _panel.GetGlobalRect();
        var x = Mathf.Clamp(rect.Position.X + rect.Size.X / 2f - _tip.Size.X / 2f, 4f, view.X - _tip.Size.X - 4f);
        _tip.Position = new Vector2(x, rect.End.Y + 8f * scale);
        _tip.Visible = true;
    }

    private void HideSeed()
    {
        if (_seedLabel == null || !GodotObject.IsInstanceValid(_seedLabel))
        {
            _seedLabel = null;
            if (Producer.LiveStateProducer.Latest is not { InRun: true }) return;
            if (_seedSearchWait-- > 0) return;
            _seedSearchWait = 3;
            _seedLabel = GetTree().Root.FindChild("DebugSeed", true, false) as CanvasItem;
            if (_seedLabel == null) return;
        }
        var show = !Competitive.On;
        if (_seedLabel.Visible != show) _seedLabel.Visible = show;
    }

    public static string Describe(Competitive.Verdict verdict)
    {
        if (verdict.State == Competitive.State.Off) return "";
        string text;
        if (verdict.State == Competitive.State.Cleared)
            text = verdict.Detail == null
                ? Loc.T("comp_cleared")
                : Loc.T("comp_cleared") + "\n" + Loc.T("comp_bracket_" + verdict.Detail);
        else
        {
            var reason = Loc.T("comp_reason_" + verdict.Reason);
            if (verdict.Detail != null) reason = string.Format(reason, verdict.Detail);
            text = Loc.T("comp_flagged") + "\n" + reason;
        }
        if (verdict.Crashes > 0) text += "\n" + string.Format(Loc.T("comp_crashes"), verdict.Crashes);
        return text;
    }

    private void EnsurePanel()
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel)) return;
        _panel = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        var logo = new TextureRect
        {
            Texture = _logo ??= MakeLogo(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        logo.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _panel.AddChild(logo);
        _icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0.5f, AnchorTop = 0.5f, AnchorRight = 1.08f, AnchorBottom = 1.08f,
        };
        _panel.AddChild(_icon);
        _layer.AddChild(_panel);
        _shown = Competitive.State.Off;
    }

    private static ImageTexture MakeLogo()
    {
        const int size = 96;
        Image? img = null;
        try
        {
            var asm = typeof(CompetitiveBadge).Assembly;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith("codex_logo.png", System.StringComparison.Ordinal)) continue;
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) break;
                using var copy = new System.IO.MemoryStream();
                stream.CopyTo(copy);
                var loaded = new Image();
                if (loaded.LoadPngFromBuffer(copy.ToArray()) == Error.Ok) img = loaded;
                break;
            }
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Info($"competitive: logo load failed: {e.Message}");
        }
        img ??= Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Convert(Image.Format.Rgba8);
        img.Resize(size, size, Image.Interpolation.Lanczos);
        var c = (size - 1) / 2f;
        var rim = Skin.Accent;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var d = new Vector2(x - c, y - c).Length();
                if (d > c) img.SetPixel(x, y, new Color(0, 0, 0, 0));
                else if (d > c - 3f) img.SetPixel(x, y, rim);
                else
                {
                    var px = img.GetPixel(x, y);
                    img.SetPixel(x, y, new Color(px.R, px.G, px.B, 1f));
                }
            }
        return ImageTexture.CreateFromImage(img);
    }

    private static ImageTexture MakeIcon(bool ok)
    {
        const int size = 40;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        var fill = ok ? Skin.Good : Skin.Danger;
        var ink = Skin.Field;
        var c = (size - 1) / 2f;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (new Vector2(x - c, y - c).Length() <= c + 0.3f)
                    img.SetPixel(x, y, fill);
        if (ok)
        {
            Stroke(img, new Vector2(10, 20), new Vector2(17, 27), ink);
            Stroke(img, new Vector2(17, 27), new Vector2(29, 13), ink);
        }
        else
        {
            Stroke(img, new Vector2(12, 12), new Vector2(27, 27), ink);
            Stroke(img, new Vector2(27, 12), new Vector2(12, 27), ink);
        }
        return ImageTexture.CreateFromImage(img);
    }

    private static void Stroke(Image img, Vector2 from, Vector2 to, Color ink)
    {
        var steps = (int)(from.DistanceTo(to) * 3f);
        for (var i = 0; i <= steps; i++)
        {
            var p = from.Lerp(to, i / (float)steps);
            for (var dy = -2; dy <= 1; dy++)
                for (var dx = -2; dx <= 1; dx++)
                {
                    int x = Mathf.RoundToInt(p.X) + dx, y = Mathf.RoundToInt(p.Y) + dy;
                    if (x >= 0 && y >= 0 && x < img.GetWidth() && y < img.GetHeight())
                        img.SetPixel(x, y, ink);
                }
        }
    }
}
