using Godot;

namespace SpireCodex.Ui;

// Puts an "Import vanilla saves" button on the game's profile-select screen, which is where a
// first-time modder actually notices their profiles are empty. Opens ImportCard; the same import
// also lives in the F5 Settings tab for anyone who goes looking there.
//
// Deliberately additive: we add one child to the screen and never touch the game's own nodes.
// (The prior art reparents the delete buttons into new containers to sit an import button beside
// each one, which is where its crash reports point.) The button is only added when there is
// actually an unmodded profile to import, so a clean install never sees it.
public static class ProfileImportButton
{
    private const string NodeName = "SpireCodexImportButton";

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            MainFile.Logger.Info("no SceneTree; profile import button not started");
            return;
        }
        tree.NodeAdded += OnNodeAdded;
    }

    private static void OnNodeAdded(Node node)
    {
        if (node is not Control screen || screen.Name != "ProfileScreen") return;

        // The screen is rebuilt each time it opens, so this fires again on every visit.
        if (screen.IsNodeReady()) Attach(screen);
        else screen.Ready += () => Attach(screen);
    }

    private static void Attach(Control screen)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(screen)) return;
            if (screen.HasNode(NodeName)) return; // already attached to this instance
            if (!AnyVanillaSaves()) return;       // nothing to import, so no button

            // A full-width strip across the bottom, with the button centred in it. Letting the
            // container do the sizing avoids a zero-width button, which is what you get from
            // anchoring a bare Control to a single point. Ignore mouse on the strip itself so it
            // can't eat clicks meant for the screen behind it.
            var holder = new CenterContainer
            {
                Name = NodeName,
                AnchorLeft = 0f, AnchorRight = 1f, AnchorTop = 1f, AnchorBottom = 1f,
                GrowHorizontal = Control.GrowDirection.Both,
                GrowVertical = Control.GrowDirection.Begin,
                OffsetTop = -132, OffsetBottom = -64,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };

            // The gold CTA from the overlay's kit: this is the one thing the strip exists for,
            // and it needs to read as ours against the game's own chrome.
            var button = new Button { Text = Loc.T("deck_settings_import") };
            Skin.Primary(button);
            button.AddThemeFontSizeOverride("font_size", 15);
            button.Pressed += ImportCard.Open;

            // A small gold "SPIRE CODEX" tag above it, so it is obvious which mod is offering
            // this rather than looking like a stray game button.
            var brand = Skin.Head("Spire Codex");
            brand.HorizontalAlignment = HorizontalAlignment.Center;

            var stack = new VBoxContainer();
            Skin.ApplyFont(stack); // the menu around us is Kreon; match it
            stack.AddThemeConstantOverride("separation", 4);
            stack.AddChild(brand);
            stack.AddChild(button);

            holder.AddChild(stack);
            screen.AddChild(holder);
        }
        catch (System.Exception e)
        {
            // The profile screen is the game's, not ours; never take it down over a button.
            MainFile.Logger.Info($"profile import button not attached: {e.Message}");
        }
    }

    private static bool AnyVanillaSaves()
    {
        for (var i = 1; i <= Core.SaveImport.ProfileCount; i++)
            if (Core.SaveImport.VanillaProfileHasData(i))
                return true;
        return false;
    }
}
