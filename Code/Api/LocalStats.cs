using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// Personal per-character win rates, computed locally from the player's own .run history
// (no API needed, works offline). Scanned once in the background at startup; lookups return
// null until ready. Abandoned runs are excluded — they say nothing about win rate.
public static class LocalStats
{
    public sealed record CharRecord(int Runs, int Wins)
    {
        public double WinRate => Runs > 0 ? Wins * 100.0 / Runs : 0;
    }

    private static Dictionary<string, CharRecord>? _byCharacter;
    private static bool _started;

    public static CharRecord? For(string? charId) =>
        charId == null ? null : _byCharacter?.GetValueOrDefault(charId);

    public static void EnsureLoaded()
    {
        if (_started) return;
        _started = true;
        _ = Task.Run(Scan);
    }

    private static void Scan()
    {
        try
        {
            var root = RunUploader.FindSaveRoot();
            if (root == null) return;

            var tally = new Dictionary<string, (int Runs, int Wins)>();
            foreach (var path in Directory.EnumerateFiles(root, "*.run", SearchOption.AllDirectories))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var r = doc.RootElement;
                    if (r.TryGetProperty("was_abandoned", out var ab) && ab.GetBoolean()) continue;
                    if (!r.TryGetProperty("players", out var players) || players.GetArrayLength() == 0) continue;
                    var raw = players[0].TryGetProperty("character", out var ch) ? ch.GetString() : null;
                    var id = Core.Ids.Bare(raw);
                    if (string.IsNullOrEmpty(id)) continue;
                    var win = r.TryGetProperty("win", out var w) && w.GetBoolean();

                    var t = tally.GetValueOrDefault(id);
                    tally[id] = (t.Runs + 1, t.Wins + (win ? 1 : 0));
                }
                catch { /* unreadable/partial file -> skip */ }
            }

            var result = new Dictionary<string, CharRecord>();
            foreach (var (id, t) in tally) result[id] = new CharRecord(t.Runs, t.Wins);
            _byCharacter = result;
            Diag($"scanned local history: {result.Count} characters, " +
                 $"{System.Linq.Enumerable.Sum(result.Values, r => r.Runs)} runs");
        }
        catch (Exception e) { Diag($"scan failed: {e.GetType().Name}: {e.Message}"); }
    }

    private static void Diag(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "spire-codex-scores.log"),
                $"{DateTimeOffset.UtcNow:o}  [local-stats] {msg}\n");
        }
        catch { /* ignore */ }
    }
}
