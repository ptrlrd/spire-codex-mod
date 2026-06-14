using Godot;
using SpireCodex.Api;

namespace SpireCodex.Ui;

// The first-upload disclosure (M3-C trust gate). Watches for "uploads enabled but consent
// never answered" and pops a centered native-styled card explaining exactly what gets
// uploaded, with Allow / Not now. Allow persists the grant (Consent.Grant, which also lets
// RunUploader flush held runs); Not now flips the UploadRuns toggle back off and saves the
// config, so re-enabling it in settings re-asks.
public partial class ConsentPrompt : CanvasLayer
{
    private static ConsentPrompt? _instance;

    private PanelContainer _panel = null!;
    private double _sinceCheck;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            GD.Print("[SpireCodex] no SceneTree; consent prompt not started");
            return;
        }
        var c = new ConsentPrompt { Name = "SpireCodexConsentPrompt" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, c);
    }

    public override void _Ready()
    {
        _instance = this;
        Layer = 210; // above our plates (200) and the run card (150)

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

        var text = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(520, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        text.AddThemeColorOverride("default_color", new Color(0.91f, 0.89f, 0.84f));
        text.Text =
            "[color=#ffd34d][b]Spire[/b][/color] [color=#ffffff][b]Codex[/b][/color] " +
            "[color=#ffd966][b]run tracking[/b][/color]\n" +
            "Turn on run tracking to get a live, ranked, shareable page for every run, and to " +
            "power the community stats you see in-game. Off by default - your call:\n\n" +
            "- Completed runs (character, deck, relics, score, seed, result) are sent to " +
            "[color=#8fd0ff]spire-codex.com[/color]\n" +
            "- Runs are attributed to your Steam id and shown on public run pages and leaderboards\n" +
            "- Backfill history uploads your existing local runs once; Share live status shows " +
            "your in-progress run (character, floor, HP) on the site while you play\n\n" +
            "Keep it off? You can turn it on later under [b]Run Tracking - Upload runs[/b] in " +
            "the SpireCodex mod settings.";
        vbox.AddChild(text);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(row);

        var turnOn = new Button { Text = "Turn on" };
        turnOn.Pressed += () =>
        {
            Visible = false;
            SpireCodexConfig.UploadRuns = true;
            BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();
            Consent.Grant();
            GD.Print("[SpireCodex] run tracking turned on");
        };
        row.AddChild(turnOn);

        var keepOff = new Button { Text = "Keep off" };
        keepOff.Pressed += () =>
        {
            Visible = false;
            SpireCodexConfig.UploadRuns = false;
            BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();
            Consent.Decline();
            GD.Print("[SpireCodex] run tracking kept off");
        };
        row.AddChild(keepOff);

        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (Visible) return;
        _sinceCheck += delta;
        if (_sinceCheck < 1.0) return;
        _sinceCheck = 0;
        // Show the onboarding choice once (until the player answers), AND re-show the
        // disclosure if they later enable uploads in settings without having granted. Once
        // granted, never again; once "Keep off" with uploads off, the condition is false.
        if (!Consent.Granted && (!Consent.Answered || Config.UploadRuns)) Visible = true;
    }
}
