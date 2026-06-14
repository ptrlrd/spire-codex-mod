using System;
using System.IO;
using System.Text.Json;

namespace SpireCodex.Api;

// Run-upload onboarding state, persisted to %APPDATA%/SpireCodex/consent.json. Two flags:
//  - Answered: the player has made an explicit choice (Turn on / Keep off), so the
//    first-run onboarding prompt shows exactly once per machine.
//  - Granted: the player turned uploads on AND consented, so the uploader may send. Until
//    granted, RunUploader holds every upload at the gate.
// Uploads default OFF; "Turn on" grants, "Keep off" records the choice without granting.
public static class Consent
{
    private static bool? _granted;
    private static bool? _answered;

    // Fired on the grant transition so the uploader can flush held runs + kick the backfill.
    public static event Action? OnGranted;

    public static bool Granted => _granted ??= ReadBool("granted");
    public static bool Answered => _answered ??= ReadBool("answered");

    // "Turn on": enable + consent.
    public static void Grant()
    {
        _granted = true;
        _answered = true;
        Persist();
        OnGranted?.Invoke();
    }

    // "Keep off": record the choice so we don't re-ask, without granting.
    public static void Decline()
    {
        _granted = false;
        _answered = true;
        Persist();
    }

    private static void Persist()
    {
        try
        {
            var path = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                granted = _granted ?? false,
                answered = _answered ?? false,
                at = DateTimeOffset.UtcNow.ToString("o"),
            }));
        }
        catch { /* the in-memory state still applies this session */ }
    }

    private static bool ReadBool(string key)
    {
        try
        {
            var path = StorePath();
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(key, out var v) && v.GetBoolean();
        }
        catch { return false; }
    }

    private static string StorePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpireCodex", "consent.json");
}
