using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// Mod version + game-version guard (X2 churn response). Knows three things:
//  - Current: this build's version, read from the SpireCodex.json manifest next to the dll
//    (one source of truth: the manifest).
//  - UpdateAvailable: a newer mod version published at GET /api/mod/latest (null when
//    current or the endpoint is not deployed).
//  - Sts2Untested: the running game build is newer than the newest tested one. The tested
//    version ships as a baked-in fallback and the server value overrides it, so after a
//    game patch the warning can be cleared (or raised) without shipping a new mod build.
// Surfaced in the F9 overlay header; everything degrades silently when offline.
public static class ModVersion
{
    // Bump when a release is verified against a newer game build.
    private const string FallbackTestedSts2 = "v0.103.3";

    public static string Current { get; } = ReadManifestVersion() ?? "v0.0.0";
    public static string? UpdateAvailable { get; private set; }
    public static string? UpdateUrl { get; private set; }
    public static bool Sts2Untested { get; private set; }

    public static void Start(string sts2Version)
    {
        Sts2Untested = Compare(sts2Version, FallbackTestedSts2) > 0;
        if (Sts2Untested)
            Godot.GD.Print($"[SpireCodex] game {sts2Version} is newer than tested {FallbackTestedSts2}");
        _ = CheckAsync(sts2Version);
    }

    private static async Task CheckAsync(string sts2Version)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = await http.GetStringAsync($"{Config.ApiBase}/mod/latest").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } latest
                && Compare(latest, Current) > 0)
            {
                UpdateAvailable = latest;
                UpdateUrl = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                Godot.GD.Print($"[SpireCodex] update available: {latest} (running {Current})");
            }

            if (root.TryGetProperty("sts2_tested", out var t) && t.GetString() is { Length: > 0 } tested)
                Sts2Untested = Compare(sts2Version, tested) > 0;
        }
        catch { /* endpoint not deployed yet, or offline; the baked-in fallback applies */ }
    }

    // Numeric part-by-part compare of "v0.103.3+commit" style strings. Unparseable parts
    // count as 0, so "unknown" never out-ranks a real version.
    internal static int Compare(string a, string b)
    {
        var pa = Parts(a);
        var pb = Parts(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static int[] Parts(string v)
    {
        var core = v.TrimStart('v', 'V');
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];
        return core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }

    private static string? ReadManifestVersion()
    {
        try
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (dir == null) return null;
            var path = Path.Combine(dir, "SpireCodex.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }
}
