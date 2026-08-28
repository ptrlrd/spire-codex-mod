using Xunit;
using SpireCodex.Api;

namespace SpireCodex.Tests;

public sealed class RanksTests
{
    [Theory]
    [InlineData(90, "S")]
    [InlineData(89.99, "A")]
    [InlineData(78, "A")]
    [InlineData(77.99, "B")]
    [InlineData(65, "B")]
    [InlineData(64.99, "C")]
    [InlineData(54, "C")]
    [InlineData(50, "C")]
    [InlineData(49.99, "D")]
    [InlineData(35, "D")]
    [InlineData(34.99, "F")]
    public void TierMatchesWebsiteScoreBands(double score, string expected)
    {
        Assert.Equal(expected, Ranks.Tier(score));
    }
}
