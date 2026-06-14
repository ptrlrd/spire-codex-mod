using BaseLib.Config;
using Godot;

namespace SpireCodex;

// In-game settings, shown in BaseLib's mod settings menu. Properties must be public static
// get/set; BaseLib reads/writes them by reflection and persists to
// userdata/mod_configs/SpireCodex.cfg. Registered once in MainFile.Initialize.
//
// Labels currently show the raw property/section names (no loc table is shipped yet); a
// small loc table can prettify them later without touching this class.
public sealed class SpireCodexConfig : SimpleModConfig
{
    // --- Run tracking ---------------------------------------------------------------
    [ConfigSection("RunTracking")]
    // Upload completed runs to spire-codex.com, attributed to your Steam id. Default OFF:
    // the first-run onboarding prompt (ConsentPrompt) presents the choice ("Turn on" /
    // "Keep off") and nothing uploads unless the player turns it on there or in settings.
    public static bool UploadRuns { get; set; } = false;

    // One-time backfill of existing local run history once uploads are enabled.
    public static bool BackfillHistory { get; set; } = true;

    // Show your in-progress run on the site's live view while you play. Heartbeats every
    // ~30s; needs upload consent + Steam sign-in, and nothing is sent outside a run.
    public static bool ShareLiveStatus { get; set; } = true;

    // --- In-game UI -----------------------------------------------------------------
    // Every surface is individually toggleable and takes effect immediately (no restart).
    [ConfigSection("Overlay")]
    // Gates the overlay panel (run info, leaderboards, your runs, about) opened by OverlayKey.
    public static bool ShowDeckView { get; set; } = true;
    // Codex Score / win% plates drawn on the card-reward and shop screens.
    public static bool ShowCardRewardHints { get; set; } = true;
    // Community stats appended into the game's own hover tooltips (cards, relics, potions,
    // events, shop, campfire options, your character portrait).
    public static bool ShowHoverTips { get; set; } = true;
    // Map guidance: the recommended-route rings + fight-name labels + danger marks.
    public static bool ShowMapDanger { get; set; } = true;
    // The "Upcoming events" readout in the corner of the act map.
    public static bool ShowUpcomingEvents { get; set; } = true;
    // The shareable "Run tracked" card that pops when a completed run uploads.
    public static bool ShowPostRunCard { get; set; } = true;

    // --- Hotkeys --------------------------------------------------------------------
    [ConfigSection("Hotkeys")]
    // The single overlay hotkey (the companion panel; was two keys before consolidation).
    public static HotKey OverlayKey { get; set; } = HotKey.F5;

    // No Account section: Steam sign-in is fully silent (SteamTicketAuth exchanges a
    // Steamworks ticket for the API JWT at launch and re-auths when it expires). Privacy
    // is controlled by the UploadRuns / ShareLiveStatus toggles, not by auth state.

    // Resolved Godot keycode (read-only, so BaseLib ignores it as a config entry).
    public static Key OverlayKeycode => KeyOf(OverlayKey);

    private static Key KeyOf(HotKey k) => k switch
    {
        HotKey.F5 => Key.F5,
        HotKey.F6 => Key.F6,
        HotKey.F7 => Key.F7,
        HotKey.F8 => Key.F8,
        HotKey.F9 => Key.F9,
        HotKey.F10 => Key.F10,
        HotKey.F11 => Key.F11,
        HotKey.F12 => Key.F12,
        _ => Key.None,
    };
}

// A small, dropdown-friendly set of bindable keys (vs. the full Godot.Key enum).
public enum HotKey { None, F5, F6, F7, F8, F9, F10, F11, F12 }
