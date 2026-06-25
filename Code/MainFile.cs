using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using SpireCodex.Api;
using SpireCodex.Producer;

namespace SpireCodex;

// Mod entry point. The Slay the Spire 2 modding API calls Initialize() on load
// (the [ModInitializer] attribute). This is NOT BepInEx: the game has a first-party
// loader that reads mods from <game>/mods/<ModName>/ and Harmony ships with the game.
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "SpireCodex";

    // Game logger; prefixes every line with the mod id, so no manual "[SpireCodex]" tag.
    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    // Held so it is not garbage-collected; it owns the run-file watcher.
    private static RunUploader? _uploader;

    public static void Initialize()
    {
        Logger.Info($"initializing (sts2 {Core.Sts2Version.Current})");

        // Load + register the in-game settings first so every later step reads the saved
        // values (constructing the config loads SpireCodex.cfg into the static properties).
        BaseLib.Config.ModConfigRegistry.Register(ModId, new SpireCodexConfig());

        // Version guard: warn when the game build is newer than tested, check for a newer
        // published mod build (both surfaced in the F9 overlay header).
        Api.ModVersion.Start(Core.Sts2Version.Current);

        // Anonymous daily-active-user ping (random install id, no Steam id / PII).
        Api.Telemetry.Start(Core.Sts2Version.Current);

        // Restore any saved Steam JWT so authenticated uploads work from launch, then start
        // the silent Steamworks-ticket sign-in (the only auth path; re-auths on expiry).
        Api.SteamAuth.LoadStored();
        Api.SteamTicketAuth.Start();

        var harmony = new Harmony(ModId);
        harmony.PatchAll();

        // Append our community stats into the game's own native card hover-tip set, so they
        // render inside the real tooltip widget instead of a separate panel.
        Core.NativeHoverTips.Apply(harmony);

        // Play-by-play capture off the game's first-party hook points; feeds the live
        // ticker that PresencePublisher ships with each heartbeat.
        Core.RunEvents.Apply(harmony);

        // Per-hit damage tracking off the game's damage hooks; feeds the combat damage meter,
        // the live snapshot's combat block, and the run-upload damage summary.
        Core.DamageTracker.Apply(harmony);

        // Live-state producer: a Node on the scene tree that polls RunState ~10x/second
        // and writes the snapshot file.
        LiveStateProducer.Start();

        // The in-game companion overlay panel (run info, leaderboards, runs, about).
        Ui.DeckImagePanel.Start();

        // Win% labels rendered directly on the card-reward selection screen.
        Ui.CardRewardHints.Start();

        // Community danger tags on the act-map nodes.
        Ui.MapDangerHints.Start();

        // Live combat damage meter (corner readout fed by DamageTracker via the snapshot).
        Ui.DamageMeter.Start();

        // Corner indicator for the active stat filter (All runs / Ascension 5+ / Ascension 10).
        Ui.StatFilterIndicator.Start();

        // Post-run shareable card (pops when a completed run uploads).
        Ui.RunCompleteCard.Start();

        // First-upload disclosure: when uploads are enabled but consent was never given,
        // ask before anything is sent (M3-C trust gate).
        Ui.ConsentPrompt.Start();

        // Pre-fetch community Codex Scores so the overlay can annotate the deck/relics,
        // plus community headline stats (portrait/removal tips) and the local personal
        // win-rate scan, so those hovers are instant.
        Api.CodexScores.EnsureLoaded();
        Api.CommunityStats.EnsureLoaded();
        Api.LocalStats.EnsureLoaded();
        Api.PersonalStats.EnsureLoaded(); // the signed-in player's own pick rates (no-op until a token lands)

        // Run uploader: watches the save tree for completed .run files and posts them
        // to the API (opt-in via Config.UploadRuns).
        _uploader = new RunUploader(Core.Sts2Version.Current);
        _uploader.Start();

        // Live presence heartbeat: feeds the site's "who is in a run now" view (gated by
        // upload consent + the ShareLiveStatus toggle + Steam sign-in).
        Api.PresencePublisher.Start();
    }
}
