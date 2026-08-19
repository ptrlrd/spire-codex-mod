using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace SpireCodex.Ui;

// Adds a "Spire Codex Settings" entry to the main menu, so everything the mod offers is one click
// from the front page instead of buried in the game's Mod Configuration list. It opens the F5
// overlay on its Settings tab; the other tabs (Leaderboard, Runs, Import, About) are right there.
//
// Cloning the game's own Settings button is what keeps it looking native: it inherits the menu's
// font, hover animation, focus wiring and layout for free. Same approach the extractor mod uses,
// which in turn follows OceanUwU/sts2-exporter (MIT).
//
// The duplicate is taken in the prefix, before NMainMenu._Ready connects the real button's
// signals, so we get a clean copy rather than one wired to the game's settings screen.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class MainMenuEntry
{
    private const string SettingsButtonPath = "MainMenuTextButtons/SettingsButton";

    private static NMainMenuTextButton? _button;

    public static void Prefix(NMainMenu __instance)
    {
        _button = null;
        try
        {
            if (__instance.GetNodeOrNull<NMainMenuTextButton>(SettingsButtonPath) is { } settings)
                _button = (NMainMenuTextButton)settings.Duplicate();
            else
                MainFile.Logger.Info($"main menu: no {SettingsButtonPath}; skipping our entry");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"main menu entry not cloned: {e.Message}");
        }
    }

    public static void Postfix(NMainMenu __instance)
    {
        if (_button is not { } button) return;
        try
        {
            if (__instance.GetNodeOrNull<NMainMenuTextButton>(SettingsButtonPath) is not { } settings)
                return;

            settings.AddSibling(button);

            // The label is the button's first child; set it directly instead of via
            // SetLocalization, which would look the text up in the game's own loc tables.
            Loc.Refresh();
            if (button.GetChildOrNull<MegaLabel>(0) is { } label)
                label.Text = Loc.T("menu_entry");

            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(OnPressed));
        }
        catch (Exception e)
        {
            // The main menu is the game's; a mod entry is never worth taking it down for.
            MainFile.Logger.Info($"main menu entry not added: {e.Message}");
            _button = null;
        }
    }

    private static void OnPressed(NButton _) => DeckImagePanel.OpenSettings();
}
