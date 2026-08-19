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
    public static readonly Color Bg = Hex("171c24");          // card / panel body
    public static readonly Color BgSoft = Hex("1d2430");      // raised row
    public static readonly Color BgSofter = Hex("242a35");    // secondary button face
    public static readonly Color Border = Hex("2c313c");
    public static readonly Color Text = Hex("e8e3d6");
    public static readonly Color TextMuted = Hex("c8ccd5");
    public static readonly Color Accent = Hex("ffd34d");      // brand gold
    public static readonly Color AccentBright = Hex("ffe073"); // gold hover
    public static readonly Color AccentDim = Hex("d9ad33");    // gold pressed
    public static readonly Color Field = Hex("0f1217");        // inset field / checkbox interior
    public static readonly Color Good = Hex("4ec977");
    public static readonly Color Danger = Hex("c74b4b");

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
        var s = new StyleBoxFlat { BgColor = Bg with { A = 0.98f }, BorderColor = Accent };
        s.SetBorderWidthAll(2);
        s.SetCornerRadiusAll(8);
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
