using Godot;
using SpireCodex.Api;

namespace SpireCodex.Ui;

// The upload disclosure (M3-C trust gate). One card, two modes:
//
//  - Onboarding, when nobody has answered yet (or uploads were switched on in settings without
//    a grant): the full "what gets uploaded" body with Turn on / Keep off. Turn on persists the
//    grant, which also lets RunUploader flush held runs; Keep off flips UploadRuns back off and
//    saves, so re-enabling in settings re-asks.
//
//  - Re-disclosure, when someone granted against an OLDER disclosure than the current one.
//    Consent.Answered is sticky for the life of a machine, so without this mode a change to
//    what we collect would apply silently to every existing player. Their run uploads are not
//    interrupted and their grant is not reset; the card just says what is new, and the newer
//    data stays held until they answer it. Added when replays shipped: agreeing to "completed
//    runs (character, deck, relics, score, seed, result)" in v1.0.10 is not agreement to a
//    per-decision journal of how you play.
public partial class ConsentPrompt : CanvasLayer
{
    private static ConsentPrompt? _instance;

    private PanelContainer _panel = null!;
    private RichTextLabel _body = null!;
    private HBoxContainer _onboardRow = null!;
    private HBoxContainer _redisclosureRow = null!;
    private double _sinceCheck;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; consent prompt not started");
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
        Skin.ApplyFont(_panel);
        AddChild(_panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(vbox);

        _body = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(520, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _body.AddThemeColorOverride("default_color", new Color(0.91f, 0.89f, 0.84f));
        _body.Text = Loc.F("consent_body", OverlayKeyName());
        vbox.AddChild(_body);

        _onboardRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _onboardRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(_onboardRow);

        var turnOn = new Button { Text = Loc.T("consent_turnon") };
        turnOn.Pressed += () =>
        {
            Visible = false;
            SpireCodexConfig.UploadRuns = true;
            BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();
            Consent.Grant();
            MainFile.Logger.Info("run tracking turned on");
        };
        _onboardRow.AddChild(turnOn);

        var keepOff = new Button { Text = Loc.T("consent_keepoff") };
        keepOff.Pressed += () =>
        {
            Visible = false;
            SpireCodexConfig.UploadRuns = false;
            // Turn replays off too, not just leave them inert. UploadReplays ships on so a
            // player who says yes gets them without a second question, but "Keep off" has to
            // leave the settings menu telling the truth: an Upload replays toggle reading ON
            // while nothing uploads is worse than the toggle being wrong. Turning run tracking
            // back on later re-asks, and that card names replays.
            SpireCodexConfig.UploadReplays = false;
            BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();
            Consent.Decline();
            MainFile.Logger.Info("run tracking kept off; replay uploads off");
        };
        _onboardRow.AddChild(keepOff);

        _redisclosureRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        _redisclosureRow.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(_redisclosureRow);

        var replaysOn = new Button { Text = Loc.T("consent_replays_ok") };
        replaysOn.Pressed += () =>
        {
            Visible = false;
            SpireCodexConfig.UploadReplays = true;
            BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();
            Consent.AcknowledgeDisclosure();
            MainFile.Logger.Info("replay uploads accepted");
        };
        _redisclosureRow.AddChild(replaysOn);

        // Declining replays leaves run uploads exactly as they were. Recording to disk also
        // stays on, so the player can change their mind later without having lost the runs in
        // between; the sweep picks them up when the toggle goes back on.
        var replaysOff = new Button { Text = Loc.T("consent_replays_off") };
        replaysOff.Pressed += () =>
        {
            Visible = false;
            SpireCodexConfig.UploadReplays = false;
            BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();
            Consent.AcknowledgeDisclosure();
            MainFile.Logger.Info("replay uploads declined; run uploads unchanged");
        };
        _redisclosureRow.AddChild(replaysOff);

        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (Visible) return;
        _sinceCheck += delta;
        if (_sinceCheck < 1.0) return;
        _sinceCheck = 0;
        if (!WelcomeCard.Done) return;
        // Show the onboarding choice once (until the player answers), AND re-show the
        // disclosure if they later enable uploads in settings without having granted. Once
        // granted, never again; once "Keep off" with uploads off, the condition is false.
        var onboarding = !Consent.Granted && (!Consent.Answered || Config.UploadRuns);
        // Re-disclosure only matters to someone already granted, and only until they answer it.
        var redisclose = !onboarding && Consent.NeedsRedisclosure;
        if (!onboarding && !redisclose) return;

        // Resolve the language now (the card is built at boot, possibly before the game's
        // LocManager was ready) and re-apply the body text before showing.
        Loc.Refresh();
        _body.Text = onboarding ? Loc.F("consent_body", OverlayKeyName()) : Loc.T("consent_replays_body");
        _onboardRow.Visible = onboarding;
        _redisclosureRow.Visible = redisclose;
        Visible = true;
    }

    private static string OverlayKeyName() =>
        SpireCodexConfig.OverlayKey is var k and not HotKey.None ? k.ToString() : "F5";
}
