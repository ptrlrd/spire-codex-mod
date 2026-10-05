using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SpireCodex.Replay;

// The append-only NDJSON journal for one run, and the thread that writes it.
//
// Shape:
//
//   Harmony hook (game thread)
//     -> ReplayLine, values only, no reflection into live objects
//     -> Channel<ReplayLine>.Writer.TryWrite            // bounded, never blocks the game
//          |
//     writer thread (LongRunning)
//     -> Utf8JsonWriter into a reused buffer
//     -> append newline-terminated record to <seed>-<start_time>.jsonl
//     -> flush to the OS whenever the queue is empty, and at least every FlushIntervalMs
//
// THE LOSS CONTRACT. No in-process recorder gives both zero frame hitches and zero crash
// loss: durability means waiting on storage, and asynchronous capture means an uncommitted
// interval. So the failure modes are chosen rather than stumbled into.
//
//  - The channel is BOUNDED. If events ever outrun the disk, the options are block the producer
//    (a frame hitch, the exact thing this class exists to avoid), silently drop events (a
//    recording that lies about what happened), or drop and SAY SO. We drop and say so: the
//    first lost sequence number and the total are recorded, and the terminal line carries
//    capture_status "gapped". Capture then CONTINUES — an earlier version latched it off
//    permanently, which turned one 50ms disk stall into an abandoned hour-long run. A recording
//    with a marked hole is usable; one that stops silently at floor 12 is not.
//  - The on-disk journal is raw NDJSON, not gzip. A process killed mid-write leaves a valid
//    newline-terminated prefix plus at most one torn trailing record, which recovery drops. A
//    gzip stream killed mid-member may have no trailer at all and its recoverability depends
//    on decoder leniency. Compression happens once, at finalize, off the game thread.
//
// Nothing here is allowed to throw into the game. Every public entry point swallows.
internal sealed class ReplayJournal : IDisposable
{
    // ~1 second of the worst burst we have seen (a large AoE turn emits a few dozen lines).
    // Large enough that normal play never approaches it, small enough that a genuinely stuck
    // writer is caught in bounded memory rather than growing until the game dies.
    private const int QueueCapacity = 4096;
    private const int FlushIntervalMs = 2000;

    private readonly Channel<ReplayLine> _channel;
    private readonly Task _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();

    private long _seq;

    // Capture health, read when the terminal line is written. Volatile: set on the game
    // thread when an enqueue fails, read on the writer thread at finalize.
    private volatile bool _capturing = true;
    private volatile string? _stopReason;
    private long _firstLostSeq;
    private long _lostCount;
    // Set when the terminal line could not be queued; the writer appends it directly instead.
    private volatile ReplayLine? _pendingTerminal;

    public string Path { get; }
    public bool Capturing => _capturing;

    public bool Resumed { get; }

    // Highest card-instance id and decision id already written to this file, or 0 when it is
    // new. The recorder resumes its counters above these so a reloaded session never re-mints
    // an id the earlier session already used. See ReplayJournalScan.HighWaterOf.
    public int LastCardId { get; }
    public int LastDecisionId { get; }
    public int LastCreatureId { get; }

    // The last full deck listing already in the file, raw. Null on a new run. The recorder
    // aligns the resumed deck against it to bridge instance ids across the reload.
    public string? DeckLine { get; }

    private ReplayJournal(string path)
    {
        Path = path;
        // A run loaded from a save reopens THIS journal and appends to it, so the sequence has
        // to continue rather than restart. Without this a reloaded run wrote a second s=0,1,2…
        // into a file that already had them, and the exploder keys events on (run_hash, s):
        // duplicate keys, silently, on exactly the runs the save-scum work is trying to study.
        var prior = ReplayJournalScan.HighWaterOf(path);
        // True when this journal reopened a file that already had lines, which is the ONLY
        // reliable signal that a run is being picked back up. The game's reload counter does not
        // move on a save-and-quit-to-menu followed by Continue in the same process, so gating the
        // resume marker on it left that boundary unmarked.
        Resumed = prior.Seq >= 0;
        _seq = prior.Seq + 1;
        LastCardId = prior.Card;
        LastDecisionId = prior.Decision;
        LastCreatureId = prior.Creature;
        DeckLine = prior.DeckLine;
        _channel = Channel.CreateBounded<ReplayLine>(new BoundedChannelOptions(QueueCapacity)
        {
            // Wait, NOT DropWrite. This is the mode that makes overflow OBSERVABLE.
            //
            // BoundedChannelFullMode.DropWrite means "drop the item being written" and TryWrite
            // returns TRUE. The earlier version used DropWrite and treated a false return as the
            // overflow signal, so the signal never fired: lines vanished, _lostCount stayed 0,
            // and the terminal line still claimed capture_status "complete". The loss contract
            // this class documents was simply false.
            //
            // With Wait, TryWrite still does NOT block — it returns false immediately when the
            // channel is full, which is exactly the non-blocking failure signal we need. Only
            // WriteAsync would wait, and that is never called on the game thread.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false, // hooks can fire from more than one game-side context
        });
        _writer = Task.Factory.StartNew(
            WriteLoopAsync, TaskCreationOptions.LongRunning).Unwrap();
    }

    // Resuming the counters is pure file/string work with no game coupling, so it lives in
    // ReplayJournalScan where the test project can compile it directly -- the mod assembly
    // needs Godot to load, and a guard that only works with the game running is no guard at
    // all (the same reason JsonContractTests reads source rather than reflecting over types).
    internal static long LastSequence(string path) => ReplayJournalScan.LastSequence(path);

    // Open a journal for a run. Returns null when the path can't be prepared, and the
    // recorder then simply doesn't record; a replay is never worth failing a run over.
    public static ReplayJournal? Open(string dir, string seed, long startTime)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var name = $"{Sanitize(seed)}-{startTime}.jsonl";
            return new ReplayJournal(System.IO.Path.Combine(dir, name));
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay: could not open journal: {e.Message}");
            return null;
        }
    }

    // Enqueue one line. Game thread. Stamps sequence and elapsed-ms here so ordering is
    // decided in one place. Returns immediately, always.
    public void Write(ReplayLine line)
    {
        if (!_capturing) return;
        try
        {
            line.Seq = Interlocked.Increment(ref _seq) - 1;
            line.Ms = _clock.ElapsedMilliseconds;
            if (_channel.Writer.TryWrite(line)) return;

            // A drop is RECORDED but capture is NOT latched off. The earlier version stopped
            // recording permanently on the first drop, so one 50ms disk stall during a big AoE
            // turn would abandon the rest of an hour-long run. Sequence numbers make the gap
            // self-evident (s jumps) and the terminal line reports the count, so resuming is
            // strictly better: a recording with a marked hole beats one that stops at floor 12
            // without saying why.
            if (Interlocked.Increment(ref _lostCount) == 1)
            {
                Interlocked.Exchange(ref _firstLostSeq, line.Seq);
                _stopReason = "queue_full";
                MainFile.Logger.Info($"replay: dropped a line at seq {line.Seq} (queue full); continuing");
            }
        }
        catch
        {
            // Capture must never break the game.
        }
    }

    // Close the journal: drain what's queued, write the terminal line, flush to disk. Called
    // on run end and on mod shutdown. Waits, so callers keep it off the game thread.
    public async Task<bool> CloseAsync(ReplayLine terminal)
    {
        try
        {
            var lost = Interlocked.Read(ref _lostCount);
            terminal.Set("capture_status", !_capturing ? "truncated" : lost > 0 ? "gapped" : "complete");
            if (!_capturing || lost > 0)
            {
                terminal.Set("stop_reason", _stopReason);
                terminal.Set("lost_from_seq", Interlocked.Read(ref _firstLostSeq));
                terminal.Set("lost_count", Interlocked.Read(ref _lostCount));
            }
            // Bypass the capture latch: the terminal line is exactly what a gapped recording
            // needs most, so it goes in even after capture stopped.
            terminal.Seq = Interlocked.Increment(ref _seq) - 1;
            terminal.Ms = _clock.ElapsedMilliseconds;
            // If the queue is full this is the one line that must not be lost: without it the
            // next launch's recovery scan reads the run as an interrupted crash and overwrites
            // the real outcome with "interrupted".
            if (!_channel.Writer.TryWrite(terminal)) _pendingTerminal = terminal;

            _channel.Writer.TryComplete();
            await _writer.ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay: close failed: {e.Message}");
            return false;
        }
    }

    private async Task WriteLoopAsync()
    {
        FileStream? fs = null;
        try
        {
            // FileShare.ReadWrite so the file can be inspected while a run is in progress,
            // which is how phase 1 gets verified by hand.
            fs = new FileStream(
                Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
                bufferSize: 64 * 1024, useAsync: false);

            var buffer = new ArrayBufferWriter<byte>(4096);
            var lastFlush = Stopwatch.StartNew();

            await foreach (var line in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                WriteLine(fs, buffer, line);

                // Hand the file to the OS whenever the writer has caught up, not only every two
                // seconds. A process that dies keeps what the OS already holds, so a fight's last
                // rows are no longer lost behind a game save that already recorded the victory.
                // The interval still bounds the loss during a burst the writer is still draining.
                if (!_channel.Reader.TryPeek(out _) || lastFlush.ElapsedMilliseconds >= FlushIntervalMs)
                {
                    fs.Flush();
                    lastFlush.Restart();
                }
            }
            if (_pendingTerminal is { } pending)
                WriteLine(fs, new ArrayBufferWriter<byte>(1024), pending);
            fs.Flush(flushToDisk: true);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception e)
        {
            MainFile.Logger.Info($"replay: writer died: {e.Message}");
            _capturing = false;
            _stopReason = "writer_error";
        }
        finally
        {
            try { fs?.Dispose(); } catch { }
        }
    }

    private static void WriteLine(FileStream fs, ArrayBufferWriter<byte> buffer, ReplayLine line)
    {
        buffer.Clear();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("t", line.Kind);
            w.WriteNumber("s", line.Seq);
            w.WriteNumber("ms", line.Ms);
            foreach (var (key, value) in line.Fields) WriteField(w, key, value);
            w.WriteEndObject();
        }
        fs.Write(buffer.WrittenSpan);
        fs.WriteByte((byte)'\n');
    }

    // Values arriving from hooks are a small closed set. Anything unexpected is written as a
    // string rather than dropped, so an unhandled type shows up in the file instead of
    // vanishing silently.
    private static void WriteField(Utf8JsonWriter w, string key, object? value)
    {
        switch (value)
        {
            case null: break;
            case string s: w.WriteString(key, s); break;
            case bool b: w.WriteBoolean(key, b); break;
            case int i: w.WriteNumber(key, i); break;
            case long l: w.WriteNumber(key, l); break;
            case double d: w.WriteNumber(key, d); break;
            case float f: w.WriteNumber(key, f); break;
            case decimal m: w.WriteNumber(key, m); break;
            case IEnumerable<string> strings:
                w.WriteStartArray(key);
                foreach (var item in strings) w.WriteStringValue(item);
                w.WriteEndArray();
                break;
            case IEnumerable<int> ints:
                w.WriteStartArray(key);
                foreach (var item in ints) w.WriteNumberValue(item);
                w.WriteEndArray();
                break;
            case ReplayLine nested: // a single nested object (rng_state)
                w.WriteStartObject(key);
                foreach (var (k, v) in nested.Fields) WriteField(w, k, v);
                w.WriteEndObject();
                break;
            case IEnumerable<ReplayLine> rows: // nested option/entity rows
                w.WriteStartArray(key);
                foreach (var row in rows)
                {
                    w.WriteStartObject();
                    foreach (var (k, v) in row.Fields) WriteField(w, k, v);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
            default: w.WriteString(key, value.ToString()); break;
        }
    }

    private static string Sanitize(string raw)
    {
        var chars = raw.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
        return new string(chars);
    }

    public void Dispose()
    {
        try
        {
            _channel.Writer.TryComplete();
            _cts.Cancel();
            _cts.Dispose();
        }
        catch { }
    }
}
