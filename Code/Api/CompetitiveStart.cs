using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpireCodex.Api;

public static class CompetitiveStart
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static void Send(string seed, long startTime, string? character, string? gameMode,
        int ascension, int playerCount, int modifiers)
    {
        var body = JsonSerializer.Serialize(new
        {
            seed,
            start_time = startTime,
            character,
            game_mode = gameMode,
            ascension,
            player_count = playerCount,
            modifiers,
        });
        _ = PostAsync(body);
    }

    private static async Task PostAsync(string body)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!string.IsNullOrEmpty(SteamAuth.Token))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{Config.ApiBase}/runs/competitive/start");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SteamAuth.Token);
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode) return;
                    var code = (int)resp.StatusCode;
                    if (code is >= 400 and < 500 and not 401 and not 429)
                    {
                        MainFile.Logger.Info($"competitive: start rejected ({code})");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                MainFile.Logger.Info($"competitive: start not sent: {e.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(20 * (attempt + 1))).ConfigureAwait(false);
        }
    }
}
