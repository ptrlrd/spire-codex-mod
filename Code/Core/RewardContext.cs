namespace SpireCodex.Core;

// Shared, cross-component state for the current card-selection screen. CardRewardHints
// computes the best pick each tick (it sees the whole offered set); NativeHoverTips reads
// it so the native tooltip can label the winner. Kept in Core so the Ui -> Core dependency
// direction holds (Core never references Ui).
internal static class RewardContext
{
    // Bare id of the statistically best card currently offered, or null when no selection
    // screen is up (or none of the offered cards have community data).
    public static string? BestCardId;

    // The player's current character (bare id, e.g. "IRONCLAD"), published by the live-state
    // producer. Lets the hovertip show character-specific win rates. Null outside a run.
    public static string? Character;

    // Current HP as a percentage of max (0-100), published by the live-state producer.
    // Lets the campfire tip pick the matching HP-band community numbers. Null outside a run.
    public static double? HpPct;
}
