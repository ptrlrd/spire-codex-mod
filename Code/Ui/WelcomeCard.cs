using System;
using System.IO;
using Godot;
using SpireCodex.Api;

namespace SpireCodex.Ui;

// First-time welcome (FTUE). Pops once on first launch to point new players at F5, the Settings
// tab, and the headline features — the discoverability gap ("people don't know about the
// settings"). Same native card style as ConsentPrompt, but shown only AFTER the consent choice
// is made so the two never stack. A "seen" marker under %APPDATA%/SpireCodex gates the auto-show;
// the Settings tab's "Show welcome again" button re-opens it on demand via ShowAgain().
public partial class WelcomeCard : CanvasLayer
{
    private static WelcomeCard? _instance;
    private PanelContainer _panel = null!;
    private double _sinceCheck;
    private bool _settled; // stop polling once shown or already-seen

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; welcome card not started");
            return;
        }
        var c = new WelcomeCard { Name = "SpireCodexWelcome" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, c);
    }

    // Re-open from the Settings "Show welcome again" button (deferred for thread safety).
    public static void ShowAgain() => Callable.From(() => _instance?.Open()).CallDeferred();

    public override void _Ready()
    {
        _instance = this;
        Layer = 215; // above the consent prompt (210), below the F5 panel (220)
        BuildUi();
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_settled || Visible) return;
        _sinceCheck += delta;
        if (_sinceCheck < 1.0) return;
        _sinceCheck = 0;

        if (SeenMarkerExists()) { _settled = true; return; } // shown on a prior launch
        // Show once, but only after the player has answered the consent prompt, so the two cards
        // never overlap. Mark seen on show so it never auto-pops again.
        if (Consent.Answered) { _settled = true; WriteSeenMarker(); Open(); }
    }

    private void Open() => Visible = true;

    private void BuildUi()
    {
        _panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.14f, 0.98f),
            BorderColor = new Color(1f, 0.827f, 0.302f, 1f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(8);
        style.ContentMarginLeft = 22; style.ContentMarginRight = 22;
        style.ContentMarginTop = 16; style.ContentMarginBottom = 16;
        _panel.AddThemeStyleboxOverride("panel", style);
        if (ThemeDB.GetProjectTheme() is { } theme) _panel.Theme = theme;
        AddChild(_panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(vbox);

        // Reflect the player's actual overlay hotkey (defaults to F5; stays right if rebound).
        var key = SpireCodexConfig.OverlayKey is var k and not HotKey.None ? k.ToString() : "F5";
        var text = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            CustomMinimumSize = new Vector2(520, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        text.AddThemeColorOverride("default_color", new Color(0.91f, 0.89f, 0.84f));
        text.Text =
            "[color=#ffd34d][b]Welcome to Spire[/b][/color] [color=#ffffff][b]Codex[/b][/color]\n" +
            $"Press [color=#d7a84a][b]{key}[/b][/color] anytime to open the overlay, your live run " +
            "dashboard, leaderboards, and [b]Settings[/b].\n\n" +
            "- Community win-rates and Codex tiers on every card, relic, and event tooltip\n" +
            "- A live damage meter, plus map route and danger guidance, during combat\n" +
            "- Open [b]Settings[/b] in the overlay to toggle any surface or pick a stat bracket\n\n" +
            "[color=#c8ccd5]Controller: click a stick to open, bumpers to switch tabs.[/color]";
        vbox.AddChild(text);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(row);

        var open = new Button { Text = "Open it now" };
        open.Pressed += () => { Visible = false; DeckImagePanel.OpenOverlay(); };
        row.AddChild(open);

        var gotIt = new Button { Text = "Got it" };
        gotIt.Pressed += () => Visible = false;
        row.AddChild(gotIt);
    }

    // --- "seen" marker (%APPDATA%/SpireCodex/welcome-seen) ---------------------------

    private static string? MarkerPath()
    {
        try
        {
            var dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SpireCodex");
            return Path.Combine(dir, "welcome-seen");
        }
        catch { return null; }
    }

    private static bool SeenMarkerExists()
    {
        var p = MarkerPath();
        return p != null && File.Exists(p);
    }

    private static void WriteSeenMarker()
    {
        try
        {
            var p = MarkerPath();
            if (p == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, DateTimeOffset.UtcNow.ToString("o"));
        }
        catch { /* best-effort; worst case it shows again next launch */ }
    }
}
