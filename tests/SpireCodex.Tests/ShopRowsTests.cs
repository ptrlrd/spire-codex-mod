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
        var row = ShopRows.Entry(new ReplayLine("buy"), 271L, "blitz", 1, null, 7u,
                                 "BlitzMerchantColorlessCardRewardEntry", "ColorlessCardReward");

        Assert.Equal(new Dictionary<string, object?>
                     {
                         ["purchase_id"] = 271L, ["kind"] = "blitz", ["slot"] = 1, ["entry_id"] = 7L,
                         ["entry_class"] = "BlitzMerchantColorlessCardRewardEntry",
                         ["blitz_entry"] = "ColorlessCardReward",
                     },
                     row.Fields);
    }

    // Lord's Parasol buys every entry with ignoreCost: true, affordable or not.
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, false)]
    public void APurchaseStartsWhenTheGameWouldTryIt(bool stocked, bool enoughGold, bool ignoreCost, bool starts)
        => Assert.Equal(starts, ShopRows.PurchaseCanStart(stocked, enoughGold, ignoreCost));

    [Fact]
    public void BlitzOffersAreListedInInventoryOrderWithTheirStatus()
    {
        var rows = ShopRows.BlitzOffers(new (string?, string, string?)[]
        {
            ("UpgradedRareCard", "BlitzMerchantUpgradedRareCardEntry", "Used"),
            ("CardRemoval", "BlitzMerchantCardRemovalEntry", "Disabled"),
            ("Glam", "BlitzMerchantGlamEntry", "Available"),
        })!;

        Assert.Equal(3, rows.Count);
        Assert.Equal(new Dictionary<string, object?>
                     {
                         ["slot"] = 1, ["blitz_entry"] = "CardRemoval",
                         ["entry_class"] = "BlitzMerchantCardRemovalEntry", ["status"] = "disabled",
                     },
                     rows[1].Fields);
        Assert.Equal("used", rows[0].Fields["status"]);
        Assert.Equal(2, rows[2].Fields["slot"]);
    }

    [Fact]
    public void ANormalShopWritesNoBlitzList()
        => Assert.Null(ShopRows.BlitzOffers(Array.Empty<(string?, string, string?)>()));

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
