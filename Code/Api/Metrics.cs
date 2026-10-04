using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpireCodex.Api;

public static class Metrics
{
    private sealed record Tables(
        Dictionary<string, EntityMetrics> Cards,
        Dictionary<string, EntityMetrics> Relics,
        Dictionary<string, EntityMetrics> Potions,
        Dictionary<string, CampfireMetrics> Campfires,
        Dictionary<string, ShopMetrics> Shops,
        double? BaselineWinRate)
    {
        public static readonly Tables Empty = new(new(), new(), new(), new(), new(), null);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly ConcurrentDictionary<string, Tables> _cache = new();
    private static readonly ConcurrentDictionary<string, byte> _inFlight = new();
    private static volatile string _bracket = StatFilter.DefaultKey;
    private static volatile Tables _active = Tables.Empty;

    public static bool Loaded => _active != Tables.Empty;
    public static double? BaselineWinRate => _active.BaselineWinRate;

    public static EntityMetrics? Card(string id) => _active.Cards.GetValueOrDefault(id);
    public static EntityMetrics? Relic(string id) => _active.Relics.GetValueOrDefault(id);
    public static EntityMetrics? Potion(string id) => _active.Potions.GetValueOrDefault(id);
    public static CampfireMetrics? Campfire(string choice) => _active.Campfires.GetValueOrDefault(choice);
    public static ShopMetrics? Shop(string entityType, string id) => _active.Shops.GetValueOrDefault(entityType + ":" + id);

    private static string Key(string bracket) => bracket + "|" + Loc.Lang;

    public static void SetBracket(string key)
    {
        if (_bracket != key)
        {
            _bracket = key;
            _active = _cache.GetValueOrDefault(Key(key)) ?? Tables.Empty;
        }
        EnsureLoaded();
    }

    public static void EnsureLoaded()
    {
        var key = Key(_bracket);
        if (_cache.TryGetValue(key, out var t))
        {
            if (_active != t) _active = t;
            return;
        }
        if (_inFlight.TryAdd(key, 0)) _ = LoadAsync(_bracket, key);
    }

    private static async Task LoadAsync(string bracket, string key)
    {
        try
        {
            var lang = Loc.Lang;
            var cards = FetchAsync($"metrics/cards?bracket={bracket}");
            var relics = FetchAsync($"metrics/relics?bracket={bracket}");
            var potions = FetchAsync($"metrics/potions?bracket={bracket}");
            var campfires = FetchAsync($"metrics/campfires?bracket={bracket}&lang={lang}");
            var shops = FetchAsync($"metrics/shops?bracket={bracket}");
            await Task.WhenAll(cards, relics, potions, campfires, shops).ConfigureAwait(false);

            var tables = new Tables(
                MetricsParse.Entities(cards.Result), MetricsParse.Entities(relics.Result),
                MetricsParse.Entities(potions.Result), MetricsParse.Campfires(campfires.Result),
                MetricsParse.Shops(shops.Result), MetricsParse.Baseline(cards.Result));
            _cache[key] = tables;
            if (Key(_bracket) == key) _active = tables;
            MainFile.Logger.Info($"metrics: bracket={bracket} cards={tables.Cards.Count} relics={tables.Relics.Count} " +
                                 $"potions={tables.Potions.Count} campfires={tables.Campfires.Count} shops={tables.Shops.Count}");
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"metrics: load failed for bracket={bracket}: {e.Message}");
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private static async Task<JsonDocument?> FetchAsync(string path)
    {
        try
        {
            using var resp = await Http.GetAsync($"{Config.ApiBase}/runs/{path}").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        }
        catch { return null; }
    }
}
