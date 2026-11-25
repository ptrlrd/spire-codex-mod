using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

public sealed class CardCostsTests
{
    [Fact]
    public void ACardAtItsBaseCostWritesNothing()
    {
        CardCosts.Reset();
        Assert.False(CardCosts.Changed(1, 1, 1));
        Assert.False(CardCosts.Changed(1, 1, 1));
    }

    [Fact]
    public void ARandomizedCostIsWrittenOnceUntilItChangesAgain()
    {
        CardCosts.Reset();
        Assert.True(CardCosts.Changed(2, 1, 3));
        Assert.False(CardCosts.Changed(2, 1, 3));
        Assert.True(CardCosts.Changed(2, 1, 0));
    }

    [Fact]
    public void GoingBackToTheBaseCostIsWritten()
    {
        CardCosts.Reset();
        Assert.True(CardCosts.Changed(3, 2, 0));
        Assert.True(CardCosts.Changed(3, 2, 2));
        Assert.False(CardCosts.Changed(3, 2, 2));
    }

    [Fact]
    public void ANewCombatForgetsTheLastCost()
    {
        CardCosts.Reset();
        Assert.True(CardCosts.Changed(4, 1, 3));
        CardCosts.Reset();
        Assert.True(CardCosts.Changed(4, 1, 3));
    }

    [Fact]
    public void AnUnknownCardWritesNothing()
    {
        CardCosts.Reset();
        Assert.False(CardCosts.Changed(0, 1, 3));
    }
}
