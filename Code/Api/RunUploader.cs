using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SpireCodex.Api;

// Watches the Slay the Spire 2 save tree for completed .run files and uploads them,
// and (once) backfills the player's existing run history. Both gated by the opt-in
// Config.UploadRuns.
//
// Notes on Slay the Spire 2 saves:
//  - Save root is %APPDATA%/SlayTheSpire2/steam/<steamid64>/. The folder name is the
//    steam id, used for run attribution (?steam_id=).
//  - When mods are loaded the game writes to a separate modded/ subtree, so finished
//    runs land in steam/<id>/modded/profileN/saves/history/. We watch the whole
//    steam/<id>/ tree recursively to cover modded and vanilla profiles alike.
//  - The game writes run files atomically (temp then rename), so the final *.run name
//    arrives as a Renamed event, not Created. We handle both.
public sealed class RunUploader : IDisposable
{
    private readonly SpireCodexClient _client = new();
    private readonly string _sts2Version;
    private readonly HashSet<string> _dispatched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _heldForConsent = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    public RunUploader(string sts2Version) => _sts2Version = sts2Version;

    public void Start()
    {
        var root = FindSaveRoot();
        if (root == null)
        {
            MainFile.Logger.Info("no save root found; run upload disabled");
            return;
        }

        Config.SteamId ??= new DirectoryInfo(root).Name;

        _watcher = new FileSystemWatcher(root, "*.run")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024, // deep recursive tree, avoid buffer overflow
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, e) => _ = Upload(e.FullPath);
        _watcher.Renamed += (_, e) => _ = Upload(e.FullPath);

        MainFile.Logger.Info($"watching for completed runs under {root} " +
                 $"(steam_id={Config.SteamId}, upload={Config.UploadRuns})");

        // When the player grants upload consent mid-session, flush any runs that finished
        // while the prompt was up and kick the backfill that was held at the gate.
        Consent.OnGranted += () =>
        {
            string[] held;
            lock (_heldForConsent)
            {
                held = new string[_heldForConsent.Count];
                _heldForConsent.CopyTo(held);
                _heldForConsent.Clear();
            }
            foreach (var path in held) _ = Upload(path);
            if (Config.BackfillOnce) _ = BackfillAsync(root);
        };

        // One-time backfill of existing history, off the main thread.
        if (Config.BackfillOnce) _ = BackfillAsync(root);
    }

    // %APPDATA%/SlayTheSpire2/steam/<steamid64>/ — the one all-digit subfolder.
    // Also used by LocalStats for personal per-character win rates.
    internal static string? FindSaveRoot()
    {
        try
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            var steamDir = Path.Combine(appData, "SlayTheSpire2", "steam");
            if (!Directory.Exists(steamDir)) return null;

            return Directory.GetDirectories(steamDir)
                .FirstOrDefault(d => Path.GetFileName(d) is { Length: >= 17 } name && name.All(char.IsDigit));
        }
        catch
        {
            return null;
        }
    }

    private async Task Upload(string path)
    {
        if (!Config.UploadRuns) return;
        if (!Consent.Granted) // hold at the gate; flushed by OnGranted, dropped on decline
        {
            lock (_heldForConsent) _heldForConsent.Add(Path.GetFullPath(path));
            return;
        }
        if (!_dispatched.Add(Path.GetFullPath(path))) return; // Windows fires duplicate events

        try
        {
            await Task.Delay(1500).ConfigureAwait(false); // let the game finish writing the file
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            if (!HasMapHistory(json)) return; // empty insta-abandon, nothing to record

            // Attach this run's damage summary (per-hit totals tracked during the run). Live
            // completions only: backfilled history has no in-memory damage and rides through
            // unchanged. No-op when nothing was tracked.
            var payload = Core.DamageTracker.AttachTo(json);

            var result = await _client
                .UploadRunAsync(payload, Config.SteamId, Config.Username, _sts2Version)
                .ConfigureAwait(false);

            MainFile.Logger.Info($"upload {Path.GetFileName(path)}: " +
                     $"{(result.Success ? "ok" : "FAILED")} ({result.StatusCode}) " +
                     $"{Truncate(result.Body, 200)}");

            // Pop the post-run shareable card for live completions (not backfill), with this
            // run's damage summary line when we tracked any.
            if (result.Success && ParseUploadResponse(result.Body) is { Hash: not null } up)
                Ui.RunCompleteCard.ShowRunDeferred(
                    up.Url ?? Config.RunUrl(up.Hash!), up.RankLine, Core.DamageTracker.RunCardLine());
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"upload error for {path}: {e.Message}");
        }
    }

    private readonly record struct UploadInfo(string? Hash, string? Url, string? RankLine);

    private static UploadInfo ParseUploadResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string? hash = null;
            if (r.TryGetProperty("run_hash", out var h) && h.GetString() is { Length: > 0 } rh) hash = rh;
            else if (r.TryGetProperty("run_id", out var i) && i.GetString() is { Length: > 0 } id) hash = id;

            string? url = null;
            if (r.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } ru) url = ru;

            // Seed standing (server includes it when this run sits on a known seed).
            string? rankLine = null;
            var rank = r.TryGetProperty("seed_rank", out var sr) && sr.ValueKind == JsonValueKind.Number
                ? sr.GetInt32() : (int?)null;
            var total = r.TryGetProperty("seed_total", out var st) && st.ValueKind == JsonValueKind.Number
                ? st.GetInt32() : (int?)null;
            if (rank is { } rv && total is { } tv) rankLine = Loc.F("rc_seed_rank", rv, tv);
            else if (total is { } tv2 and > 1) rankLine = Loc.F("rc_seed_tracked", tv2);

            return new UploadInfo(hash, url, rankLine);
        }
        catch { return default; }
    }

    // Upload every existing .run once, then write a marker so it never runs again.
    private async Task BackfillAsync(string root)
    {
        try
        {
            if (!Config.UploadRuns) return; // respect opt-in; retry next launch if enabled later
            if (!Consent.Granted) return; // OnGranted re-kicks this once consent lands

            var marker = BackfillMarkerPath();
            if (marker != null && File.Exists(marker)) return; // already backfilled

            var files = Directory.GetFiles(root, "*.run", SearchOption.AllDirectories);
            MainFile.Logger.Info($"backfill: scanning {files.Length} run files...");

            int added = 0, duplicate = 0, skipped = 0, errored = 0;
            using var gate = new SemaphoreSlim(6); // limit concurrency, be polite to the API
            var tasks = new List<Task>();

            foreach (var file in files)
            {
                await gate.WaitAsync().ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                        if (!HasMapHistory(json)) { Interlocked.Increment(ref skipped); return; }

                        _dispatched.Add(Path.GetFullPath(file)); // don't double with the watcher
                        var r = await _client
                            .UploadRunAsync(json, Config.SteamId, Config.Username, _sts2Version)
                            .ConfigureAwait(false);

                        if (!r.Success) Interlocked.Increment(ref errored);
                        else if (r.Body.Contains("\"duplicate\":true")) Interlocked.Increment(ref duplicate);
                        else Interlocked.Increment(ref added);
                    }
                    catch
                    {
                        Interlocked.Increment(ref errored);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            MainFile.Logger.Info($"backfill done: new={added} duplicate={duplicate} " +
                     $"skipped={skipped} error={errored}");

            if (marker != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("o")).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"backfill error: {e.Message}");
        }
    }

    private static string? BackfillMarkerPath()
    {
        try
        {
            var dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SpireCodex");
            return Path.Combine(dir, $"backfill-{Config.SteamId}.done");
        }
        catch
        {
            return null;
        }
    }

    // The API rejects runs with empty map_point_history (insta-abandons). Skip them
    // client-side so they don't generate noise.
    private static bool HasMapHistory(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("map_point_history", out var m)
                   && m.ValueKind == JsonValueKind.Array
                   && m.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max) + "...";

    public void Dispose() => _watcher?.Dispose();
}
