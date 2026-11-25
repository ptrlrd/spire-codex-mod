using Godot;

namespace SpireCodex.Ui;

// The mod's visual language, in one place: the palette plus the button/field/panel styles the
// F5 overlay uses. Shared with the extractor mod's kit, so both mods read as one product.
//
// This exists because the palette was drifting. Every new surface was re-deriving "close enough"
// golds and greys by hand, which is how we ended up with two different golds before. New UI
// should pull from here rather than write its own Color literals.
public static class Skin
{
    public static readonly Color Bg = Hex("132028");
    public static readonly Color BgSoft = Hex("0f1920");
    public static readonly Color BgSofter = Hex("1a2b35");
    public static readonly Color Border = Hex("2a4555");
    public static readonly Color Line = Hex("1e3040");
    public static readonly Color Link = Hex("45cfd8");
    public static readonly Color Text = Hex("e0ddd8");
    public static readonly Color TextMuted = Hex("bfc4ca");
    public static readonly Color Accent = Hex("e8b830");      // brand gold
    public static readonly Color AccentBright = Hex("fbd91e"); // gold hover
    public static readonly Color AccentDim = Hex("c19a24");    // gold pressed
    public static readonly Color Field = Hex("0a1118");        // inset field / checkbox interior
    public static readonly Color Good = Hex("34d399");
    public static readonly Color Danger = Hex("f87171");

    // Gold call-to-action: solid gold face, dark text. For the one action a surface exists for.
    public static void Primary(Button b)
    {
        b.AddThemeStyleboxOverride("normal", KitBox(Accent, Accent));
        b.AddThemeStyleboxOverride("hover", KitBox(AccentBright, AccentBright));
        b.AddThemeStyleboxOverride("pressed", KitBox(AccentDim, AccentDim));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        foreach (var p in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            b.AddThemeColorOverride(p, Bg);
        b.AddThemeFontSizeOverride("font_size", 14);
    }

    // Dark face, gold ring. For everything alongside the primary action.
    public static void Secondary(Button b)
    {
        b.AddThemeStyleboxOverride("normal", KitBox(BgSofter, Accent));
        b.AddThemeStyleboxOverride("hover", KitBox(Border, AccentBright));
        b.AddThemeStyleboxOverride("pressed", KitBox(Accent, Accent));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", Text);
        b.AddThemeColorOverride("font_hover_color", AccentBright);
        b.AddThemeColorOverride("font_pressed_color", Bg);
        b.AddThemeFontSizeOverride("font_size", 13);
    }

    // Dropdowns and other inset controls, matching the overlay's setting rows.
    public static void Field_(Control c)
    {
        c.AddThemeStyleboxOverride("normal", ButtonBox(BgSofter, Border));
        c.AddThemeStyleboxOverride("hover", ButtonBox(Border, Accent));
        c.AddThemeStyleboxOverride("pressed", ButtonBox(Border, Accent));
        c.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        c.AddThemeColorOverride("font_color", Text);
        c.AddThemeColorOverride("font_hover_color", AccentBright);
        c.AddThemeColorOverride("font_focus_color", Text);
        c.AddThemeFontSizeOverride("font_size", 13);
    }

    // The overlay's card shell: dark body, gold hairline, generous padding.
    public static StyleBoxFlat CardBox()
    {
        var s = new StyleBoxFlat { BgColor = Bg with { A = 0.98f }, BorderColor = Border };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(8);
        s.ShadowColor = new Color(0, 0, 0, 0.6f);
        s.ShadowSize = 18;
        s.ShadowOffset = new Vector2(0, 8);
        s.ContentMarginLeft = 22; s.ContentMarginRight = 22;
        s.ContentMarginTop = 16; s.ContentMarginBottom = 16;
        return s;
    }

    // ---- Kreon, the game's font ------------------------------------------------------
    //
    // STS2 does NOT set a project-wide theme (there is no gui/theme/custom in project.godot), so
    // ThemeDB.GetProjectTheme() hands back Godot's stock theme and stock font. The game gets Kreon
    // by referencing res://themes/kreon_*_shared.tres per scene and through its own MegaLabel
    // widgets. Our overlay builds plain Label/Button/RichTextLabel nodes, so unless we load those
    // font resources ourselves everything renders in Godot's default face and reads as bolted on.
    //
    // Loading them into one Theme and hanging it on a panel covers every descendant. Anything the
    // theme doesn't define keeps resolving up the normal chain, so this only changes the font.
    private static readonly string[] RegularPaths =
    {
        "res://themes/kreon_regular_shared.tres",
        "res://themes/kreon_regular_glyph_space_one.tres",
        "res://fonts/kreon_regular.ttf",
    };
    private static readonly string[] BoldPaths =
    {
        "res://themes/kreon_bold_shared.tres",
        "res://themes/kreon_bold_glyph_space_one.tres",
        "res://fonts/kreon_bold.ttf",
    };

    public static Font? Bold { get; private set; }

    private static Theme? _gameTheme;
    private static bool _themeResolved;

    // Applies Kreon to a control and everything under it. No-op when the game's fonts can't be
    // found (renamed resources, no-pck dev run), which just leaves the stock font in place.
    public static void ApplyFont(Control c)
    {
        if (GameTheme() is { } theme) c.Theme = theme;
    }

    private static Theme? GameTheme()
    {
        if (_themeResolved) return _gameTheme;
        _themeResolved = true;

        var regular = LoadFont(RegularPaths);
        if (regular == null)
        {
            MainFile.Logger.Info("skin: Kreon not found; using the stock font");
            return null;
        }
        var bold = LoadFont(BoldPaths) ?? regular;

        var theme = new Theme { DefaultFont = regular };
        // Per-type entries as well as the default, because RichTextLabel resolves "normal_font"
        // and "bold_font" rather than the theme default; without the bold entry, [b] in our
        // BBCode strings would silently stop rendering bold.
        foreach (var type in new[] { "Label", "Button", "OptionButton", "CheckBox", "MenuButton", "LinkButton" })
            theme.SetFont("font", type, regular);
        theme.SetFont("normal_font", "RichTextLabel", regular);
        theme.SetFont("bold_font", "RichTextLabel", bold);
        theme.SetFont("italics_font", "RichTextLabel", regular);
        theme.SetFont("bold_italics_font", "RichTextLabel", bold);

        Bold = bold;
        _gameTheme = theme;
        return _gameTheme;
    }

    private static Font? LoadFont(string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (!ResourceLoader.Exists(path)) continue;
                if (ResourceLoader.Load(path) is Font f) return f;
            }
            catch { /* try the next candidate */ }
        }
        return null;
    }

    // The small uppercase gold section heading used across the overlay's tabs.
    public static Label Head(string title)
    {
        var l = new Label { Text = title.ToUpperInvariant() };
        l.AddThemeColorOverride("font_color", Accent);
        l.AddThemeFontSizeOverride("font_size", 12);
        return l;
    }

    public static CheckBox Check(string label, bool on)
    {
        var cb = new CheckBox { Text = label, ButtonPressed = on };
        cb.AddThemeFontSizeOverride("font_size", 14);
        cb.AddThemeColorOverride("font_color", Text);
        cb.AddThemeColorOverride("font_hover_color", AccentBright);
        cb.AddThemeColorOverride("font_pressed_color", Text);
        cb.AddThemeColorOverride("font_hover_pressed_color", AccentBright);
        cb.AddThemeColorOverride("font_focus_color", Text);
        cb.AddThemeConstantOverride("h_separation", 10);
        cb.AddThemeIconOverride("unchecked", CheckIcon(false));
        cb.AddThemeIconOverride("checked", CheckIcon(true));
        cb.AddThemeIconOverride("unchecked_disabled", CheckIcon(false));
        cb.AddThemeIconOverride("checked_disabled", CheckIcon(true));
        cb.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        return cb;
    }

    private static ImageTexture? _checkOn, _checkOff;
    private static ImageTexture CheckIcon(bool on) => on ? (_checkOn ??= MakeCheckIcon(true)) : (_checkOff ??= MakeCheckIcon(false));
    private static ImageTexture MakeCheckIcon(bool check)
    {
        const int size = 20, border = 2, inset = 5;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        img.Fill(Accent);
        img.FillRect(new Rect2I(border, border, size - 2 * border, size - 2 * border), Field);
        if (check)
            img.FillRect(new Rect2I(inset, inset, size - 2 * inset, size - 2 * inset), Accent);
        return ImageTexture.CreateFromImage(img);
    }

    public static Label Muted(string text, int size = 12)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        l.AddThemeColorOverride("font_color", TextMuted);
        l.AddThemeFontSizeOverride("font_size", size);
        return l;
    }

    public static Label SectionHead(string title)
    {
        var l = new Label { Text = title.ToUpperInvariant() };
        l.AddThemeColorOverride("font_color", TextMuted);
        l.AddThemeFontSizeOverride("font_size", 12);
        return l;
    }

    public static Label Title(string text)
    {
        var l = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        l.AddThemeColorOverride("font_color", Text);
        l.AddThemeFontSizeOverride("font_size", 16);
        if (Bold != null) l.AddThemeFontOverride("font", Bold);
        return l;
    }

    public static void Tab(Button b, bool active)
    {
        var box = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = active ? Accent : new Color(0, 0, 0, 0) };
        box.BorderWidthBottom = 3;
        box.ContentMarginLeft = 14; box.ContentMarginRight = 14;
        box.ContentMarginTop = 8; box.ContentMarginBottom = 6;
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed" })
            b.AddThemeStyleboxOverride(st, box);
        var focus = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = Link };
        focus.SetBorderWidthAll(1);
        focus.SetCornerRadiusAll(4);
        b.AddThemeStyleboxOverride("focus", focus);
        b.AddThemeColorOverride("font_color", active ? Accent : TextMuted);
        b.AddThemeColorOverride("font_hover_color", active ? Accent : Text);
        b.AddThemeColorOverride("font_pressed_color", Accent);
        b.AddThemeColorOverride("font_focus_color", active ? Accent : Text);
        b.AddThemeFontSizeOverride("font_size", 15);
        if (active && Bold != null) b.AddThemeFontOverride("font", Bold);
        else b.RemoveThemeFontOverride("font");
    }

    public static void Segment(Button b, bool active)
    {
        var face = active ? Accent : BgSoft;
        var normal = ButtonBox(face, active ? Accent : Border);
        normal.ContentMarginTop = 6; normal.ContentMarginBottom = 6;
        var hover = ButtonBox(active ? AccentBright : BgSofter, active ? AccentBright : Accent);
        hover.ContentMarginTop = 6; hover.ContentMarginBottom = 6;
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", normal);
        b.AddThemeStyleboxOverride("disabled", normal);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        foreach (var p in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_disabled_color" })
            b.AddThemeColorOverride(p, active ? Field : TextMuted);
        if (!active) b.AddThemeColorOverride("font_hover_color", Text);
        b.AddThemeFontSizeOverride("font_size", 13);
    }

    public static CheckButton Switch(bool on)
    {
        var sw = new CheckButton { ButtonPressed = on, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        foreach (var icon in new[] { "checked", "checked_disabled", "checked_mirrored", "checked_disabled_mirrored" })
            sw.AddThemeIconOverride(icon, SwitchIcon(true));
        foreach (var icon in new[] { "unchecked", "unchecked_disabled", "unchecked_mirrored", "unchecked_disabled_mirrored" })
            sw.AddThemeIconOverride(icon, SwitchIcon(false));
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
            sw.AddThemeStyleboxOverride(st, new StyleBoxEmpty());
        var focus = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = Link };
        focus.SetBorderWidthAll(1);
        focus.SetCornerRadiusAll(12);
        sw.AddThemeStyleboxOverride("focus", focus);
        return sw;
    }

    private static ImageTexture? _switchOn, _switchOff;
    private static ImageTexture SwitchIcon(bool on) => on ? (_switchOn ??= MakeSwitchIcon(true)) : (_switchOff ??= MakeSwitchIcon(false));
    private static ImageTexture MakeSwitchIcon(bool on)
    {
        const int w = 42, h = 24;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var track = on ? Hex("3d3212") : Field;
        var rim = on ? Accent : Border;
        var knob = on ? Accent : TextMuted;
        var knobX = on ? w - 12f : 12f;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var px = x + 0.5f;
            var py = y + 0.5f;
            var cx = Mathf.Clamp(px, h / 2f, w - h / 2f);
            var d = new Vector2(px - cx, py - h / 2f).Length();
            var c = new Color(0, 0, 0, 0);
            if (d <= h / 2f) c = d > h / 2f - 1.5f ? rim : track;
            if (new Vector2(px - knobX, py - h / 2f).Length() <= 8.5f) c = knob;
            img.SetPixel(x, y, c);
        }
        return ImageTexture.CreateFromImage(img);
    }

    private static ImageTexture? _logo;
    private static bool _logoResolved;
    public static ImageTexture? Logo()
    {
        if (_logoResolved) return _logo;
        _logoResolved = true;
        try
        {
            var asm = typeof(Skin).Assembly;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.EndsWith("codex_logo.png", System.StringComparison.Ordinal)) continue;
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) break;
                using var copy = new System.IO.MemoryStream();
                stream.CopyTo(copy);
                var img = new Image();
                if (img.LoadPngFromBuffer(copy.ToArray()) == Error.Ok) _logo = ImageTexture.CreateFromImage(img);
                break;
            }
        }
        catch (System.Exception e)
        {
            MainFile.Logger.Info($"skin: logo load failed: {e.Message}");
        }
        return _logo;
    }

    public static StyleBoxFlat RowBox(bool active)
    {
        var s = new StyleBoxFlat
        {
            BgColor = active ? Accent with { A = 0.12f } : BgSoft,
            BorderColor = active ? Accent : Border,
        };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(6);
        s.ContentMarginLeft = 12; s.ContentMarginRight = 12;
        s.ContentMarginTop = 8; s.ContentMarginBottom = 8;
        return s;
    }

    public static void Pill(Button b, bool active)
    {
        var normal = new StyleBoxFlat { BgColor = active ? Accent with { A = 0.15f } : new Color(0, 0, 0, 0) };
        normal.SetCornerRadiusAll(4);
        normal.ContentMarginLeft = 10; normal.ContentMarginRight = 10;
        normal.ContentMarginTop = 4; normal.ContentMarginBottom = 4;
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed" })
            b.AddThemeStyleboxOverride(st, normal);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", active ? Accent : TextMuted);
        b.AddThemeColorOverride("font_hover_color", active ? Accent : Text);
        b.AddThemeColorOverride("font_pressed_color", Accent);
        b.AddThemeFontSizeOverride("font_size", 13);
    }

    public static StyleBoxFlat KitBox(Color bg, Color border)
    {
        var s = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        s.SetBorderWidthAll(2);
        s.SetCornerRadiusAll(6);
        s.ContentMarginLeft = 16; s.ContentMarginRight = 16;
        s.ContentMarginTop = 7; s.ContentMarginBottom = 7;
        return s;
    }

    public static StyleBoxFlat ButtonBox(Color bg, Color border)
    {
        var s = new StyleBoxFlat { BgColor = bg, BorderColor = border };
        s.SetBorderWidthAll(1);
        s.SetCornerRadiusAll(4);
        s.ContentMarginLeft = 12; s.ContentMarginRight = 12;
        s.ContentMarginTop = 4; s.ContentMarginBottom = 4;
        return s;
    }

    public static Color Hex(string rgb) => new(
        System.Convert.ToInt32(rgb.Substring(0, 2), 16) / 255f,
        System.Convert.ToInt32(rgb.Substring(2, 2), 16) / 255f,
        System.Convert.ToInt32(rgb.Substring(4, 2), 16) / 255f);
}
