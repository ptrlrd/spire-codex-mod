namespace SpireCodex.Api;

public readonly record struct StatFilterDef(string Key, string LabelKey);

public static class StatFilter
{
    public static readonly StatFilterDef[] Options =
    {
        new("all", "sf_all"),
        new("a10", "sf_a10"),
        new("wr30", "sf_wr30"),
        new("wr50", "sf_wr50"),
        new("wr75", "sf_wr75"),
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
