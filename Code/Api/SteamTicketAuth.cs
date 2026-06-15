using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Steamworks;

namespace SpireCodex.Api;

// Silent Steam sign-in: the mod runs inside an authenticated Steam game, so instead of the
// browser OpenID flow it asks Steamworks for a Web API auth ticket and exchanges it:
//   SteamUser.GetAuthTicketForWebApi("spire-codex") -> GetTicketForWebApiResponse_t
//   POST /api/auth/steam/ticket {"ticket": <hex>} -> { token, steamid }
// Zero clicks, cryptographically verified by Valve server-side, and the ONLY sign-in path
// (no manual buttons). Degrades silently when the endpoint isn't deployed yet or Steamworks
// isn't available; retries fast a few times, then every 5 minutes. Stays alive while signed
// in so an expiring JWT (7-day TTL, checked by SteamAuth.IsSignedIn) re-auths mid-session.
//
// A Node so the ticket request runs on the main thread after the game has initialized
// Steamworks (mod init is too early to ask reliably).
public partial class SteamTicketAuth : Node
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // First try a few seconds in (Steam is up by then), then back off. After the fast
    // attempts, keep retrying every 5 minutes for the whole session: the backend ticket
    // endpoint can deploy mid-session, and giving up would strand sign-in until a game
    // restart (seen live on 2026-06-11).
    private static readonly double[] AttemptAt = { 4, 30, 120 };
    private const double SlowRetrySeconds = 300;
    private int _attempt;
    private double _clock;
    private bool _inFlight;
    private bool _stopped; // hard failure (Steamworks unavailable); retrying is pointless
    private Callback<GetTicketForWebApiResponse_t>? _callback;

    public static void Start()
    {
        if (Engine.GetMainLoop() is not SceneTree tree) return;
        var n = new SteamTicketAuth { Name = "SpireCodexSteamTicketAuth" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, n);
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_stopped)
        {
            QueueFree(); // Steamworks is a lost cause this session
            return;
        }
        // While signed in, idle (don't free): when the token's exp slack runs out,
        // IsSignedIn flips false and the retry clock below resumes where it left off.
        if (SteamAuth.IsSignedIn) return;
        var nextAt = _attempt < AttemptAt.Length
            ? AttemptAt[_attempt]
            : AttemptAt[^1] + (_attempt - AttemptAt.Length + 1) * SlowRetrySeconds;
        if (_inFlight || _clock < nextAt) return;
        _attempt++;
        RequestTicket();
    }

    private void RequestTicket()
    {
        try
        {
            _callback ??= Callback<GetTicketForWebApiResponse_t>.Create(OnTicket);
            var handle = SteamUser.GetAuthTicketForWebApi("spire-codex");
            _inFlight = true;
            Diag($"ticket requested (attempt {_attempt}, handle {handle.m_HAuthTicket})");
        }
        catch (Exception e)
        {
            // Steamworks unavailable (dll mismatch, not initialized): the browser flow remains.
            Diag($"steamworks unavailable: {e.GetType().Name}: {e.Message}");
            _stopped = true;
        }
    }

    private void OnTicket(GetTicketForWebApiResponse_t r)
    {
        if (r.m_eResult != EResult.k_EResultOK || r.m_cubTicket <= 0)
        {
            Diag($"ticket failed: {r.m_eResult}");
            _inFlight = false;
            return;
        }
        var hex = Convert.ToHexString(r.m_rgubTicket, 0, r.m_cubTicket);
        Diag($"ticket received ({r.m_cubTicket} bytes); exchanging");
        _ = ExchangeAsync(hex);
    }

    private async Task ExchangeAsync(string ticketHex)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { ticket = ticketHex });
            using var resp = await Http
                .PostAsync($"{Config.ApiBase}/auth/steam/ticket",
                    new StringContent(body, Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Diag($"exchange rejected: HTTP {(int)resp.StatusCode} (endpoint not deployed yet?)");
                return;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
            var steamId = root.TryGetProperty("steamid", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(token)) { Diag("exchange returned no token"); return; }
            SteamAuth.StoreExternalToken(token!, steamId);
        }
        catch (Exception e) { Diag($"exchange failed: {e.GetType().Name}: {e.Message}"); }
        finally { _inFlight = false; }
    }

    private static void Diag(string msg)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "spire-codex-auth.log"), $"{DateTimeOffset.UtcNow:o}  [ticket] {msg}\n"); }
        catch { /* ignore */ }
        MainFile.Logger.Info($"ticket-auth: {msg}");
    }
}
