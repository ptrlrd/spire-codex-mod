using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

// replay_version 10: one player's journal says whose each row is, so a replay can drive the
// teammate too. Nothing here may change a single-player row.
public sealed class CoopRowsTests
{
    private sealed class FakePlayer
    {
        public ulong NetId { get; init; }
    }

    [Fact]
    public void ASlotIsThePlayersIndexInRunStatePlayers()
    {
        var a = new FakePlayer { NetId = 76561190000000001 };
        var b = new FakePlayer { NetId = 76561190000000002 };
        var players = new List<object> { a, b };

        Assert.Equal(0, CoopRows.SlotIn(players, a));
        Assert.Equal(1, CoopRows.SlotIn(players, b));
        Assert.Null(CoopRows.SlotIn(players, new FakePlayer()));
        Assert.Equal(1, CoopRows.SlotOfNetId(players, 76561190000000002));
        Assert.Null(CoopRows.SlotOfNetId(players, 5));
    }

    [Fact]
    public void SinglePlayerRowsGetNoOwnershipFields()
    {
        var row = new ReplayLine("play").Set("id", "ZAP");
        CoopRows.Owner(row, coop: false, mine: true, slot: 0);
        CoopRows.Side(row, "dst", coop: false, mine: true, slot: 0, ownerMine: null, ownerSlot: null);

        Assert.Equal(new Dictionary<string, object?> { ["id"] = "ZAP" }, row.Fields);
    }

    [Fact]
    public void ACoopRowNamesItsPlayerBySlotNeverByNetId()
    {
        var row = CoopRows.Owner(new ReplayLine("play"), coop: true, mine: false, slot: 1);

        Assert.Equal(new Dictionary<string, object?> { ["mine"] = false, ["player_slot"] = 1 }, row.Fields);
    }

    [Fact]
    public void ACreatureFieldSaysWhichPlayerOrWhosePet()
    {
        var hit = new ReplayLine("hit");
        CoopRows.Side(hit, "src", true, mine: false, slot: 1, ownerMine: null, ownerSlot: null);
        CoopRows.Side(hit, "dst", true, mine: null, slot: null, ownerMine: true, ownerSlot: 0);

        Assert.Equal(new Dictionary<string, object?>
                     {
                         ["src_mine"] = false, ["src_player_slot"] = 1,
                         ["dst_owner_mine"] = true, ["dst_owner_slot"] = 0,
                     },
                     hit.Fields);
    }

    [Fact]
    public void AMonsterGetsNoCreatureOwnership()
    {
        var hit = CoopRows.Side(new ReplayLine("hit"), "dst", true, null, null, null, null);
        Assert.Empty(hit.Fields);
    }

    // Shapes of the game's messages (fields, as the game declares them).
    private sealed class FakeCoord { public int col = 3; public int row = 7; }
    private sealed class FakeMapLocation { public int actIndex = 1; public object? coord = new FakeCoord(); }
    private sealed class FakeRunLocation { public object mapLocation = new FakeMapLocation(); public int? roomId = 2; }

    private sealed class FakeRewardSelected
    {
        public FakeRunLocation location = new();
        public FakeRunLocation Location => location;
        public int setId = 4;
        public int rewardIndex = 1;
    }

    private sealed class FakeMerchantPurchase
    {
        public FakeRunLocation Location { get; set; } = new();
        public uint id = 13;
    }

    private enum FakeOptionIndexType { Event, RestSite }

    private sealed class FakeOptionChosen
    {
        public FakeOptionIndexType type = FakeOptionIndexType.RestSite;
        public uint optionIndex = 2;
    }

    private enum FakeRewardType { None, SpecialCard }
    private sealed class FakeModel { public string Id = "CARD.CLAW"; }

    private sealed class FakeRewardObtained
    {
        public FakeRewardType rewardType = FakeRewardType.SpecialCard;
        public object? cardModel = new FakeModel();
        public bool wasSkipped;
    }

    [Fact]
    public void ARewardPickNamesTheSetAndIndexAndWhere()
    {
        var row = CoopRows.PeerMessage(new ReplayLine("peer_msg"), "RewardSelectedMessage", 1, false,
                                       new FakeRewardSelected());

        Assert.NotNull(row);
        Assert.Equal(new Dictionary<string, object?>
                     {
                         ["msg"] = "RewardSelectedMessage", ["sender_slot"] = 1, ["mine"] = false,
                         ["location"] = "1:3,7:2", ["set_id"] = 4, ["reward_index"] = 1,
                     },
                     row!.Fields);
    }

    [Fact]
    public void ATeammateShopBuyNamesTheEntryId()
    {
        var row = CoopRows.PeerMessage(new ReplayLine("peer_msg"), "MerchantEntryPurchasedMessage", 1, false,
                                       new FakeMerchantPurchase())!;
        Assert.Equal(13L, row.Fields["entry_id"]);
    }

    [Fact]
    public void AnOptionChoiceSaysEventOrRestSite()
    {
        var row = CoopRows.PeerMessage(new ReplayLine("peer_msg"), "OptionIndexChosenMessage", 1, false,
                                       new FakeOptionChosen())!;
        Assert.Equal("rest_site", row.Fields["option_type"]);
        Assert.Equal(2L, row.Fields["option_index"]);
        Assert.False(row.Fields.ContainsKey("location"));
    }

    [Fact]
    public void AnObtainedRewardNamesTheCardBare()
    {
        var row = CoopRows.PeerMessage(new ReplayLine("peer_msg"), "RewardObtainedMessage", 1, false,
                                       new FakeRewardObtained())!;
        Assert.Equal("special_card", row.Fields["reward_type"]);
        Assert.Equal("CLAW", row.Fields["card"]);
        Assert.Equal(false, row.Fields["skipped"]);
    }

    [Theory]
    [InlineData("RestSiteOptionHoveredMessage")]
    [InlineData("ActionEnqueuedMessage")]
    [InlineData("RequestEnqueueActionMessage")]
    public void CosmeticAndCombatActionMessagesWriteNoRow(string type)
    {
        Assert.False(CoopRows.Records(type));
        Assert.Null(CoopRows.PeerMessage(new ReplayLine("peer_msg"), type, 1, false, new object()));
    }

    [Fact]
    public void AnUnknownMessageStillWritesItsTypeAndSender()
    {
        var row = CoopRows.PeerMessage(new ReplayLine("peer_msg"), "SomeNewMessage", 0, false, new object())!;
        Assert.Equal(new Dictionary<string, object?>
                     {
                         ["msg"] = "SomeNewMessage", ["sender_slot"] = 0, ["mine"] = false,
                     },
                     row.Fields);
    }

    [Fact]
    public void ALocationWithoutACoordLeavesItEmpty()
    {
        var loc = new FakeRunLocation { roomId = null };
        ((FakeMapLocation)loc.mapLocation).coord = null;
        Assert.Equal("1::", CoopRows.Location(loc));
    }
}
