using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace SpireCodex.Core;

// Resolves the Slay the Spire 2 build version. Tag every snapshot and run upload with
// this: map/route data and some run fields are only valid for the version that
// produced them. See docs/GAME-STATE.md.
//
// The sts2 assembly's informational version is an unhelpful "0.1.0+<commit>", so we
// prefer the game's release_info.json next to the exe, which carries the real human
// version (e.g. "v0.103.3") plus the commit. Confirmed against v0.103.3.
internal static class Sts2Version
{
    private static string? _cached;

    public static string Current => _cached ??= Resolve();

    private static string Resolve()
    {
        return FromReleaseInfo() ?? FromAssembly() ?? "unknown";
    }

    private static string? FromReleaseInfo()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (exeDir == null) return null;

            var path = Path.Combine(exeDir, "release_info.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("version", out var v) || v.GetString() is not { Length: > 0 } version)
                return null;

            if (root.TryGetProperty("commit", out var c) && c.GetString() is { Length: > 0 } commit)
                return $"{version}+{commit}";
            return version;
        }
        catch
        {
            return null;
        }
    }

    private static string? FromAssembly()
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "sts2", StringComparison.OrdinalIgnoreCase));
            if (asm == null) return null;

            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info;

            return asm.GetName().Version?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
