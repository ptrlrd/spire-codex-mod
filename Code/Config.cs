using System.IO;

namespace SpireCodex;

// Mod configuration. Phase 1 should load this from a json in the mod folder and
// expose a BaseLib settings UI. For now these are in-memory defaults.
public static class Config
{
    // Always production for run submissions; beta rejects them (DISABLE_RUN_SUBMISSIONS).
    public const string ApiBase = "https://spire-codex.com/api";

    // Public site base (for shareable run page URLs).
    public const string SiteBase = "https://spire-codex.com";
    public static string RunUrl(string runHash) => $"{SiteBase}/runs/{runHash}";

    // User-facing toggles live in the in-game settings (BaseLib). These delegate so the rest
    // of the code keeps reading Config.* as the single source. See SpireCodexConfig.
    //  - UploadRuns: opt-in run upload (default OFF until the player enables it).
    //  - BackfillOnce: one-time backfill of existing history (marker
    //    %APPDATA%/SpireCodex/backfill-<steamid>.done so it only runs once).
    public static bool UploadRuns => SpireCodexConfig.UploadRuns;
    public static bool BackfillOnce => SpireCodexConfig.BackfillHistory;

    // Run attribution (?steam_id / ?username). Set after Steam sign-in (Phase 3).
    public static string? SteamId { get; set; }
    public static string? Username { get; set; }

    // Where the live-state snapshot is written for the overlay/desktop app to read.
    public static string LiveStatePath { get; } =
        Path.Combine(Path.GetTempPath(), "spire-codex-live-state.json");

    // Dev-only: when true, Introspect.cs dumps live type surfaces once (general + combat) to
    // spire-codex-introspect*.txt. Flip on to re-confirm member names after a game patch, or to
    // capture Monster.NextMove for exact intent damage/hits.
    public static bool DumpIntrospection { get; set; } = false;

}
