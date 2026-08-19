using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;

namespace SpireCodex.Core;

// Copies a vanilla (unmodded) save profile into a modded one.
//
// Slay the Spire 2 keeps modded progress completely separate. The only divergence in the game's
// own path code is UserDataPathProvider.GetProfileDir(), which prepends "modded/" while mods are
// loaded, so a first-time modded player looks like a brand new account: no unlocks, no ancient
// stats, no run history.
//
// Copying the files by hand does NOT stick for most players. NGame.GameStartup runs a cloud sync
// before it reads any save, and CloudSaveStore deletes local files that have no counterpart in
// the cloud ("Deleting {path} because it does not exist on remote"). Anything dropped on disk
// with File.Copy is reaped on the next launch, and the game talks to the Steam Remote Storage
// API rather than Steam's auto file rules, so nothing on disk gets uploaded on its own.
//
// So we go through the game's own save managers instead: turn IsRunningModded off, let the game
// load the vanilla profile into memory, turn it back on, then ask the game to save. Those writes
// run through CloudSaveStore, which puts every file local AND in the cloud, so the import
// survives the next boot's sync.
//
// Vanilla is only ever read. We set _currentProfileId directly rather than calling
// SwitchProfileId for the read half, because SwitchProfileId writes profile.save as a side
// effect and that would touch the vanilla file.
internal static class SaveImport
{
    internal const int ProfileCount = 3;

    // True while a run is loaded. Importing mid-run would swap the progress out from under it.
    internal static bool RunInProgress()
    {
        try { return Reflect.GetMember(GameNode(), "CurrentRunNode") != null; }
        catch { return false; }
    }

    internal static bool VanillaProfileHasData(int profileId) => HasProgress(ProfileDir(profileId, modded: false));

    internal static bool ModdedProfileHasData(int profileId) => HasProgress(ProfileDir(profileId, modded: true));

    private static bool HasProgress(string? dir) =>
        dir != null && File.Exists(Path.Combine(dir, "saves", "progress.save"));

    private static string? ProfileDir(int profileId, bool modded)
    {
        if (Api.RunUploader.FindSaveRoot() is not { } root) return null;
        return modded
            ? Path.Combine(root, "modded", $"profile{profileId}")
            : Path.Combine(root, $"profile{profileId}");
    }

    // Copies vanilla profile <source> onto modded profile <target>: progress (unlocks, ancient
    // stats), prefs, the account profile record, and every finished run in history/.
    // Returns the number of history runs copied, or -1 on failure.
    internal static int Import(int source, int target)
    {
        try
        {
            var save = SaveManager.Instance;
            if (save == null) return -1;

            // Guard here too, not just in the UI: the save managers happily invent a blank save
            // when the file is missing, and writing THAT over the modded profile would wipe it.
            if (!VanillaProfileHasData(source))
            {
                MainFile.Logger.Info($"save import: vanilla profile {source} has no progress.save; refusing");
                return -1;
            }

            var store = Field(save, "_saveStore") as ISaveStore;
            var profileMgr = Field(save, "_profileSaveManager");
            var idField = AccessTools.Field(save.GetType(), "_currentProfileId");
            if (store == null || profileMgr == null || idField == null)
            {
                MainFile.Logger.Info("save import: SaveManager internals not found; game version changed?");
                return -1;
            }

            // --- read the vanilla side into memory -------------------------------------
            var history = new Dictionary<string, string>();
            var wasModded = UserDataPathProvider.IsRunningModded;
            try
            {
                UserDataPathProvider.IsRunningModded = false;
                idField.SetValue(save, source);
                Reflect.Call(profileMgr, "LoadProfile");
                save.InitProgressData();
                save.InitPrefsData();

                if (HistoryPath(source) is { } fromDir && store.DirectoryExists(fromDir))
                {
                    foreach (var name in store.GetFilesInDirectory(fromDir))
                    {
                        if (!name.EndsWith(".run", StringComparison.OrdinalIgnoreCase)) continue;
                        if (store.ReadFile(fromDir + "/" + name) is { Length: > 0 } content)
                            history[name] = content;
                    }
                }
            }
            finally
            {
                UserDataPathProvider.IsRunningModded = wasModded;
            }

            // --- write it back out on the modded side ----------------------------------
            // SwitchProfileId points the managers at the target profile, writes the modded
            // profile.save and creates its history dir. The three saves then flush the vanilla
            // data we just loaded into the modded paths, through the cloud store.
            save.SwitchProfileId(target);
            save.SaveProfile();
            save.SaveProgressFile();
            save.SavePrefsFile();

            if (HistoryPath(target) is { } toDir)
                foreach (var (name, content) in history)
                    store.WriteFile(toDir + "/" + name, content);

            MainFile.Logger.Info($"save import: vanilla profile {source} -> modded profile {target}, {history.Count} runs");
            return history.Count;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"save import failed: {e}");
            return -1;
        }
    }

    // Reloads the main menu so it shows the imported profile instead of the pre-import state.
    // Best-effort: on a miss the caller tells the player to restart the game.
    internal static bool ReloadMainMenu()
    {
        try
        {
            if (GameNode() is not { } game) return false;
            var m = game.GetType().GetMethod("ReloadMainMenu", Type.EmptyTypes);
            if (m == null) return false;
            m.Invoke(game, null);
            return true;
        }
        catch { return false; }
    }

    private static Node? GameNode()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is not { } root) return null;
        foreach (var child in root.GetChildren())
            if (child.Name == "Game")
                return child;
        return null;
    }

    // RunHistorySaveManager.GetHistoryPath is static but has changed arity between game builds
    // (int) / (int, bool?), so bind it loosely.
    private static string? HistoryPath(int profileId)
    {
        try
        {
            var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.Saves.Managers.RunHistorySaveManager");
            if (type == null) return null;
            foreach (var m in type.GetMethods())
            {
                if (m.Name != "GetHistoryPath" || !m.IsStatic) continue;
                var ps = m.GetParameters();
                if (ps.Length == 1) return m.Invoke(null, new object?[] { profileId }) as string;
                if (ps.Length == 2) return m.Invoke(null, new object?[] { profileId, null }) as string;
            }
            return null;
        }
        catch { return null; }
    }

    private static object? Field(object target, string name) =>
        AccessTools.Field(target.GetType(), name)?.GetValue(target);
}
