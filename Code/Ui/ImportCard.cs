using Godot;

namespace SpireCodex.Ui;

// The "Import vanilla saves" flow as a standalone card, opened from the button we add to the
// game's profile-select screen. Same job as the Settings-tab section, but reachable at the exact
// moment a first-time modder notices their profiles are empty, which is where they actually need
// it. Native card chrome, matching ConsentPrompt and WelcomeCard.
public partial class ImportCard : CanvasLayer
{
    private static ImportCard? _instance;

    private RichTextLabel _body = null!;
    private Label _title = null!, _sourceLabel = null!, _targetLabel = null!;
    private Label _status = null!;
    private Button _import = null!, _cancel = null!;
    private OptionButton _source = null!;
    private OptionButton _target = null!;
    private bool _armed; // second press actually writes

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        var c = new ImportCard { Name = "SpireCodexImportCard" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, c);
    }

    // Opened by the profile-screen button (deferred for thread safety).
    public static void Open() => Callable.From(() => _instance?.OpenCard()).CallDeferred();

    public override void _Ready()
    {
        _instance = this;
        Layer = 216; // above the welcome card (215), below the F5 panel (220)
        BuildUi();
        Visible = false;
    }

    private void OpenCard()
    {
        // The card is built at boot, possibly before the game's LocManager was ready, so re-apply
        // every string here rather than trusting what was baked in at _Ready.
        Loc.Refresh();
        _title.Text = Loc.T("deck_settings_import").ToUpperInvariant();
        _body.Text = Loc.T("deck_settings_import_desc");
        _sourceLabel.Text = Loc.T("deck_import_source");
        _targetLabel.Text = Loc.T("deck_import_target");
        _cancel.Text = Loc.T("deck_import_cancel");
        Relabel(_source);
        Relabel(_target);
        Disarm();
        _status.Visible = false;
        Visible = true;
    }

    // Re-translate the profile entries in place, keeping the current selection.
    private static void Relabel(OptionButton opt)
    {
        for (var i = 0; i < opt.ItemCount; i++)
            opt.SetItemText(i, Loc.F("deck_import_profile", opt.GetItemId(i)));
    }

    private void BuildUi()
    {
        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
        };
        panel.AddThemeStyleboxOverride("panel", Skin.CardBox());
        Skin.ApplyFont(panel);
        AddChild(panel);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 10);
        panel.AddChild(vbox);

        // Brand lockup, same gold "Spire" + off-white "Codex" as the welcome card, so a card
        // that opens over the game's own profile screen is unmistakably ours.
        var brand = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            CustomMinimumSize = new Vector2(520, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = $"[color=#{Skin.Accent.ToHtml(false)}][b]Spire[/b][/color] [b]Codex[/b]",
        };
        brand.AddThemeColorOverride("default_color", Skin.Text);
        brand.AddThemeFontSizeOverride("normal_font_size", 18);
        brand.AddThemeFontSizeOverride("bold_font_size", 18);
        brand.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        brand.CustomMinimumSize = new Vector2(480, 0);

        // Brand on the left, close X on the right, same as the overlay header.
        var top = new HBoxContainer();
        top.AddChild(brand);
        top.AddChild(DeckImagePanel.CloseButton(() => { Disarm(); Visible = false; }));
        vbox.AddChild(top);

        var rule = new HSeparator();
        var ruleStyle = new StyleBoxFlat { BgColor = Skin.Border };
        ruleStyle.ContentMarginTop = 1; ruleStyle.ContentMarginBottom = 1;
        rule.AddThemeStyleboxOverride("separator", ruleStyle);
        vbox.AddChild(rule);

        _title = Skin.Head(Loc.T("deck_settings_import"));
        vbox.AddChild(_title);

        _body = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false,
            CustomMinimumSize = new Vector2(520, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _body.AddThemeColorOverride("default_color", Skin.Text);
        _body.Text = Loc.T("deck_settings_import_desc");
        vbox.AddChild(_body);

        _source = Picker();
        _target = Picker();
        vbox.AddChild(Row(Loc.T("deck_import_source"), _source, out _sourceLabel));
        vbox.AddChild(Row(Loc.T("deck_import_target"), _target, out _targetLabel));
        // Changing either picker invalidates an armed confirm, so a mis-click can't land on a
        // profile the player never confirmed.
        _source.ItemSelected += _ => Disarm();
        _target.ItemSelected += _ => Disarm();

        _status = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(520, 0),
            Visible = false,
        };
        _status.AddThemeFontSizeOverride("font_size", 12);
        vbox.AddChild(_status);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        row.AddThemeConstantOverride("separation", 10);
        vbox.AddChild(row);

        _import = new Button { Text = Loc.T("deck_import_button") };
        Skin.Primary(_import);
        _import.Pressed += OnImport;
        row.AddChild(_import);

        _cancel = new Button { Text = Loc.T("deck_import_cancel") };
        Skin.Secondary(_cancel);
        _cancel.Pressed += () => { Disarm(); Visible = false; };
        row.AddChild(_cancel);
    }

    private void OnImport()
    {
        var source = (int)_source.GetSelectedId();
        var target = (int)_target.GetSelectedId();

        if (Core.SaveImport.RunInProgress()) { Disarm(); Say(Loc.T("deck_import_in_run"), Skin.Danger); return; }
        if (!Core.SaveImport.VanillaProfileHasData(source))
        {
            Disarm();
            Say(Loc.F("deck_import_no_source", source), Skin.Danger);
            return;
        }

        if (!_armed)
        {
            _armed = true;
            _import.Text = Loc.T("deck_import_confirm");
            Say(Loc.F("deck_import_warn", target), Skin.Accent);
            return;
        }
        Disarm();

        var runs = Core.SaveImport.Import(source, target);
        if (runs < 0) { Say(Loc.T("deck_import_failed"), Skin.Danger); return; }

        // Reloading the main menu drops us back out of the profile screen showing the imported
        // data, so close the card first. If the reload misses, stay open and say to restart.
        if (Core.SaveImport.ReloadMainMenu())
        {
            Visible = false;
        }
        else
        {
            Say(Loc.F("deck_import_done_restart", source, target, runs), Skin.Good);
        }
    }

    private void Say(string text, Color color)
    {
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color);
        _status.Visible = true;
    }

    private void Disarm()
    {
        _armed = false;
        _import.Text = Loc.T("deck_import_button");
    }

    // Profile 1/2/3 picker. Item ids are the profile numbers, so the caller reads them directly.
    private static OptionButton Picker()
    {
        var opt = new OptionButton();
        Skin.Field_(opt);
        for (var i = 1; i <= Core.SaveImport.ProfileCount; i++)
            opt.AddItem(Loc.F("deck_import_profile", i), i);
        opt.Select(0);
        return opt;
    }

    private static Control Row(string label, Control field, out Label name)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        name = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        name.AddThemeColorOverride("font_color", Skin.TextMuted);
        name.AddThemeFontSizeOverride("font_size", 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(name);
        row.AddChild(field);
        return row;
    }
}
