using Godot;

namespace SpireCodex.Ui;

// The signature post-run moment: when a completed run uploads, a native-styled toast pops
// up with the live, shareable run-page URL and a copy-link action, then auto-dismisses.
// Triggered from RunUploader (any thread) via ShowRunDeferred.
public partial class RunCompleteCard : CanvasLayer
{
    private const double DismissSeconds = 16;

    private static RunCompleteCard? _instance;

    private PanelContainer _panel = null!;
    private RichTextLabel _text = null!;
    private string _url = "";
    private double _elapsed;
    private bool _showing;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; run card not started");
            return;
        }
        var c = new RunCompleteCard { Name = "SpireCodexRunCard" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, c);
    }

    // Safe to call from a background thread (the uploader runs off the main loop).
    public static void ShowRunDeferred(string url, string? rankLine = null, string? damageLine = null)
        => Callable.From(() => _instance?.ShowRun(url, rankLine, damageLine)).CallDeferred();

    public override void _Ready()
    {
        _instance = this;
        Layer = 150;

        _panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1f, AnchorBottom = 1f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Begin,
            OffsetBottom = -40,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.14f, 0.98f),
            BorderColor = new Color(1f, 0.827f, 0.302f, 1f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(8);
        style.ContentMarginLeft = 18; style.ContentMarginRight = 18;
        style.ContentMarginTop = 12; style.ContentMarginBottom = 12;
        _panel.AddThemeStyleboxOverride("panel", style);
        if (ThemeDB.GetProjectTheme() is { } theme) _panel.Theme = theme;
        AddChild(_panel);

        var vbox = new VBoxContainer();
        _panel.AddChild(vbox);

        _text = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(440, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _text.AddThemeColorOverride("default_color", new Color(0.91f, 0.89f, 0.84f));
        vbox.AddChild(_text);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        vbox.AddChild(row);

        var open = new Button { Text = Loc.T("rc_open") };
        open.Pressed += () => OS.ShellOpen(_url);
        row.AddChild(open);

        var copy = new Button { Text = Loc.T("rc_copy_link") };
        copy.Pressed += () => DisplayServer.ClipboardSet(_url);
        row.AddChild(copy);

        var dismiss = new Button { Text = Loc.T("rc_dismiss") };
        dismiss.Pressed += Dismiss;
        row.AddChild(dismiss);

        Visible = false;
    }

    public void ShowRun(string url, string? rankLine = null, string? damageLine = null)
    {
        if (!SpireCodexConfig.ShowPostRunCard) return;
        _url = url;
        var rank = string.IsNullOrEmpty(rankLine)
            ? ""
            : $"\n[color=#ffd966][b]{rankLine}[/b][/color]";
        var damage = string.IsNullOrEmpty(damageLine)
            ? ""
            : $"\n[color=#ff9a7a]{damageLine}[/color]";
        _text.Text =
            $"[color=#ffd34d][b]{Loc.T("rc_run_tracked")}[/b][/color]\n" +
            Loc.T("rc_live_on") + "\n" +
            $"[color=#8fd0ff]{url}[/color]" + rank + damage;
        Visible = true;
        _showing = true;
        _elapsed = 0;
    }

    private void Dismiss()
    {
        Visible = false;
        _showing = false;
    }

    public override void _Process(double delta)
    {
        if (!_showing) return;
        _elapsed += delta;
        if (_elapsed >= DismissSeconds) Dismiss();
    }
}
