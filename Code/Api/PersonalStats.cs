using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpireCodex.Api;

// One player's own pick record for a single entity: taken Picked of the Offered times it was
// offered (e.g. a card seen on 40 reward screens kept 10; a relic offered at 3 ancients taken 2).
public sealed record UserPick(int Picked, int Offered);

// The signed-in player's own pick rates, per surface. Cards = card-reward keep rate; Ancients =
// relic take rate at the 3-relic ancient offers (Neow, Nonupeipe, and the other ancients).
public sealed record PersonalStatsData(
    IReadOnlyDictionary<string, UserPick> Cards,
    IReadOnlyDictionary<string, UserPick> Ancients);

// One-shot cache of the signed-in player's OWN pick rates (/api/runs/me/picks), shown as a
// "You: ..." line beside the community numbers. Needs a Steam sign-in (the server scopes the
// data to the JWT's verified steam_id), so it stays null and silent when signed out. A
// successful-but-empty response (a new player) is cached so we stop refetching; only a failed
// request leaves it null to retry.
public static class PersonalStats
{
    private static PersonalStatsData? _data;
    private static bool _loading;

    public static UserPick? Card(string? cardId) => Lookup(_data?.Cards, cardId);
    public static UserPick? Ancient(string? relicId) => Lookup(_data?.Ancients, relicId);

    private static UserPick? Lookup(IReadOnlyDictionary<string, UserPick>? map, string? id) =>
        map == null || string.IsNullOrEmpty(id) ? null : map.GetValueOrDefault(id.ToUpperInvariant());

    public static void EnsureLoaded()
    {
        if (_data != null || _loading) return;
        if (string.IsNullOrEmpty(SteamAuth.Token)) return; // needs sign-in; retried once a token lands
        _loading = true;
        _ = LoadAsync();
    }

    private static async Task LoadAsync()
    {
        try
        {
            // Non-null is a success (even when empty): cache it so we don't refetch every hover.
            // Null is a failed/unauthorized request: leave _data null so EnsureLoaded retries.
            var d = await new SpireCodexClient().GetUserPicksAsync().ConfigureAwait(false);
            if (d != null) _data = d;
        }
        finally { _loading = false; }
    }
}
