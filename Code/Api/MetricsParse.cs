using System.Collections.Generic;
using System.Text.Json;

namespace SpireCodex.Api;

public sealed record WaxMetrics(int Picks, int Wins, double WinRate);

public sealed record EntityMetrics(
    string Id, double? Score, string? Tier, double? Elo, double WinRate, double? WinRateLow, double? WinRateHigh,
    double? PickRate, double? HoldRate, double? Lift, int LiftN, int Picks, int? Used, double? UseRate,
    WaxMetrics? Wax);

public sealed record CampfireMetrics(
    string Choice, string Name, int Chosen, double Share, int Wins, double WinRate, double? Lift, int LiftN,
    double? LowHpShare);

public sealed record ShopMetrics(
    string EntityType, string Id, int Seen, int Bought, double BuyRate, double WinRate, double? Lift, int LiftN);

public static class MetricsParse
{
    public static IEnumerable<JsonElement> Rows(JsonDocument? doc)
    {
        if (doc == null) yield break;
        var root = doc.RootElement;
        JsonElement rows = default;
        if (root.ValueKind == JsonValueKind.Array) rows = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array) rows = r;
        if (rows.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in rows.EnumerateArray()) yield return e;
    }

    public static double? Baseline(JsonDocument? doc)
        => doc != null && doc.RootElement.ValueKind == JsonValueKind.Object ? Num(doc.RootElement, "baseline_win_rate") : null;

    public static Dictionary<string, EntityMetrics> Entities(JsonDocument? doc)
    {
        var d = new Dictionary<string, EntityMetrics>();
        foreach (var e in Rows(doc))
        {
            var id = Str(e, "id");
            if (id == null) continue;
            if (e.TryGetProperty("upgraded", out var up) && up.ValueKind == JsonValueKind.True) id += "+";
            var ci = Pair(e, "win_rate_ci");
            WaxMetrics? wax = null;
            if (e.TryGetProperty("wax", out var w) && w.ValueKind == JsonValueKind.Object
                && Int(w, "picks") is { } wp && Int(w, "wins") is { } ww && Num(w, "win_rate") is { } wr)
                wax = new WaxMetrics(wp, ww, wr);
            d[id] = new EntityMetrics(
                id, Num(e, "score"), Str(e, "tier"), Num(e, "elo"), Num(e, "win_rate") ?? 0, ci?.lo, ci?.hi,
                Num(e, "pick_rate"), Num(e, "hold_rate"), Num(e, "lift"), Int(e, "lift_n") ?? 0, Int(e, "picks") ?? 0,
                Int(e, "used"), Num(e, "use_rate"), wax);
        }
        return d;
    }

    public static Dictionary<string, CampfireMetrics> Campfires(JsonDocument? doc)
    {
        var d = new Dictionary<string, CampfireMetrics>();
        foreach (var e in Rows(doc))
        {
            var choice = Str(e, "choice");
            if (choice == null) continue;
            d[choice] = new CampfireMetrics(
                choice, Str(e, "name") ?? choice, Int(e, "chosen") ?? 0, Num(e, "share") ?? 0, Int(e, "wins") ?? 0,
                Num(e, "win_rate") ?? 0, Num(e, "lift"), Int(e, "lift_n") ?? 0, Num(e, "low_hp_share"));
        }
        return d;
    }

    public static Dictionary<string, ShopMetrics> Shops(JsonDocument? doc)
    {
        var d = new Dictionary<string, ShopMetrics>();
        foreach (var e in Rows(doc))
        {
            var type = Str(e, "entity_type");
            var id = Str(e, "id");
            if (type == null || id == null) continue;
            d[type + ":" + id] = new ShopMetrics(
                type, id, Int(e, "seen") ?? 0, Int(e, "bought") ?? 0, Num(e, "buy_rate") ?? 0, Num(e, "win_rate") ?? 0,
                Num(e, "lift"), Int(e, "lift_n") ?? 0);
        }
        return d;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static (double lo, double hi)? Pair(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 2) return null;
        var a = v[0];
        var b = v[1];
        if (a.ValueKind != JsonValueKind.Number || b.ValueKind != JsonValueKind.Number) return null;
        return (a.GetDouble(), b.GetDouble());
    }
}
