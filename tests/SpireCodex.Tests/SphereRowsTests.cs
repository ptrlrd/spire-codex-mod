using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

// The Crystal Sphere rows carry the player's clicks and what they revealed, so a replay can
// dig the same cells in the same order and check the rewards (STS-238).
public sealed class SphereRowsTests
{
    [Fact]
    public void ADigRowNamesTheCellTheToolAndTheDivinationsBeforeTheClick()
    {
        var row = SphereRows.Dig(new ReplayLine("sphere_dig"), 3, 5, "Big", 2);

        Assert.Equal("sphere_dig", row.Kind);
        Assert.Equal(new Dictionary<string, object?> { ["x"] = 3, ["y"] = 5, ["tool"] = "Big", ["left"] = 2 },
                     row.Fields);
    }

    [Fact]
    public void ACardRewardRevealCarriesTheCardRarityAndNotThePotionDefault()
    {
        var row = SphereRows.Reveal(new ReplayLine("sphere_reveal"), "CardReward", 7, 1, "Rare", "None", false);

        Assert.Equal(new Dictionary<string, object?>
                     {
                         ["item"] = "CardReward", ["x"] = 7, ["y"] = 1, ["rarity"] = "Rare",
                     },
                     row.Fields);
    }

    [Fact]
    public void APotionRevealCarriesThePotionRarityAndNotTheCardDefault()
    {
        var row = SphereRows.Reveal(new ReplayLine("sphere_reveal"), "Potion", 0, 9, "None", "Common", false);

        Assert.Equal("Common", row.Fields["rarity"]);
        Assert.False(row.Fields.ContainsKey("big"));
    }

    [Fact]
    public void AGoldRevealSaysWhetherItIsBigEvenWhenItIsNot()
    {
        var small = SphereRows.Reveal(new ReplayLine("sphere_reveal"), "Gold", 4, 4, "None", "None", false);
        var big = SphereRows.Reveal(new ReplayLine("sphere_reveal"), "Gold", 4, 4, "None", "None", true);

        Assert.Equal(false, small.Fields["big"]);
        Assert.Equal(true, big.Fields["big"]);
        Assert.False(small.Fields.ContainsKey("rarity"));
    }

    [Theory]
    [InlineData("Relic")]
    [InlineData("Curse")]
    public void ARelicOrCurseRevealCarriesNoRarityAndNoGoldFlag(string type)
    {
        var row = SphereRows.Reveal(new ReplayLine("sphere_reveal"), type, 2, 6, "None", "None", false);

        Assert.Equal(new Dictionary<string, object?> { ["item"] = type, ["x"] = 2, ["y"] = 6 }, row.Fields);
    }

    [Fact]
    public void AForcedEndRowSaysForcedAndHowManyDivinationsWereLeft()
    {
        var cut = SphereRows.Forced(new ReplayLine("sphere_end"), 2);

        Assert.Equal(new Dictionary<string, object?> { ["forced"] = true, ["left"] = 2 }, cut.Fields);
    }
}
