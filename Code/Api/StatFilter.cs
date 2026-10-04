namespace SpireCodex.Api;

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
