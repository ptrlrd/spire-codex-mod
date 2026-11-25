using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using SpireCodex.Api;

namespace SpireCodex.Replay;

// Phase 3: sends a finished journal to /api/runs/{run_hash}/replay.
//
// Ordering is not optional. The endpoint 404s until the run doc exists, so the .run is always
// uploaded first and its response hands us the run_hash — the mod never computes that hash
// itself, which is also what keeps co-op correct (after PR #1019 the response carries the hash
// of the SUBMITTING player's slot, so a non-host's replay lands on their own run).
//
// Gated by UploadReplays, which requires UploadRuns and a consent grant given against a
// disclosure that names replays. Consent.Answered is sticky forever, so gating on Granted alone
// would have started sending per-action journals from everyone who agreed to "completed runs"
// back in v1.0.10 without them ever seeing a card that says so.
internal static class ReplayUploader
{
    // Journals already accepted by the server, so a re-upload is never attempted. Same shape and
    // location as the .run ledger next to it.
    private static readonly object Gate = new();
    private static HashSet<string>? _sent;
    private static HashSet<string>? _abandoned;

    // Sweep journals stranded on disk, at launch and whenever the toggle is switched on.
    //
    // Without this a completed journal is only ever offered once, in the same moment its .run
    // uploads. Anything that misses that window is orphaned for good: recorded while
    // UploadReplays was off, a retryable failure with nothing to re-trigger it, or a run
    // finished before the feature existed. Crash recovery does not cover them either — it only
    // looks for journals with NO end line.
    //
    // The run_hash is not computed here. Each journal names the .run it belongs to (both carry
    // the same start_time), so the .run is re-posted first; that endpoint is idempotent and
    // returns the same hash for an already-known run, which is also the only way to get the
    // right slot's hash in co-op.
    public static async Task SweepAsync()
    {
        try
        {
            if (!SpireCodexConfig.UploadReplays || !Config.UploadRuns || !Consent.ReplaysGranted) return;
            if (_bfActive) return;

            var client = new SpireCodexClient();
            var sent = 0;
            foreach (var run in Pending())
            {
                if (await OfferAsync(client, run, force: false).ConfigureAwait(false) == null) continue;
                if (++sent >= 25) break;          // one launch does a bounded chunk
                await Task.Delay(500).ConfigureAwait(false); // stay well under the rate limit
            }
            if (sent > 0) MainFile.Logger.Info($"replay sweep: offered {sent} stranded journal(s)");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay sweep error: {e.Message}");
        }
    }

    public enum BackfillStart { Started, NeedUploads, NeedSignIn }

    private static volatile bool _bfActive;
    private static volatile bool _bfHasRun;
    private static int _bfTotal;
    private static int _bfDone;
    private static int _bfSent;

    public static bool BackfillActive => _bfActive;
    public static bool BackfillHasRun => _bfHasRun;
    public static int BackfillTotal => _bfTotal;
    public static int BackfillDone => _bfDone;
    public static int BackfillSent => _bfSent;

    public static BackfillStart BackfillNow()
    {
        if (_bfActive) return BackfillStart.Started;
        if (!Config.UploadRuns || !Consent.Granted) return BackfillStart.NeedUploads;
        if (!SteamAuth.IsSignedIn) return BackfillStart.NeedSignIn;
        _bfActive = true;
        _bfHasRun = false;
        _bfTotal = 0;
        _bfDone = 0;
        _bfSent = 0;
        _ = BackfillAsync();
        return BackfillStart.Started;
    }

    private static async Task BackfillAsync()
    {
        try
        {
            var pending = Pending();
            _bfTotal = pending.Count;
            var client = new SpireCodexClient();
            foreach (var run in pending)
            {
                if (await OfferAsync(client, run, force: true).ConfigureAwait(false) == true) _bfSent++;
                _bfDone++;
                await Task.Delay(500).ConfigureAwait(false);
            }
            MainFile.Logger.Info($"replay backfill: {_bfSent}/{_bfTotal} journal(s) accepted");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay backfill error: {e.Message}");
        }
        finally
        {
            _bfActive = false;
            _bfHasRun = true;
        }
    }

    private static List<string> Pending()
    {
        var runs = new List<string>();
        string[] files;
        try { files = Directory.GetFiles(ReplayRecorder.Dir, "*.jsonl"); }
        catch { return runs; }

        foreach (var path in files)
        {
            if (AlreadySent(path)) continue;
            if (ReplayRecorder.Active && path == ReplayRecorder.CurrentPath) continue;
            if (!HasTerminalLine(path)) continue;
            if (RunFileFor(path) is { } run) runs.Add(run);
        }
        return runs;
    }

    private static async Task<bool?> OfferAsync(SpireCodexClient client, string run, bool force)
    {
        try
        {
            var runJson = await File.ReadAllTextAsync(run).ConfigureAwait(false);
            var up = await client
                .UploadRunAsync(runJson, Config.SteamId, Config.Username, Core.Sts2Version.Current)
                .ConfigureAwait(false);
            if (!up.Success) return null;
            var hash = HashOf(up.Body);
            if (hash == null) return null;
            return await TryUploadAsync(runJson, hash, force).ConfigureAwait(false);
        }
        catch { return null; }
    }

    // A journal is finished when its last line is the terminal record. Read the tail only.
    private static bool HasTerminalLine(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 4096);
            if (take == 0) return false;
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var tail = System.Text.Encoding.UTF8.GetString(buf, 0, read);
            var at = tail.LastIndexOf("\"t\":\"end\"", StringComparison.Ordinal);
            return at >= 0 && tail.IndexOf('\n', at) >= 0;
        }
        catch { return false; }
    }

    // "<seed>-<start_time>.jsonl" -> the "<start_time>.run" it belongs to, anywhere in the save
    // tree (profiles and the modded/vanilla split mean the directory is not fixed).
    private static string? RunFileFor(string journalPath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(journalPath);
            var dash = name.LastIndexOf('-');
            if (dash < 0) return null;
            var stamp = name.Substring(dash + 1);
            var root = Api.RunUploader.FindSaveRoot();
            if (root == null) return null;
            foreach (var f in Directory.GetFiles(root, stamp + ".run", SearchOption.AllDirectories))
                return f;
        }
        catch { }
        return null;
    }

    private static string? HashOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("run_hash", out var h)) return h.GetString();
        }
        catch { }
        return null;
    }

    // Called after a .run upload succeeds. `runJson` identifies which journal belongs to it.
    public static async Task<bool> TryUploadAsync(string runJson, string runHash, bool force = false)
    {
        try
        {
            if (!Config.UploadRuns) return false;
            if (!force && (!SpireCodexConfig.UploadReplays || !Consent.ReplaysGranted)) return false;
            if (string.IsNullOrEmpty(runHash)) return false;

            var path = JournalFor(runJson);
            if (path == null) return false; // no journal for this run (recording was off, or an old run)
            if (AlreadySent(path)) return false;

            var raw = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var gzip = Gzip(raw);
            // Never post something that cannot be read back. A truncated member would be
            // rejected as 400 not_gzip after the upload had already spent a request.
            if (!RoundTrips(gzip, raw.Length))
            {
                MainFile.Logger.Info($"replay: gzip of {Path.GetFileName(path)} did not round-trip; not sending");
                return false;
            }

            var result = await new SpireCodexClient().UploadReplayAsync(runHash, gzip).ConfigureAwait(false);
            var code = Code(result.Body);

            if (result.Success || code == "replay_exists")
            {
                // replay_exists means the server already holds one for this run and keeps the
                // first; either way this journal is done and must not be retried.
                MarkAccepted(path);
                MainFile.Logger.Info($"replay upload {Path.GetFileName(path)}: ok ({result.StatusCode}) " +
                                     $"{raw.Length}B -> {gzip.Length}B");
                return true;
            }

            // Permanent ONLY when the server names a reason this exact file can never satisfy.
            // Everything else, 400 included, gets another go on a later launch.
            //
            // This used to be the other way round: anything that was not a 404, a 5xx or a
            // transport error was treated as final and written into the sent ledger. A server
            // that had simply never heard of a newer replay_version answered 400 bad_header, and
            // the client quietly burned seventeen journals that were perfectly good and would
            // have been accepted the moment the allowlist was updated. A 400 means "this server,
            // today, did not like this request", which is the most fixable failure there is, and
            // it is exactly the one the old policy made unrecoverable.
            //
            // A genuinely unsendable file now costs one request per launch instead of being lost.
            // That is the cheaper mistake by a wide margin, and the sweep is capped per launch.
            var permanent = code is "not_owner" or "too_large";
            MainFile.Logger.Info(
                $"replay upload {Path.GetFileName(path)}: FAILED ({result.StatusCode}) {code ?? "?"}"
                + (permanent ? " — permanent, not retrying" : " — will retry on a later launch"));

            if (permanent) MarkAbandoned(path);
            return false;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay upload error: {e.Message}");
            return false;
        }
    }

    // The journal belonging to a .run, matched on the identity both files carry. The recorder
    // names journals "<seed>-<start_time>.jsonl" using the SAME seed string and start time the
    // .run records, precisely so this lookup needs no index.
    private static string? JournalFor(string runJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(runJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("seed", out var s) || s.GetString() is not { Length: > 0 } seed)
                return null;
            if (!root.TryGetProperty("start_time", out var t)) return null;
            var start = t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0;
            if (start <= 0) return null;

            var path = Path.Combine(ReplayRecorder.Dir, $"{seed}-{start}.jsonl");
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    // One gzip member, closed once. CompressionLevel.Fastest, not Optimal: measured on a real
    // journal the difference is ~6% of an already-small file, and this runs right after a run
    // ends when the game is still doing its own save work.
    private static byte[] Gzip(byte[] raw)
    {
        using var outp = new MemoryStream();
        using (var gz = new GZipStream(outp, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return outp.ToArray();
    }

    private static bool RoundTrips(byte[] gzip, int expectedLength)
    {
        try
        {
            using var inp = new MemoryStream(gzip);
            using var gz = new GZipStream(inp, CompressionMode.Decompress);
            using var outp = new MemoryStream();
            gz.CopyTo(outp);
            return outp.Length == expectedLength;
        }
        catch { return false; }
    }

    // The server returns {"detail": {"code": "...", "message": "..."}} on every error.
    private static string? Code(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var d)
                && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("code", out var c))
                return c.GetString();
        }
        catch { }
        return null;
    }

    // --- ledgers -----------------------------------------------------------------------
    //
    // TWO files, deliberately. "Do not offer this again" and "the server has this" are different
    // facts, and the pruner may only act on the second.
    //
    //   replays-sent-<steam>.txt       accepted: a 200, or replay_exists. The server HAS it.
    //   replays-abandoned-<steam>.txt  permanently rejected (not_owner, too_large). Never sent,
    //                                  never will be, and must never be deleted on that basis.
    //
    // One combined file is what made the outage worse: a 400 wrote into it, so a file that had
    // never reached the server looked identical to one that had. A pruner keyed on that would
    // have deleted unsent runs.

    private static string? LedgerPath(string kind)
    {
        try
        {
            if (string.IsNullOrEmpty(Config.SteamId)) return null;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpireCodex");
            return Path.Combine(dir, $"replays-{kind}-{Config.SteamId}.txt");
        }
        catch { return null; }
    }

    private static HashSet<string> Load(string kind)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var lp = LedgerPath(kind);
            if (lp != null && File.Exists(lp))
                foreach (var line in File.ReadAllLines(lp))
                    if (!string.IsNullOrWhiteSpace(line)) set.Add(line.Trim());
        }
        catch { /* a missing ledger just means we may re-offer once; the server dedupes */ }
        return set;
    }

    private static bool AlreadySent(string path)
    {
        lock (Gate)
        {
            _sent ??= Load("sent");
            _abandoned ??= Load("abandoned");
            var full = Path.GetFullPath(path);
            return _sent.Contains(full) || _abandoned.Contains(full);
        }
    }

    // The server has this journal. The ONLY thing that authorises deleting it later.
    private static void MarkAccepted(string path) => Append("sent", ref _sent, path);

    // Rejected for a reason this file can never satisfy. Stops re-offering; authorises nothing.
    private static void MarkAbandoned(string path) => Append("abandoned", ref _abandoned, path);

    private static void Append(string kind, ref HashSet<string>? cache, string path)
    {
        lock (Gate)
        {
            cache ??= Load(kind);
            if (!cache.Add(Path.GetFullPath(path))) return;
            try
            {
                var lp = LedgerPath(kind);
                if (lp == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(lp)!);
                File.AppendAllText(lp, Path.GetFullPath(path) + "\n");
            }
            catch { /* best effort; the server's duplicate check is the real guard */ }
        }
    }

    private static bool IsAccepted(string path)
    {
        lock (Gate)
        {
            _sent ??= Load("sent");
            return _sent.Contains(Path.GetFullPath(path));
        }
    }

    // --- retention ----------------------------------------------------------------------

    private const int RetentionDays = 90;

    // Delete local journals 90 days after they were last written, but ONLY where deleting one
    // cannot lose data that never reached the server.
    //
    // Two ways a journal earns deletion:
    //   - the server accepted it, so a copy exists off this machine; or
    //   - replay uploading is off, so it was never going to leave and is purely local.
    //
    // Everything else is kept forever, on purpose: a journal waiting to upload, one the server
    // permanently rejected, and the run currently being written. Age alone is never enough.
    // Seventeen journals were silently burned by an upload policy that assumed a failure was
    // final, and this is the same mistake with a delete on the end of it.
    public static void PruneOld()
    {
        try
        {
            string[] files;
            try { files = Directory.GetFiles(ReplayRecorder.Dir, "*.jsonl"); }
            catch { return; }

            // Off means these were never going to leave the machine, so age is the only question.
            var uploading = SpireCodexConfig.UploadReplays && Config.UploadRuns && Consent.ReplaysGranted;
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            var removed = 0;

            foreach (var path in files)
            {
                if (ReplayRecorder.Active && path == ReplayRecorder.CurrentPath) continue;
                DateTime written;
                try { written = File.GetLastWriteTimeUtc(path); }
                catch { continue; }
                if (written > cutoff) continue;
                if (uploading && !IsAccepted(path)) continue; // still owed to the server
                try { File.Delete(path); removed++; }
                catch { /* locked or gone; next launch tries again */ }
            }
            if (removed > 0)
                MainFile.Logger.Info($"replay: pruned {removed} journal(s) older than {RetentionDays} days");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay prune error: {e.Message}");
        }
    }
}
