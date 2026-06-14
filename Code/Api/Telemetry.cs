using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// Daily-active-user ping. Posted once per launch to /api/telemetry/ping, carrying the Steam-issued
// JWT the mod already obtains at sign-in. The backend requires that token (a public client can hold
// no static secret, so the Steam ticket is the security gate) and counts a salted hash of the steam
// id per day - so DAU is one count per account per day, pseudonymous in storage, and can't be
// inflated without real game-owning Steam accounts. Fire-and-forget and silent.
public static class Telemetry
{
    public static void Start(string sts2Version) => _ = PingAsync(sts2Version);

    private static async Task PingAsync(string sts2Version)
    {
        try
        {
            // The ping is authenticated, so wait briefly for Steam sign-in (a stored token is
            // ready immediately for returning players; a fresh ticket auth takes a few seconds).
            for (var i = 0; i < 30 && string.IsNullOrEmpty(SteamAuth.Token); i++)
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(SteamAuth.Token)) return;

            var payload = JsonSerializer.Serialize(new
            {
                mod_version = ModVersion.Current,
                sts2_version = sts2Version,
            });
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Config.ApiBase}/telemetry/ping")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SteamAuth.Token);
            await http.SendAsync(req).ConfigureAwait(false);
        }
        catch { /* not signed in, endpoint not deployed, or offline; DAU misses this launch */ }
    }
}
