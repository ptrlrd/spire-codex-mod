using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SpireCodex.Api;
using SpireCodex.Replay;

namespace SpireCodex.Core;

public static class Competitive
{
    public enum State { Off, Cleared, Flagged }

    public readonly record struct Verdict(State State, string? Reason, string? Detail, int Crashes);

    private static HashSet<string>? _keep;

    private static HashSet<string> Keep => _keep ??= Needed();

    private static string[] _approved = Array.Empty<string>();

    public static void SetApproved(IEnumerable<string> ids)
    {
        _approved = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToArray();
        _keep = null;
        _otherMods = null;
    }

    private static HashSet<string> Needed()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SpireCodex", "BaseLib", "BaseLibCompatibilityHelper" };
        foreach (var id in _approved) keep.Add(id);
        try
        {
            var manager = HookPatcher.FindType("MegaCrit.Sts2.Core.Modding.ModManager");
            if (Reflect.GetStatic(manager, "Mods") is not IEnumerable all) return keep;
            var needs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in all)
            {
                var manifest = Reflect.GetMember(mod, "manifest");
                var id = Reflect.GetString(manifest, "id");
                if (id == null) continue;
                var list = new List<string>();
                if (Reflect.GetMember(manifest, "dependencies") is IEnumerable deps)
                    foreach (var dep in deps)
                        if (Reflect.GetString(dep, "id") is { } depId) list.Add(depId);
                needs[id] = list;
            }
            var queue = new Queue<string>(keep);
            while (queue.Count > 0)
                if (needs.TryGetValue(queue.Dequeue(), out var deps))
                    foreach (var dep in deps)
                        if (keep.Add(dep)) queue.Enqueue(dep);
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"competitive: dependency scan failed: {e.Message}");
        }
        return keep;
    }

    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpireCodex", "competitive-disabled.txt");

    private static bool? _applied;
    private static bool _inRun;
    private static string? _runFlag;
    private static int _crashes;
    private static int _unexplained;
    private static List<string>? _otherMods;
    private static HashSet<string>? _endedCombats;

    public static bool On => SpireCodexConfig.CompetitiveMode;

    public static string? RunFlag => _runFlag;

    public static int Crashes => _crashes;

    public static int Unexplained => _unexplained;

    public static IReadOnlyList<string> OtherMods()
    {
        if (_otherMods != null) return _otherMods;
        var names = new List<string>();
        try
        {
            var manager = HookPatcher.FindType("MegaCrit.Sts2.Core.Modding.ModManager");
            var loaded = manager?.GetMethod("GetLoadedMods", Type.EmptyTypes)?.Invoke(null, null);
            if (loaded is not IEnumerable mods) return names;
            foreach (var mod in mods)
            {
                var manifest = Reflect.GetMember(mod, "manifest");
                var id = Reflect.GetString(manifest, "id");
                if (id == null || Keep.Contains(id)) continue;
                names.Add(Reflect.GetString(manifest, "name") ?? id);
            }
            _otherMods = names;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"competitive: mod list failed: {e.Message}");
        }
        return names;
    }

    public static Verdict Current()
    {
        if (!On) return new Verdict(State.Off, null, null, 0);
        var mods = OtherMods();
        if (mods.Count > 0)
            return new Verdict(State.Flagged, "mods", string.Join(", ", mods), _crashes);
        if (!SpireCodexConfig.UploadRuns || !SpireCodexConfig.RecordReplays || !SpireCodexConfig.UploadReplays
            || !Consent.ReplaysGranted || !SteamAuth.IsSignedIn)
            return new Verdict(State.Flagged, "uploads", null, _crashes);
        if (_inRun && _runFlag != null)
            return new Verdict(State.Flagged, _runFlag, null, _crashes);
        if (_inRun && Producer.LiveStateProducer.Latest is { InRun: true } s)
        {
            var bracket = ReplayJournalScan.Bracket(s.GameMode, s.Ascension, s.PlayerCount, s.Modifiers.Count);
            if (bracket == null) return new Verdict(State.Flagged, "bracket", null, _crashes);
            return new Verdict(State.Cleared, null, bracket, _crashes);
        }
        return new Verdict(State.Cleared, null, null, _crashes);
    }

    public static void Sync()
    {
        var on = On;
        if (on && _inRun && _runFlag == null && UsedDebugTool()) Flag("console");
        if (_applied == on) return;
        var first = _applied == null;
        _applied = on;
        if (on)
        {
            ForceUploads();
            DisableOthers();
        }
        else RestoreOthers();
        if (!first && _inRun) Flag(on ? "late" : "toggled");
    }

    private static bool UsedDebugTool()
    {
        try
        {
            return Reflect.GetBool(Reflect.GetMember(Sts2Access.LiveRunState, "ExtraFields"), "UsedDebugCommand");
        }
        catch { return false; }
    }

    public static void BeginRun(string path, bool resumed, int floor, int reloads)
    {
        _inRun = true;
        _runFlag = null;
        _crashes = 0;
        _unexplained = 0;
        _endedCombats = null;
        if (resumed)
        {
            var seen = ReplayJournalScan.ProgressOf(path);
            _endedCombats = seen.EndedCombats;
            var history = ReplayJournalScan.CompetitiveOf(path);
            _crashes = history.CrashRecoveries + history.UnexplainedExits;
            _unexplained = history.UnexplainedExits;
            _runFlag = history.Flag ?? (history.StartedOn ? null : "late");
            if (_runFlag == null && !On) _runFlag = "toggled";
            if (_runFlag == null && ReplayJournalScan.RolledBack(seen, floor, reloads)) _runFlag = "rollback";
        }
        else if (floor > 1 || reloads > 0) _runFlag = "gap";
        else if (!On) _runFlag = "late";
    }

    public static void NoteResumedCombat(string? combatId)
    {
        if (combatId != null && _endedCombats?.Contains(combatId) == true) Flag("rollback");
        _endedCombats = null;
    }

    public static void EndRun()
    {
        _inRun = false;
        _endedCombats = null;
        _runFlag = null;
        _crashes = 0;
    }

    public static void Flag(string reason)
    {
        if (!_inRun || _runFlag != null) return;
        _runFlag = reason;
        ReplayRecorder.Line("competitive")?.Set("flag", reason).Emit();
    }

    public static void NoteQuit()
    {
        if (!On || !_inRun || !ReplayRecorder.Active) return;
        try
        {
            ReplayRecorder.Line("competitive")?.Set("event", "quit").Emit();
            Thread.Sleep(150);
        }
        catch { }
    }

    private static void ForceUploads()
    {
        if (SpireCodexConfig.UploadRuns && SpireCodexConfig.RecordReplays && SpireCodexConfig.UploadReplays) return;
        SpireCodexConfig.UploadRuns = true;
        SpireCodexConfig.RecordReplays = true;
        SpireCodexConfig.UploadReplays = true;
        Ui.Features.Save();
    }

    private static IEnumerable? ModList(out object? saves)
    {
        saves = Reflect.GetStatic(HookPatcher.FindType("MegaCrit.Sts2.Core.Saves.SaveManager"), "Instance");
        var settings = Reflect.GetMember(saves, "SettingsSave");
        return Reflect.GetMember(Reflect.GetMember(settings, "ModSettings"), "ModList") as IEnumerable;
    }

    private static string KeyOf(object entry) =>
        $"{Reflect.GetMember(entry, "Source")}|{Reflect.GetString(entry, "Id")}";

    private static bool SetEnabled(object entry, bool enabled)
    {
        var prop = entry.GetType().GetProperty("IsEnabled");
        if (prop == null || prop.GetValue(entry) is not bool now || now == enabled) return false;
        prop.SetValue(entry, enabled);
        return true;
    }

    private static void SaveSettings(object? saves) =>
        saves?.GetType().GetMethod("SaveSettings", Type.EmptyTypes)?.Invoke(saves, null);

    private static void DisableOthers()
    {
        try
        {
            if (ModList(out var saves) is not { } list) return;
            var turnedOff = new List<string>();
            foreach (var entry in list)
            {
                var id = Reflect.GetString(entry, "Id");
                if (id == null || Keep.Contains(id)) continue;
                if (SetEnabled(entry, false)) turnedOff.Add(KeyOf(entry));
            }
            if (turnedOff.Count == 0) return;
            var known = File.Exists(MarkerPath) ? File.ReadAllLines(MarkerPath) : Array.Empty<string>();
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllLines(MarkerPath, known.Concat(turnedOff).Distinct());
            SaveSettings(saves);
            MainFile.Logger.Info($"competitive: turned off {turnedOff.Count} other mods, restart needed");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"competitive: turning off other mods failed: {e.Message}");
        }
    }

    private static void RestoreOthers()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;
            var mine = new HashSet<string>(File.ReadAllLines(MarkerPath));
            File.Delete(MarkerPath);
            if (ModList(out var saves) is not { } list) return;
            var restored = 0;
            foreach (var entry in list)
                if (mine.Contains(KeyOf(entry)) && SetEnabled(entry, true)) restored++;
            if (restored == 0) return;
            SaveSettings(saves);
            MainFile.Logger.Info($"competitive: turned {restored} mods back on, restart needed");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"competitive: restoring mods failed: {e.Message}");
        }
    }
}
