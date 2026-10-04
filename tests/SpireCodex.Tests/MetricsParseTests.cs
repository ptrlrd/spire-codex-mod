using System.Text.Json;
using SpireCodex.Api;
using Xunit;

namespace SpireCodex.Tests;

public class MetricsParseTests
{
    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    [Fact]
    public void ParsesEntityRowsWithWaxAndUpgradedSuffix()
    {
        var doc = Doc("""
            {"entity_type":"relics","bracket":"all","baseline_win_rate":53.3,"rows":[
             {"id":"VAJRA","upgraded":false,"score":65,"tier":"B","elo":1518.6,"win_rate":57.8,"win_rate_ci":[56.9,58.7],
              "pick_rate":24.1,"hold_rate":14.7,"lift":4.4,"lift_n":12126,"picks":12126,"wins":7008,
              "wax":{"picks":111,"wins":68,"win_rate":61.3,"win_rate_ci":[52.0,69.8]}},
             {"id":"LIFT","upgraded":true,"score":68,"tier":"B","elo":1265.0,"win_rate":49.7,"win_rate_ci":[45.4,54.0],
              "pick_rate":5.0,"hold_rate":0.6,"lift":null,"lift_n":12,"picks":523}]}
            """);
        var d = MetricsParse.Entities(doc);

        var v = d["VAJRA"];
        Assert.Equal(4.4, v.Lift);
        Assert.Equal(12126, v.LiftN);
        Assert.Equal(14.7, v.HoldRate);
        Assert.Equal(56.9, v.WinRateLow);
        Assert.Equal(58.7, v.WinRateHigh);
        Assert.NotNull(v.Wax);
        Assert.Equal(61.3, v.Wax!.WinRate);
        Assert.Equal(111, v.Wax.Picks);
        Assert.Null(v.Used);

        Assert.False(d.ContainsKey("LIFT"));
        var up = d["LIFT+"];
        Assert.Null(up.Lift);
        Assert.Null(up.Wax);
        Assert.Equal(53.3, MetricsParse.Baseline(doc));
    }

    [Fact]
    public void ParsesPotionUseRate()
    {
        var d = MetricsParse.Entities(Doc("""
            {"rows":[{"id":"COLORLESS_POTION","upgraded":false,"score":49,"elo":null,"win_rate":49.8,"pick_rate":null,
              "hold_rate":24.0,"lift":-0.6,"lift_n":19902,"picks":19902,"used":18360,"use_rate":92.3}]}
            """));
        var p = d["COLORLESS_POTION"];
        Assert.Null(p.Elo);
        Assert.Null(p.PickRate);
        Assert.Equal(18360, p.Used);
        Assert.Equal(92.3, p.UseRate);
        Assert.Equal(-0.6, p.Lift);
    }

    [Fact]
    public void ParsesCampfireAndShopRows()
    {
        var c = MetricsParse.Campfires(Doc("""
            {"bracket":"all","rows":[{"choice":"SMITH","name":"Schmieden","chosen":260254,"share":55.8,"wins":155813,
              "win_rate":59.9,"win_rate_ci":[59.7,60.1],"lift":6.2,"lift_n":260254,"low_hp_share":16.5}]}
            """));
        Assert.Equal("Schmieden", c["SMITH"].Name);
        Assert.Equal(55.8, c["SMITH"].Share);
        Assert.Equal(6.2, c["SMITH"].Lift);

        var s = MetricsParse.Shops(Doc("""
            {"rows":[{"entity_type":"cards","id":"ALCHEMIZE","seen":6687,"bought":259,"buy_rate":3.9,"wins":205,
              "win_rate":79.2,"win_rate_ci":[73.8,83.7],"lift":16.0,"lift_n":259}]}
            """));
        var row = s["cards:ALCHEMIZE"];
        Assert.Equal(3.9, row.BuyRate);
        Assert.Equal(6687, row.Seen);
        Assert.Equal(16.0, row.Lift);
    }

    [Fact]
    public void ToleratesEmptyNullAndBareArrayResponses()
    {
        Assert.Empty(MetricsParse.Entities(null));
        Assert.Empty(MetricsParse.Entities(Doc("""{"rows":[]}""")));
        Assert.Empty(MetricsParse.Campfires(Doc("""{"detail":"unknown bracket: x"}""")));
        Assert.Single(MetricsParse.Shops(Doc("""[{"entity_type":"relics","id":"VAJRA","seen":1,"bought":1,"buy_rate":100,"win_rate":0}]""")));
        Assert.Null(MetricsParse.Baseline(null));
    }
}
