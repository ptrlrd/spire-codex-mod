namespace SpireCodex.Replay;

// The Crystal Sphere minigame's rows, filled from plain values so the shape is testable
// without the game. The hooks in ReplayHooks read the game objects and call these.
//
// The event's rewards are a function of the grid (built from the event's seeded Rng, so a
// replay rebuilds it) and of the player's clicks, which nothing else on the tape records.
// Source: CrystalSphereMinigame (v0.113).
internal static class SphereRows
{
    // One CellClicked call: the clicked cell, the tool at the click, and DivinationCount
    // before the click spends one. CellClicked clears GetAdjacentCells(x, y) when the tool is
    // Big and the cell alone otherwise, so `tool` is the enum name as the game held it.
    public static ReplayLine Dig(ReplayLine line, int x, int y, string tool, int left)
        => line.Set("x", x).Set("y", y).Set("tool", tool).Set("left", left);

    // One OnItemRevealed call, in the order the game appends to the list it pays rewards
    // from. The item is described the way the game serializes it for co-op
    // (SerializableCrystalSphereItem.Serialize): a rarity only for a card reward or a potion,
    // the big flag only for gold. The serializable's other fields hold enum defaults that
    // mean nothing for that type, so they are never written.
    public static ReplayLine Reveal(ReplayLine line, string type, int x, int y,
                                    string? cardRarity, string? potionRarity, bool? bigGold)
    {
        line.Set("item", type).Set("x", x).Set("y", y);
        switch (type)
        {
            case "CardReward": line.Set("rarity", cardRarity); break;
            case "Potion": line.Set("rarity", potionRarity); break;
            case "Gold":
                if (bigGold is { } big) line.SetFlag("big", big);
                break;
        }
        return line;
    }

    // ForceMinigameEnd runs on every exit of the screen (NCrystalSphereScreen._ExitTree), but
    // only an exit before the last divination cuts the minigame short: the completion source
    // is still pending, the game cancels it, PlayMinigame never reaches CompleteMinigame, and
    // no reward is paid. Only that exit writes a row. A normal end needs none (its last dig
    // has left 1), and the normal exit fires after the map move, on the next floor.
    public static ReplayLine Forced(ReplayLine line, int left)
        => line.SetFlag("forced", true).Set("left", left);
}
