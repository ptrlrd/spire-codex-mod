using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

// A shop row names the entry bought, so a replay can buy the same one. Blitz entries were
// written as kind "other" with nothing else, which left the purchase unknowable.
public sealed class ShopRowsTests
{
    [Fact]
    public void ABlitzEntryIsKindBlitzEvenWhenItsNameSaysRelicOrRemoval()
    {
        Assert.Equal("blitz", ShopRows.Kind(new[] { "BlitzMerchantRelicEntry", "BlitzMerchantEntry", "MerchantEntry", "Object" }));
        Assert.Equal("blitz", ShopRows.Kind(new[] { "BlitzMerchantCardRemovalEntry", "BlitzMerchantEntry", "MerchantEntry", "Object" }));
    }

    [Theory]
    [InlineData("MerchantCardEntry", "card")]
    [InlineData("MerchantRelicEntry", "relic")]
    [InlineData("MerchantPotionEntry", "potion")]
    [InlineData("MerchantCardRemovalEntry", "removal_service")]
    [InlineData("SomeNewEntry", "other")]
    public void ANormalEntryKeepsTheKindItHadBefore(string type, string kind)
        => Assert.Equal(kind, ShopRows.Kind(new[] { type, "MerchantEntry", "Object" }));

    [Fact]
    public void ABlitzRowNamesTheEntryTypeItsClassItsSlotAndItsGameId()
    {
        var row = ShopRows.Entry(new ReplayLine("buy"), 3, "blitz", 1, null, 7u,
                                 "BlitzMerchantColorlessCardRewardEntry", "ColorlessCardReward");

        Assert.Equal(new Dictionary<string, object?>
                     {
                         ["purchase_id"] = 3, ["kind"] = "blitz", ["slot"] = 1, ["entry_id"] = 7L,
                         ["entry_class"] = "BlitzMerchantColorlessCardRewardEntry",
                         ["blitz_entry"] = "ColorlessCardReward",
                     },
                     row.Fields);
    }

    [Fact]
    public void ANormalRowKeepsItsIdAndSlotAndHasNoBlitzEntry()
    {
        var row = ShopRows.Entry(new ReplayLine("buy"), 1, "card", 4, "CRIMSON_MANTLE", null,
                                 "MerchantCardEntry", null);

        Assert.Equal(4, row.Fields["slot"]);
        Assert.Equal("CRIMSON_MANTLE", row.Fields["id"]);
        Assert.False(row.Fields.ContainsKey("blitz_entry"));
        Assert.False(row.Fields.ContainsKey("entry_id"));
    }
}
