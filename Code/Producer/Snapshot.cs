using System.Collections.Generic;

namespace SpireCodex.Producer;

// The live-state snapshot the mod writes to disk ~10x/second. Serialized snake_case
// (see SnapshotWriter) to match the shape the Spire Codex overlay/desktop app already
// expect. See docs/ARCHITECTURE.md for the full target shape.
public sealed class Snapshot
{
    public int SchemaVersion { get; set; } = 1;
    public long Timestamp { get; set; }
    public string Sts2Version { get; set; } = "unknown";
    public string Status { get; set; } = "no-run"; // ok | no-run | no-state | no-mainloop | error | ...
    public string? Error { get; set; }

    public bool InRun { get; set; }
    public string? Screen { get; set; }
    public string? Seed { get; set; }
    public int Ascension { get; set; }
    public int Act { get; set; } // 1-indexed
    public string? ActName { get; set; } // the act's display / region name, when resolvable
    public int ActFloor { get; set; }
    public int TotalFloor { get; set; }
    public string? GameMode { get; set; }
    // Active run modifiers (daily/custom-run mutators), bare ids; empty on a standard run.
    public List<string> Modifiers { get; set; } = new();
    // Elapsed run time in seconds (RunManager.RunTime); freezes at the win time once won.
    public long RunTime { get; set; }
    public string? MapCoord { get; set; }
    public bool IsGameOver { get; set; }
    public int PlayerCount { get; set; }

    // Whether THIS mod is uploading completed runs (Config.UploadRuns). The
    // companion overlay reads this to avoid double-submitting the same .run:
    // when true it stands down and lets the mod own uploads. Serialized as
    // `uploads_runs`.
    public bool UploadsRuns { get; set; }

    // First player flattened to the top level (single-player focus).
    public string? Character { get; set; } // bare id, e.g. "IRONCLAD"
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public bool IsAlive { get; set; }
    public int Gold { get; set; }
    public int Energy { get; set; }      // current energy (combat only)
    public int MaxEnergy { get; set; }
    public int DeckSize { get; set; }
    public int RelicCount { get; set; }
    public int PotionCount { get; set; }

    // Full live contents for player 0 (ids prefix-stripped, e.g. "STRIKE_IRONCLAD").
    public List<DeckEntry> Deck { get; set; } = new();
    public List<RelicEntry> Relics { get; set; } = new();
    public List<PotionEntry> Potions { get; set; } = new();

    // Present only during a fight (PlayerCombatState != null).
    public CombatSnapshot? Combat { get; set; }

    // Offered card-reward options (bare ids), present only on the card reward screen.
    public List<string> CardReward { get; set; } = new();

    // The full combat rewards/loot screen contents (gold, offered cards, relics, potions),
    // present only while the rewards screen is up. Null otherwise.
    public LootInfo? Loot { get; set; }

    // Current event context (name + prompt + the options on offer), present only while
    // the player is in an event room. Null otherwise.
    public EventInfo? Event { get; set; }

    // Current shop inventory (items + costs), present only in a merchant room. Null otherwise.
    public ShopInfo? Shop { get; set; }

    // The campfire options on offer (Rest/Smith/Dig/...), present only at a rest site. Null
    // otherwise. The "rest" ticker event is the moment of resting; this is the button state.
    public RestInfo? Rest { get; set; }

    // Set once the run ends in death: who killed you + their loss-message quote, for the death
    // screen. Null while alive / on a victory. Captured at the death moment (see DeathCapture).
    public DeathInfo? Death { get; set; }

    // The act's specific upcoming fights/events (from the pre-rolled, seed-determined
    // sequence), for the route preview + per-encounter danger. Null outside a run / no map.
    public ActRoute? Route { get; set; }

    // Per-cleared-floor summary across the whole run (all acts), the data behind the game's map
    // "previous floor" hover: room/enemy, turns, damage/heal, HP+gold snapshot, and the rewards
    // taken vs skipped. Grows one entry per floor cleared; excludes the floor you're standing on.
    // Rides the live feed so the overlay can show previous-floor cards during a run.
    public List<FloorSummary> FloorHistory { get; set; } = new();

    public List<PlayerState> Players { get; set; } = new();
}

// One specific encounter or event in the act sequence. Id is the bare id (e.g.
// "CORPSE_SLUGS_WEAK", "ABYSSAL_BATHS"); Name is a readable label; RoomType is
// monster/elite/boss/event; IsWeak flags the act's first weak fights.
public sealed record EncounterRef(string Id, string Name, string RoomType, bool IsWeak);

// The act's pre-rolled sequence as seen from the current position: the UPCOMING monsters,
// elites, and events in consumption order, plus the specific boss and ancient. The Kth
// upcoming monster node a path hits is Monsters[K] (encounters are consumed in visit order,
// not bound to a specific node).
public sealed class ActRoute
{
    public List<EncounterRef> Monsters { get; set; } = new();
    public List<EncounterRef> Elites { get; set; } = new();
    public List<EncounterRef> Events { get; set; } = new();
    public EncounterRef? Boss { get; set; }
    public EncounterRef? Ancient { get; set; }
}

// One choice on the current event page. Key = stable loc key, Text = the resolved localized button
// label. Desc = the resolved option description, which carries interpolated consequence numbers
// (e.g. Slippery Bridge's "Lose 3 HP"). Card = a card the option references (the one it will make
// you lose/gain, from the option's card hover tip). Relic = a relic it grants. Locked = greyed/
// unavailable, Proceed = the leave/continue option, Chosen = already picked this run-through.
public sealed record EventOptionInfo(
    string Key, string Text, string? Desc, string? Card, string? Relic, bool Locked, bool Proceed, bool Chosen);

public sealed record EventInfo(string Id, string? Title, string? Prompt, List<EventOptionInfo> Options);

// One campfire option: Id the stable option id ("HEAL", "SMITH", "DIG", ...), Title the resolved
// localized label, Enabled whether it's currently selectable.
public sealed record RestOptionInfo(string Id, string? Title, bool Enabled);

public sealed record RestInfo(List<RestOptionInfo> Options);

// Run-death detail for the death screen: By = the killer's name (the encounter's title), Line =
// the killer's loss-message quote. Either may be null (e.g. an event death, no quote).
public sealed record DeathInfo(string? By, string? Line);

// One shop slot: Id the bare entity id (null when sold/out of stock), Cost the current
// gold price, OnSale a card's 50%-off flag (always false for relics/potions), Stocked
// false once bought. Slot tags a card as "character" or "colorless".
public sealed record ShopItemInfo(string? Id, int Cost, bool Stocked, bool OnSale, string? Slot);

public sealed record ShopRemovalInfo(int Cost, bool Stocked);

public sealed record ShopInfo(
    List<ShopItemInfo> Cards, List<ShopItemInfo> Relics, List<ShopItemInfo> Potions, ShopRemovalInfo? Removal);

// The combat rewards/loot screen: the gold on offer, the card choices, and any relic/potion
// rewards (bare ids). CardRemoval flags a card-removal reward (e.g. from certain encounters).
public sealed class LootInfo
{
    public int? Gold { get; set; }
    public List<string> Cards { get; set; } = new();
    public List<string> Relics { get; set; } = new();
    public List<string> Potions { get; set; } = new();
    public bool CardRemoval { get; set; }

    // ScrollBoxes / "Choose a Bundle": the offered cards grouped into packs (each an ordered list of
    // bare ids), from NChooseABundleSelectionScreen._bundles. Present only on a bundle screen; null
    // otherwise. When set, these cards are NOT also in Cards, so consumers render the packs instead
    // of the flat card list.
    public List<List<string>>? Packs { get; set; }
}

// One cleared floor, mirroring the game's NMapPointHistoryHoverTip. Floor is the global run
// floor number (1-based, cumulative across acts); Act is 1-indexed; Type the room kind
// (monster/elite/boss/shop/restsite/treasure/event/ancient); EncounterId the bare enemy/event
// id (null for shop/rest/treasure). Hp/MaxHp/Gold are the player's totals as of that floor.
// Turns/DamageTaken/Healed/GoldSpent/GoldGained are null when zero/not applicable. Rewards =
// what was taken, Skipped = what was offered and left behind (cards/relics/potions).
public sealed class FloorSummary
{
    public int Floor { get; set; }
    public int Act { get; set; }
    public string Type { get; set; } = "";
    public string? EncounterId { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Gold { get; set; }
    public int? Turns { get; set; }
    public int? DamageTaken { get; set; }
    public int? Healed { get; set; }
    public int? GoldSpent { get; set; }
    public int? GoldGained { get; set; }
    public List<FloorReward> Rewards { get; set; } = new();
    public List<FloorReward> Skipped { get; set; } = new();
}

// One reward slot on a floor: Kind is "card"/"relic"/"potion"; Id the bare entity id.
public sealed record FloorReward(string Kind, string Id);

public sealed class CombatSnapshot
{
    public int Energy { get; set; }
    public int? Turn { get; set; } // CombatState.RoundNumber
    public string? TurnSide { get; set; } // "player" / "enemy" (CombatState.CurrentSide), whose turn
    public List<EnemySnapshot> Enemies { get; set; } = new();

    // The cards in the player's hand this turn (combat only), in hand order. Serialized as `hand`.
    public List<DeckEntry> Hand { get; set; } = new();

    // Full pile contents this turn (combat only), for spectator hover. The draw pile is
    // unordered in-game, so this is the set of cards in it, not the draw order. Serialized as
    // draw_pile / discard_pile / exhaust_pile. The counts below are derived from these.
    public List<DeckEntry> DrawPile { get; set; } = new();
    public List<DeckEntry> DiscardPile { get; set; } = new();
    public List<DeckEntry> ExhaustPile { get; set; } = new();

    // Pile sizes this turn (combat only): draw_count, discard_count, exhaust_count.
    public int DrawCount { get; set; }
    public int DiscardCount { get; set; }
    public int ExhaustCount { get; set; }

    // The local player's active powers/buffs/debuffs this combat (id + stack amount), the
    // player-side mirror of EnemySnapshot.Powers. Empty when the player has none.
    public List<PowerEntry> PlayerPowers { get; set; } = new();

    // The local player's channeled orbs this combat (Regent), in slot order. OrbSlots is the
    // current slot capacity (for drawing empty slots). Empty for non-orb characters.
    public List<OrbEntry> Orbs { get; set; } = new();
    public int OrbSlots { get; set; }

    // Live damage counters for this fight, from DamageTracker (off the game's damage hooks).
    // Dealt/Taken are unblocked HP damage (to enemies / to the player); BiggestHit is the
    // largest single unblocked hit we dealt this fight. Serialized as damage_dealt,
    // damage_dealt_this_turn, damage_taken, biggest_hit.
    public int DamageDealt { get; set; }
    public int DamageDealtThisTurn { get; set; }
    public int DamageTaken { get; set; }
    public int BiggestHit { get; set; }

    // Friendly summoned creatures on the allies side this fight (the Necrobinder's Osty and any
    // future pet), read from CombatState.Allies where IsPet. Owner is the index into Snapshot.Players
    // of the player who summoned it (0 in single-player). Empty when nobody has a pet out.
    public List<PetEntry> Pets { get; set; } = new();
}

// A friendly summoned creature (pet), e.g. the Necrobinder's Osty. Id is the bare model id
// ("OSTY"), Name the display name, plus its HP/block and which player owns it.
public sealed class PetEntry
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public bool IsAlive { get; set; }
    public int Owner { get; set; } // index into Snapshot.Players of the owning player
}

public sealed class EnemySnapshot
{
    public string Id { get; set; } = ""; // bare, e.g. "FUZZY_WURM_CRAWLER"
    public string? Name { get; set; }
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public bool IsAlive { get; set; }
    public bool IntendsToAttack { get; set; } // Monster.IntendsToAttack
    public List<PowerEntry> Powers { get; set; } = new(); // id + stack amount (buffs/debuffs)

    // What this enemy intends to do next turn (Monster.NextMove.Intents), so spectators see
    // the incoming move. Usually one intent; some moves are compound (e.g. attack + buff).
    public List<IntentInfo> Intents { get; set; } = new();
}

// One intent on an enemy's upcoming move. Type is the lowercased IntentType (attack,
// defend, buff, debuff, heal, escape, summon, sleep, stun, deathblow, unknown...).
// Damage/Hits are set only for attacks: Damage is the displayed per-hit damage, Hits the
// number of strikes (so "16 x2" = 32 incoming).
public sealed record IntentInfo(string Type, int? Damage, int? Hits);

// One power/buff/debuff on a creature: bare id + current stack amount (PowerModel.Amount).
public sealed record PowerEntry(string Id, int Amount);

// One channeled orb: bare id (LIGHTNING/FROST/DARK/...), Passive = its per-turn value, Evoke = its
// value when evoked (both from OrbModel; carry the orb's current accumulated value where it applies).
public sealed record OrbEntry(string Id, int Passive, int Evoke);

public sealed class PlayerState
{
    public string? Character { get; set; }
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public bool IsAlive { get; set; }
    public int Gold { get; set; }
    public int Energy { get; set; }      // current energy this turn (combat only; 0 otherwise)
    public int MaxEnergy { get; set; }
    public int DeckSize { get; set; }
    public int RelicCount { get; set; }
    public int PotionCount { get; set; }
    public bool IsLocal { get; set; } // the local "you" player in co-op; true for the only player solo

    // Co-op turn indicator: true once this player has hit end-turn this round and the next player
    // turn hasn't begun (CombatManager.IsPlayerReadyToEndTurn). Combat only; false otherwise. With
    // the top-level turn_side (player/enemy), lets a spectator see who's still playing vs waiting.
    public bool EndedTurn { get; set; }
}

public sealed class DeckEntry
{
    public string Id { get; set; } = "";
    public bool Upgraded { get; set; }
    public string? Enchantment { get; set; }
    public int? FloorAddedToDeck { get; set; }
}

public sealed class RelicEntry
{
    public string Id { get; set; } = "";
    public int? FloorAddedToDeck { get; set; }
}

public sealed class PotionEntry
{
    public string Id { get; set; } = "";
}
