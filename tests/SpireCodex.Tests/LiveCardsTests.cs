using SpireCodex.Replay;
using Xunit;

namespace SpireCodex.Tests;

// Fakes shaped like the game members LiveCards reads: CardModel._owner and Pile,
// CardPile.IsCombatPile, Player.Creature, Creature.CombatState, RunState.Players.
public sealed class LiveCardsTests : IDisposable
{
    private sealed class FakeCombat { }
    private sealed class FakeCreature { public object? CombatState { get; set; } }
    private sealed class FakePile
    {
        public bool IsCombatPile { get; init; }
        public List<FakeCard> Cards { get; } = new();
    }
    private sealed class FakePlayer
    {
        public FakeCreature Creature { get; } = new();
        public List<FakePile> Piles { get; } = new();
    }
    private sealed class FakeRun { public List<FakePlayer> Players { get; } = new(); }
    private sealed class FakeCard
    {
#pragma warning disable CS0414, IDE0052
        private FakePlayer? _owner;
#pragma warning restore CS0414, IDE0052
        public FakeCard(FakePlayer? owner) { _owner = owner; }
        public FakePile? Pile => _owner?.Piles.FirstOrDefault(p => p.Cards.Contains(this));
    }

    private readonly FakeRun _run = new();
    private readonly FakeCombat _fight = new();
    private readonly FakePlayer _player = new();
    private readonly FakePile _hand = new() { IsCombatPile = true };
    private readonly FakePile _deck = new() { IsCombatPile = false };

    public LiveCardsTests()
    {
        _run.Players.Add(_player);
        _player.Piles.Add(_hand);
        _player.Piles.Add(_deck);
        _player.Creature.CombatState = _fight;
        LiveCards.Bind(() => _run, () => _fight);
    }

    public void Dispose() => LiveCards.Bind(null, null);

    private FakeCard InPile(FakePlayer owner, FakePile pile)
    {
        var c = new FakeCard(owner);
        pile.Cards.Add(c);
        return c;
    }

    // A simulator clones the fight: its player and piles are fresh objects.
    private (FakePlayer Player, FakePile Hand) SimulatedClone()
    {
        var clone = new FakePlayer();
        var hand = new FakePile { IsCombatPile = true };
        clone.Piles.Add(hand);
        clone.Creature.CombatState = new FakeCombat();
        return (clone, hand);
    }

    [Fact]
    public void ALiveCardsTransformWritesARow()
    {
        Assert.True(LiveCards.RecordTransform(InPile(_player, _hand)));
        Assert.True(LiveCards.RecordTransform(InPile(_player, _deck)));
    }

    [Fact]
    public void AClonedCardsTransformWritesNoRow()
    {
        var (clone, hand) = SimulatedClone();
        Assert.False(LiveCards.RecordTransform(InPile(clone, hand)));
        Assert.Equal(LiveCards.Verdict.Foreign, LiveCards.Of(InPile(clone, hand)));
    }

    [Fact]
    public void ALiveOwnersCardInAClonedFightWritesNoRow()
    {
        // The live Player object, but a combat pile whose fight is not the one CombatManager holds.
        var player = new FakePlayer();
        var hand = new FakePile { IsCombatPile = true };
        player.Piles.Add(hand);
        player.Creature.CombatState = new FakeCombat();
        _run.Players.Add(player);
        Assert.False(LiveCards.RecordTransform(InPile(player, hand)));
    }

    [Fact]
    public void ATransformThatCannotBeProvenLiveWritesNoRow()
    {
        Assert.False(LiveCards.RecordTransform(new FakeCard(_player))); // in no pile
        Assert.False(LiveCards.RecordTransform(new FakeCard(null)));    // no owner
        LiveCards.Bind(null, null);                                     // accessors not found
        Assert.False(LiveCards.RecordTransform(InPile(_player, _hand)));
    }

    [Fact]
    public void UpgradeRowsDropOnlyCardsProvenForeign()
    {
        var (clone, hand) = SimulatedClone();
        Assert.False(LiveCards.Record(InPile(clone, hand)));
        Assert.False(LiveCards.Record(new FakeCard(clone)));    // the clone's card, in no pile
        Assert.True(LiveCards.Record(InPile(_player, _hand)));
        Assert.True(LiveCards.Record(new FakeCard(_player)));   // a reward card upgraded before it is added
        Assert.True(LiveCards.Record(new FakeCard(null)));
    }
}
