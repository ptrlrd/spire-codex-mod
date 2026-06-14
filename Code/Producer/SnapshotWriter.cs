using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireCodex.Producer;

// Writes the snapshot to Config.LiveStatePath as snake_case JSON. Shared read/write
// so external readers (overlay/desktop) can poll while we write; a reader can rarely
// catch a half-write, and the next tick fixes it. Never throws into the game loop.
internal static class SnapshotWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(Snapshot snapshot)
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
    }
}
