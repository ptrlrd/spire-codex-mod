using System;
using System.IO;
using System.Text.Json;
using Godot;

namespace SpireCodex.Api;

// Holds the API JWT that authenticates uploads and presence. The token is acquired
// silently by SteamTicketAuth (Steamworks ticket -> POST /api/auth/steam/ticket) and
// persisted at %APPDATA%/SpireCodex/auth.json; SpireCodexClient attaches it as a bearer.
// ?steam_id attribution keeps working unauthenticated, so a missing token never breaks
// uploads, it just downgrades them.
//
// Tokens expire (7 days server-side), so IsSignedIn checks the JWT's exp claim with an
// hour of slack: a stale token reads as signed-out, which makes SteamTicketAuth re-run
// the silent exchange. There is no manual sign-in/out; privacy is controlled by the
// UploadRuns / ShareLiveStatus toggles instead.
public static class SteamAuth
{
    private static long? _expUnix;

    public static string? Token { get; private set; }
    public static string? SteamId { get; private set; }

    // Slack so we re-auth comfortably before the server starts rejecting the token.
    private const long ExpirySlackSeconds = 3600;

    public static bool IsSignedIn =>
        !string.IsNullOrEmpty(Token)
        && (_expUnix is not { } e || DateTimeOffset.UtcNow.ToUnixTimeSeconds() < e - ExpirySlackSeconds);

    public static void LoadStored()
    {
        try
        {
            var path = TokenPath();
            if (!File.Exists(path)) { Diag("no stored token"); return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            Token = r.TryGetProperty("jwt", out var j) ? j.GetString() : null;
            SteamId = r.TryGetProperty("steam_id", out var s) ? s.GetString() : null;
            _expUnix = ExpOf(Token);
            Diag(IsSignedIn
                ? $"loaded token for {SteamId}"
                : Token == null ? "stored token empty" : "stored token expired; will re-auth via steam ticket");
        }
        catch (Exception e) { Diag($"load failed: {e.Message}"); }
    }

    // Store a token from the silent Steamworks-ticket exchange (SteamTicketAuth).
    internal static void StoreExternalToken(string token, string? steamId)
    {
        Token = token;
        _expUnix = ExpOf(token);
        if (!string.IsNullOrEmpty(steamId)) SteamId = steamId;
        Save();
        Diag($"signed in via steam ticket as {SteamId}");
    }

    // The exp claim (unix seconds) from the JWT payload, or null when unreadable; a
    // token without a readable exp is treated as non-expiring (the server still
    // rejects it eventually and uploads degrade to ?steam_id).
    private static long? ExpOf(string? token)
    {
        try
        {
            var parts = token?.Split('.');
            if (parts is not { Length: 3 }) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var exp)
                ? exp : null;
        }
        catch { return null; }
    }

    private static void Save()
    {
        try
        {
            var path = TokenPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { jwt = Token, steam_id = SteamId }));
        }
        catch (Exception e) { Diag($"save failed: {e.Message}"); }
    }

    private static string TokenPath() => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "SpireCodex", "auth.json");

    private static void Diag(string msg)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "spire-codex-auth.log"), $"{DateTimeOffset.UtcNow:o}  {msg}\n"); }
        catch { /* ignore */ }
        GD.Print($"[SpireCodex] auth: {msg}");
    }
}
