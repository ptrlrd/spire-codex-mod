using System;

namespace SpireCodex.Ui;

public sealed record Feature(string Name, string What, Func<bool> Get, Action<bool> Set);

public static class Features
{
    public static readonly Feature Plates = new("deck_toggle_card_reward_hints", "feat_plates_what",
        () => SpireCodexConfig.ShowCardRewardHints, v => SpireCodexConfig.ShowCardRewardHints = v);

    public static readonly Feature HoverTips = new("deck_toggle_hover_tips", "feat_hover_what",
        () => SpireCodexConfig.ShowHoverTips, v => SpireCodexConfig.ShowHoverTips = v);

    public static readonly Feature MapRoute = new("deck_toggle_map_guidance", "feat_map_what",
        () => SpireCodexConfig.ShowMapDanger, v => SpireCodexConfig.ShowMapDanger = v);

    public static readonly Feature UpcomingEvents = new("deck_toggle_upcoming_events", "feat_events_what",
        () => SpireCodexConfig.ShowUpcomingEvents, v => SpireCodexConfig.ShowUpcomingEvents = v);

    public static readonly Feature DamageMeter = new("deck_toggle_damage_meter", "feat_meter_what",
        () => SpireCodexConfig.ShowDamageMeter, v => SpireCodexConfig.ShowDamageMeter = v);

    public static readonly Feature RunCard = new("deck_toggle_post_run_card", "feat_runcard_what",
        () => SpireCodexConfig.ShowPostRunCard, v => SpireCodexConfig.ShowPostRunCard = v);

    public static readonly Feature BracketTag = new("deck_toggle_bracket_tag", "feat_bracket_what",
        () => SpireCodexConfig.ShowBracketTag, v => SpireCodexConfig.ShowBracketTag = v);

    public enum Preset { Track, Stats, Full, Custom }

    public static void Apply(Preset p)
    {
        if (p == Preset.Custom) return;
        Plates.Set(p == Preset.Full);
        HoverTips.Set(p != Preset.Track);
        MapRoute.Set(p == Preset.Full);
        DamageMeter.Set(p == Preset.Full);
        UpcomingEvents.Set(false);
        RunCard.Set(true);
        Save();
    }

    public static Preset Current()
    {
        foreach (var p in new[] { Preset.Track, Preset.Stats, Preset.Full })
            if (Plates.Get() == (p == Preset.Full)
                && HoverTips.Get() == (p != Preset.Track)
                && MapRoute.Get() == (p == Preset.Full)
                && DamageMeter.Get() == (p == Preset.Full)
                && !UpcomingEvents.Get())
                return p;
        return Preset.Custom;
    }

    public static void Save() => BaseLib.Config.ModConfigRegistry.Get<SpireCodexConfig>()?.Save();
}
