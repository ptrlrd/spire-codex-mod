using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SpireCodex.Producer;

// Writes the snapshot to Config.LiveStatePath as snake_case JSON. Shared read/write
// so external readers (overlay/desktop) can poll while we write; a reader can rarely
// catch a half-write, and the next tick fixes it. Never throws into the game loop.
//
// The serialize + disk write run OFF the main thread: the producer ticks at 10 Hz and a
// synchronous file write on the game loop (worsened by AV scanning the temp file each time)
// caused per-action frame hitches. The producer hands us a fresh, no-longer-mutated Snapshot
// each tick, so serializing it on a background thread is safe. A single-flight guard drops a
// tick's write if the previous one is still going (the next snapshot is fresher anyway).
internal static class SnapshotWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int _writing; // 0 = idle, 1 = a write is in flight

    public static void Write(Snapshot snapshot)
    {
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return; // a write is already running
        _ = Task.Run(() =>
        {
            try
            {
                var json = JsonSerializer.Serialize(snapshot, Options);
                using var fs = new FileStream(
                    Config.LiveStatePath, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sw = new StreamWriter(fs);
                sw.Write(json);
            }
            catch
            {
                // Snapshot writing must never crash the game. Next tick retries.
            }
            finally
            {
                Interlocked.Exchange(ref _writing, 0);
            }
        });
    }
}
