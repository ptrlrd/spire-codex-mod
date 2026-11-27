using Godot;

namespace SpireCodex.Autopilot;

// A small corner readout of the engine's advice: the chosen map route, the fight action it is
// playing and the reward it would take. Shown only while the autopilot is on or has advice.
public partial class AdviceHud : Node
{
    private static readonly Color Bg = Color.FromHtml("171c24");
    private static readonly Color Border = Color.FromHtml("2c313c");
    private const string Accent = "#ffd34d";
    private const string Text = "#e8e3d6";
    private const string Muted = "#c8ccd5";

    private CanvasLayer _layer = null!;
    private RichTextLabel? _panel;
    private double _accum;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        tree.Root.CallDeferred(Node.MethodName.AddChild, new AdviceHud { Name = "SpireCodexAdviceHud" });
    }

    public override void _Ready()
    {
        _layer = new CanvasLayer { Layer = 198 };
        AddChild(_layer);
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < 0.25) return;
        _accum = 0;
        if (Advice.Status == "off" && Advice.Updated == System.DateTimeOffset.MinValue)
        {
            if (_panel is not null) _panel.Visible = false;
            return;
        }
        _panel ??= Build();
        _panel.Visible = true;
        _panel.Text =
            $"[color={Accent}]Spire Codex autopilot[/color] [color={Muted}]{Advice.Status}[/color]\n" +
            $"[color={Text}]map[/color] [color={Muted}]{Advice.Map}[/color]\n" +
            $"[color={Text}]fight[/color] [color={Muted}]{Advice.Combat}[/color]\n" +
            $"[color={Text}]pick[/color] [color={Muted}]{Advice.Picks}[/color]";
    }

    private RichTextLabel Build()
    {
        var panel = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(420, 0),
            Position = new Vector2(16, 120),
        };
        var style = new StyleBoxFlat { BgColor = Bg, BorderColor = Border };
        style.SetBorderWidthAll(1);
        style.SetContentMarginAll(8);
        panel.AddThemeStyleboxOverride("normal", style);
        _layer.AddChild(panel);
        return panel;
    }
}
