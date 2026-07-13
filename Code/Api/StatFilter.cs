namespace SpireCodex.Api;

// Which slice of the run population the community stats are drawn from. Chosen via the Stats
// setting (SpireCodexConfig.StatBracket -> StatsFilterKey) and applied by CodexScores. The Key
// rides the score fetch as ?stat_filter=; the Label is what the on-screen indicator shows.
//
// The set MIRRORS the website's stat toggles, and the backend owns what each Key means
// (ascension floor + the players' win-rate threshold), so the mod just passes the key through.
// "all" is the default and the value an un-upgraded backend falls back to.
// LabelKey is a Loc key (translated at the display site, not baked here, so a language switch
// takes effect without rebuilding this static table).
public readonly record struct StatFilterDef(string Key, string LabelKey);

public static class StatFilter
{
    public static readonly StatFilterDef[] Options =
    {
        new("all", "sf_all"),
        new("a10", "sf_a10"),
        new("a10_wr30", "sf_a10_wr30"),
        new("a10_wr50", "sf_a10_wr50"),
        new("a10_wr75", "sf_a10_wr75"),
    };

    public const string DefaultKey = "all";

    public static int IndexOf(string key)
    {
        for (var i = 0; i < Options.Length; i++)
            if (Options[i].Key == key) return i;
        return 0;
    }

    public static StatFilterDef ByKey(string key) => Options[IndexOf(key)];
}
