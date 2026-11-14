using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

// Fakes shaped like the game's Player -> UnlockState -> SerializableUnlockState chain
// (Player.cs:57, UnlockState.cs:124, SerializableUnlockState.cs:10-19), so the row is read
// through the same reflection path the mod takes in game.
public sealed class UnlockStateRowTests
{
    private sealed record FakeModelId(string Category, string Entry)
    {
        public override string ToString() => Category + "." + Entry;
    }

    private sealed class FakeSerializable
    {
        public List<string> UnlockedEpochs { get; set; } = new();
        public List<FakeModelId> EncountersSeen { get; set; } = new();
        public int NumberOfRuns { get; set; }
    }

    private sealed class FakeUnlockState(FakeSerializable s)
    {
        public FakeSerializable ToSerializable() => s;
    }

    private sealed class FakePlayer(FakeUnlockState? u)
    {
        public FakeUnlockState? UnlockState { get; } = u;
    }

    private static FakePlayer Player(FakeSerializable s) => new(new FakeUnlockState(s));

    [Fact]
    public void AFreshProfileWritesAnEmptyEpochListNotAMissingOne()
    {
        var row = UnlockStateRow.Read(Player(new FakeSerializable()));

        Assert.NotNull(row);
        Assert.Equal(new List<string>(), row!.Fields["unlocked_epochs"]);
        Assert.Equal(new List<string>(), row.Fields["encounters_seen"]);
        Assert.Equal(0, row.Fields["number_of_runs"]);
    }

    [Fact]
    public void ThePartlyUnlockedListIsWrittenExactlyAsTheGameHandsItOver()
    {
        var epochs = new List<string> { "NEOW_EPOCH", "SILENT1_EPOCH", "COLORLESS1_EPOCH" };
        var row = UnlockStateRow.Read(Player(new FakeSerializable
        {
            UnlockedEpochs = epochs,
            EncountersSeen = new() { new("ENCOUNTER", "SHRINKER_BEETLE_WEAK"), new("ENCOUNTER", "CEREMONIAL_BEAST_BOSS") },
            NumberOfRuns = 7,
        }));

        Assert.Equal(epochs, row!.Fields["unlocked_epochs"]);
        Assert.Equal(new List<string> { "ENCOUNTER.SHRINKER_BEETLE_WEAK", "ENCOUNTER.CEREMONIAL_BEAST_BOSS" },
                     row.Fields["encounters_seen"]);
        Assert.Equal(7, row.Fields["number_of_runs"]);
    }

    [Fact]
    public void APlayerWithNoReadableUnlockStateWritesNoKeyAtAll()
    {
        Assert.Null(UnlockStateRow.Read(null));
        Assert.Null(UnlockStateRow.Read(new FakePlayer(null)));
        Assert.Null(UnlockStateRow.Read(new object()));
    }

    [Fact]
    public void OneUnreadableEntryDropsThatListRatherThanShrinkingIt()
    {
        var row = UnlockStateRow.Read(Player(new FakeSerializable
        {
            UnlockedEpochs = new() { "NEOW_EPOCH", null!, "SILENT1_EPOCH" },
            NumberOfRuns = 3,
        }));

        Assert.False(row!.Fields.ContainsKey("unlocked_epochs"));
        Assert.Equal(3, row.Fields["number_of_runs"]);
    }
}
