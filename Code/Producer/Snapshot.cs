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

    // Current event context (name + prompt + the options on offer), present only while
    // the player is in an event room. Null otherwise.
    public EventInfo? Event { get; set; }

    // Current shop inventory (items + costs), present only in a merchant room. Null otherwise.
    public ShopInfo? Shop { get; set; }

    // The act's specific upcoming fights/events (from the pre-rolled, seed-determined
    // sequence), for the route preview + per-encounter danger. Null outside a run / no map.
    public ActRoute? Route { get; set; }

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

// One choice on the current event page: Key is the stable loc key, Text the resolved
// localized button label the player sees, Locked = greyed out / unavailable, Proceed = the
// leave/continue option, Chosen = already picked this run-through.
public sealed record EventOptionInfo(string Key, string Text, bool Locked, bool Proceed, bool Chosen);

public sealed record EventInfo(string Id, string? Title, string? Prompt, List<EventOptionInfo> Options);

// One shop slot: Id the bare entity id (null when sold/out of stock), Cost the current
// gold price, OnSale a card's 50%-off flag (always false for relics/potions), Stocked
// false once bought. Slot tags a card as "character" or "colorless".
public sealed record ShopItemInfo(string? Id, int Cost, bool Stocked, bool OnSale, string? Slot);

public sealed record ShopRemovalInfo(int Cost, bool Stocked);

public sealed record ShopInfo(
    List<ShopItemInfo> Cards, List<ShopItemInfo> Relics, List<ShopItemInfo> Potions, ShopRemovalInfo? Removal);

public sealed class CombatSnapshot
{
    public int Energy { get; set; }
    public int? Turn { get; set; } // CombatState.RoundNumber
    public List<EnemySnapshot> Enemies { get; set; } = new();

    // Live damage counters for this fight, from DamageTracker (off the game's damage hooks).
    // Dealt/Taken are unblocked HP damage (to enemies / to the player); BiggestHit is the
    // largest single unblocked hit we dealt this fight. Serialized as damage_dealt,
    // damage_dealt_this_turn, damage_taken, biggest_hit.
    public int DamageDealt { get; set; }
    public int DamageDealtThisTurn { get; set; }
    public int DamageTaken { get; set; }
    public int BiggestHit { get; set; }
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
    public List<string> Powers { get; set; } = new(); // bare power ids

    // What this enemy intends to do next turn (Monster.NextMove.Intents), so spectators see
    // the incoming move. Usually one intent; some moves are compound (e.g. attack + buff).
    public List<IntentInfo> Intents { get; set; } = new();
}

// One intent on an enemy's upcoming move. Type is the lowercased IntentType (attack,
// defend, buff, debuff, heal, escape, summon, sleep, stun, deathblow, unknown...).
// Damage/Hits are set only for attacks: Damage is the displayed per-hit damage, Hits the
// number of strikes (so "16 x2" = 32 incoming).
public sealed record IntentInfo(string Type, int? Damage, int? Hits);

public sealed class PlayerState
{
    public string? Character { get; set; }
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public bool IsAlive { get; set; }
    public int Gold { get; set; }
    public int MaxEnergy { get; set; }
    public int DeckSize { get; set; }
    public int RelicCount { get; set; }
    public int PotionCount { get; set; }
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
