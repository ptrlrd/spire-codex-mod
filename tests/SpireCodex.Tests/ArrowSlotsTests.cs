using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

// Stand-ins shaped like the v0.113 game types ArrowSlots reads by reflection:
// ArrowSlot (public readonly `owner`, `Status`, `DestroyAfterImpaled`), Player.PlayerCombatState,
// PlayerCombatState.Arrows (a copy), ImpaledPower.Arrows (a copy, first in first).
public enum ArrowStatus { Retrieved, Away, Destroyed }

public sealed class FakeArrowSlot
{
    public readonly FakePlayer owner;
    public ArrowStatus Status { get; set; }
    public bool DestroyAfterImpaled { get; set; }
    public FakeArrowSlot(FakePlayer owner) { this.owner = owner; }
}

public sealed class FakePlayer
{
    public FakeCombatState? PlayerCombatState { get; set; }
}

public sealed class FakeCombatState
{
    public readonly List<FakeArrowSlot> Slots = new();
    public IReadOnlyList<FakeArrowSlot> Arrows => Slots.ToList();
}

public sealed class FakeImpaled
{
    public readonly List<FakeArrowSlot> List = new();
    public IReadOnlyList<FakeArrowSlot> Arrows => List.ToList();
}

public sealed class ArrowSlotsTests
{
    private static (FakePlayer Player, FakeCombatState State) Fool(int slots)
    {
        var player = new FakePlayer();
        var state = new FakeCombatState();
        player.PlayerCombatState = state;
        for (var i = 0; i < slots; i++) state.Slots.Add(new FakeArrowSlot(player));
        return (player, state);
    }

    private static ReplayLine Row(string ev, object? slot, object? power = null, object? state = null)
        => ArrowSlots.Describe(new ReplayLine("arrow"), ev, slot, power, state);

    [Fact]
    public void AShotRowNamesTheSlotItLeftByPositionNotByState()
    {
        var (_, state) = Fool(3);
        // Three Retrieved slots look identical; the game shoots the last (ArrowCmd.cs:60).
        var shot = state.Slots[2];
        shot.Status = ArrowStatus.Away;

        var row = Row("shot", shot);

        Assert.Equal("shot", row.Fields["ev"]);
        Assert.Equal(2, row.Fields["slot"]);
        Assert.Equal("away", row.Fields["status"]);
        Assert.Equal(new List<string> { "retrieved", "retrieved", "away" }, row.Fields["slots"]);
        Assert.Equal(false, row.Fields["destroy_after"]);
    }

    [Fact]
    public void TwoSlotsInTheSameStateAreStillTwoSlots()
    {
        var (_, state) = Fool(3);
        state.Slots[1].Status = ArrowStatus.Away;
        state.Slots[2].Status = ArrowStatus.Away;

        Assert.Equal(1, Row("shot", state.Slots[1]).Fields["slot"]);
        Assert.Equal(2, Row("shot", state.Slots[2]).Fields["slot"]);
    }

    [Fact]
    public void AnImpaleRowListsTheEnemysSlotsFirstInFirst()
    {
        var (_, state) = Fool(3);
        var enemy = new FakeImpaled();
        // Slot 2 goes in, then slot 1: a second arrow into an enemy that already holds one.
        state.Slots[2].Status = ArrowStatus.Away;
        enemy.List.Add(state.Slots[2]);
        state.Slots[1].Status = ArrowStatus.Away;
        enemy.List.Add(state.Slots[1]);

        var row = Row("impale", state.Slots[1], enemy);

        Assert.Equal(1, row.Fields["slot"]);
        Assert.Equal(new List<int> { 2, 1 }, row.Fields["impaled"]);
        Assert.Equal(new List<string> { "retrieved", "away", "away" }, row.Fields["slots"]);
    }

    [Fact]
    public void ADislodgeRowShowsTheListAfterTheFirstArrowLeftIt()
    {
        var (_, state) = Fool(3);
        var enemy = new FakeImpaled();
        state.Slots[2].Status = ArrowStatus.Away;
        state.Slots[1].Status = ArrowStatus.Away;
        enemy.List.Add(state.Slots[2]);
        enemy.List.Add(state.Slots[1]);
        // RemoveFirstArrow (ImpaledPower.cs:69-79), then the hook fires with the slot still Away.
        enemy.List.RemoveAt(0);

        var row = Row("dislodge", state.Slots[2], enemy);

        Assert.Equal(2, row.Fields["slot"]);
        Assert.Equal("away", row.Fields["status"]);
        Assert.Equal(new List<int> { 1 }, row.Fields["impaled"]);
    }

    [Fact]
    public void AnEmptyImpaledListIsWrittenAsEmptyNotOmitted()
    {
        var (_, state) = Fool(1);
        var enemy = new FakeImpaled();
        state.Slots[0].Status = ArrowStatus.Retrieved;

        var row = Row("retrieve", state.Slots[0], enemy);

        Assert.Equal(new List<int>(), row.Fields["impaled"]);
        Assert.Equal("retrieved", row.Fields["status"]);
    }

    [Fact]
    public void AnEmptyShotCarriesTheSlotsButNoSlot()
    {
        var (_, state) = Fool(2);
        state.Slots[0].Status = ArrowStatus.Away;
        state.Slots[1].Status = ArrowStatus.Destroyed;

        var row = Row("empty_shot", null, null, state);

        Assert.False(row.Fields.ContainsKey("slot"));
        Assert.False(row.Fields.ContainsKey("status"));
        Assert.Equal(new List<string> { "away", "destroyed" }, row.Fields["slots"]);
    }

    [Fact]
    public void AnArrowFromAnotherQuiverDropsTheImpaledListRatherThanGuessIt()
    {
        var (_, state) = Fool(2);
        var (_, other) = Fool(2);
        var enemy = new FakeImpaled();
        enemy.List.Add(other.Slots[1]);
        enemy.List.Add(state.Slots[1]);

        var row = Row("impale", state.Slots[1], enemy);

        Assert.Equal(1, row.Fields["slot"]);
        Assert.False(row.Fields.ContainsKey("impaled"));
    }

    [Fact]
    public void ASlotWithNoReadableOwnerWritesNoIndex()
    {
        var orphan = new FakeArrowSlot(new FakePlayer()) { Status = ArrowStatus.Away };

        var row = Row("shot", orphan);

        Assert.False(row.Fields.ContainsKey("slot"));
        Assert.False(row.Fields.ContainsKey("slots"));
        Assert.Equal("away", row.Fields["status"]);
    }

    [Fact]
    public void AStatusThatIsNotTheGamesEnumIsNotWritten()
    {
        Assert.Null(ArrowSlots.StatusOf(new { Status = "Away" }));
        Assert.Null(ArrowSlots.Statuses(new List<object> { new { Status = 1 } }));
    }
}
