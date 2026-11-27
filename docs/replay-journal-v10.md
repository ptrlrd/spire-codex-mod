# Replay journal: replay_version 10 (co-op)

The replay journal is a JSON-lines file per run in `%APPDATA%\SpireCodex\replays\`, one
object per line, `t` naming the row kind (see `Code/Replay/ReplayHooks.cs`,
`ReplayRecorder.cs` and the row helpers next to them). Version 10 makes a co-op journal
describe **every** player from one player's machine, so a replayer can drive the teammate as
well as the local player. The teammate needs no mod.

STS2 co-op runs in lockstep: each teammate decision reaches this client as a network message
and is applied to this client's copy of the teammate, so the combat and run hooks already fire
for both players. v10 says whose each row is and stops dropping the teammate's rows.

## Compatibility

- Only fields and row kinds are **added**. No existing field changes name or meaning, with one
  co-op-only correction: a row's `decision_id` no longer points at the other player's
  decision (see "Decision links in co-op").
- Every addition is written in co-op only (`player_count` > 1). A single-player journal is
  what v9 wrote, apart from `"replay_version":10` and the rows restored
  under "Also fixed in v10" (rows v9 meant to write but could not on v0.114).
- spire-codex.com rejects a `replay_version` it does not know (400 `bad_header`); the client
  keeps the journal and retries, so the server needs 10 on its allowlist.
- No Steam id or net id is written anywhere. Players are named by slot.

## Player identity

| Field | Meaning |
| --- | --- |
| `mine` | (existing) the row's player is the local player (the machine that wrote the journal). |
| `player_slot` | the row's player's index in `RunState.Players` (`RunState.GetPlayerSlotIndex`). Stable for the run and the same on every machine. Named `player_slot` because `slot` already means a shop or potion slot on `buy`, `buy_start`, `shop` and potion rows. |

A row gets `mine` + `player_slot` wherever its player can be read. Rows that wrote `mine` in v9
keep writing it exactly as before (including `mine:false` when the player could not be read);
`player_slot` is added next to it.

For a field that names a creature (`src`, `dst`, `tgt`, `target`), co-op rows add:

| Field | When |
| --- | --- |
| `<f>_mine`, `<f>_player_slot` | the creature is a player (`src:"player"`, `dst:"player"`, or a character id such as `tgt:"DEFECT"` on power rows). |
| `<f>_owner_mine`, `<f>_owner_slot` | the creature is a player's pet (`Creature.PetOwner`). |

Nothing is added for monsters.

## Header

New field `players` (co-op only), one entry per player in slot order:

```json
"players": [
  {"player_slot": 0, "mine": true,  "character": "DEFECT", "hp": 75, "max_hp": 75, "gold": 99,
   "starting_deck": [{"c": 1, "id": "STRIKE_DEFECT", "up": 0, "added_floor": 1}, ...],
   "starting_relics": ["CRACKED_CORE"], "relics": [{"id": "CRACKED_CORE"}],
   "potions": [{"slot": 0}, {"slot": 1}, {"slot": 2}]},
  {"player_slot": 1, "mine": false, ...}
]
```

`starting_deck`, `relics` and `potions` have the same shape as the header's own fields. The
header's top-level `character`, `starting_deck`, `starting_relics`, `relics`, `potions` stay the
local player's. Card `c` ids are shared across players (one counter per journal), so a
teammate's `c` values in later rows match the ones listed here.

## `resume` and `end`

- `resume` gets `players` (co-op only): one entry per player with `player_slot`, `mine`,
  `character`, `hp`, `max_hp`, `gold`, `deck` (shape of a `deck` row's `cards`), `relics` and
  `potions`: the state the run reloaded into, so a replay can resync the teammate after a
  save-and-quit. The row's own top-level fields stay the local player's.
- `end` gets `final_players` (co-op only): `player_slot`, `mine`, `character`, `hp`, `max_hp`,
  `gold`, `final_deck`, `final_relics` (relic ids). When the run is already gone as the row is
  written (the player left it), it holds only the teammate's last `peer_deck` as `final_deck`,
  with no hp, gold or relics.

## Rows that now carry `mine` + `player_slot`

| Row | Player |
| --- | --- |
| `play` | the card's owner. Also `target_mine`/`target_player_slot` when the target is a player. |
| `draw`, `discard`, `exhaust`, `generate` | the card's owner. |
| `draw_order` | the player whose draw pile it is. **New in co-op:** one `draw_order` row per player at combat start, the local player's first (v9 wrote only the local one). |
| `turn` (side `player`) | the player whose turn started. |
| `potion_start`, `potion_used`, `potion_got`, `potion_dropped` | the potion's owner. Also `target_*` for a player target. |
| `relic` | the player who obtained it. |
| `acquire`, `upgrade`, `downgrade`, `enchant`, `transform`, `remove` | the card's owner. |
| `resolve` | the player who took the reward. |
| `rest` (heal/smith rows outside the rest-site funnel) | the player. |
| `decision` | card reward, deck selection, relic reward and event pages: the player deciding. Rest-site decisions: the local player. Map and treasure decisions are shared and carry none. |
| `outcome` | card reward skip and reroll, deck selection, relic reward skip, event choice: the deciding player. |
| `deck`, `shop` | always the local player (`mine:true`). The teammate's are `peer_deck` and `peer_shop`. |
| `arrow` | the slot's owner (see below). |
| `afflict`, `unafflict`, `play_blocked`, `shuffle`, `flush`, `energy`, `stars`, `forge`, `orb_*`, `gold`, `hp`, `max_hp`, `pick`, `buy_start`, `buy`, `relic_lost`, `relic_counter`, `console`, treasure `outcome` | had `mine` in v9; `player_slot` is added. |

## Creature ownership

| Row | Fields added |
| --- | --- |
| `hit` | `src_*`, `dst_*` |
| `hp_loss` | `dst_*` (the player who lost hp) |
| `hp`, `max_hp` | `dst_*` (alongside the existing `mine`) |
| `block` | `src_*` (the creature whose block changed) |
| `power`, `power_negated` | `src_*`, `tgt_*` |
| `power_lost` | `tgt_*` |
| `attack` | `src_*` |
| `death` | `tgt_*` |

A pet on `combat_start` (`enemies`, `allies`) and on `spawn` rows gets `owner_mine` and
`owner_slot`, so its owner is known from the start, before any `hit` or `power` row names it.

## Decision links in co-op

`decision_id` on `remove`, `buy`, `resolve`, `acquire` (and its `option_index`), `upgrade`,
`downgrade`, `transform`, `relic`, `relic_lost` and `relic_counter` links a row to the
decision open when it happened. In v9 co-op a teammate's row could take the local player's
open decision (a teammate's smith upgrade linked to your rest decision). From v10 a row links
only to a decision of the same player or to a shared one (map, treasure); otherwise
`decision_id` is absent. Single-player journals are unaffected.

## The teammate's events and Crystal Sphere

v9 dropped every event and Crystal Sphere row whose owner was not the local player. v10 writes
them, with `mine:false` and the teammate's `player_slot`:

- `decision` (decision_type `event`) for each page of the teammate's event, with its own
  `decision_id` and the teammate's own `page_index` count.
- `outcome` (decision_type `event`) when the teammate chooses, linked to that decision.

A teammate's event state is kept apart from the local player's, so the local decision ids that
`upgrade`, `relic`, `transform` etc. link to are unchanged.

`sphere_dig`, `sphere_reveal`, `sphere_end` are no longer filtered and carry `mine` +
`player_slot`. The teammate's minigame runs on their own machine, though, so their result
usually arrives only as the `CrystalSphereRewardsMessage` `peer_msg` row below.

## `arrow` (The Fool)

v9 recorded only the local player's arrow slots. v10 records every player's: a `start` row per
player with slots at combat start (the local player's first), and `gain`, `shot`,
`empty_shot`, `impale`, `dislodge`, `retrieve`, `destroy` rows for any owner, each with `mine` +
`player_slot`. `slot` and the other slot fields are indexes into that owner's own slots.

## `peer_deck` (new row)

```json
{"t":"peer_deck","player_slot":1,"mine":false,"cards":[{"c":44,"id":"STRIKE_DEFECT","up":0,...}]}
```

The teammate's whole deck, written whenever it changes (a card added, removed, transformed,
upgraded or enchanted). `cards` has the shape of a `deck` row's. It is a separate kind because
`deck` stays the local player's deck (the save-and-quit remap reads the last `deck` row). The
header's `players[].starting_deck` is the baseline; the first `peer_deck` is the first change.

## `peer_msg` (new row)

One row per teammate decision message this client applies, written as it is handed to the
game's handlers (`RunLocationTargetedMessageBuffer.CallHandlersOfType`), before the rows the
handler causes (the teammate's `buy`, `relic`, `gold`...). Messages are applied in the order
they were received; one that arrives for a room this client has not entered yet is applied on
entering it.

| Field | Meaning |
| --- | --- |
| `msg` | the message class, e.g. `RewardSelectedMessage`. |
| `sender_slot` | the sender's `player_slot`. |
| `mine` | false (the local player's own choices never pass through here). |
| `location` | the message's run location as `act_index:col,row:room_id`; an absent part is empty (`"0:3,0:"`, `"1::"`). |

Per message:

| `msg` | Extra fields |
| --- | --- |
| `RewardSelectedMessage` | `set_id`, `reward_index` |
| `RewardSetSkippedMessage` | `set_id` |
| `RewardObtainedMessage` | `reward_type` (snake_case `RewardType`), `card`, `potion`, `relic` (bare ids), `gold`, `skipped` |
| `MerchantEntryPurchasedMessage` | `entry_id`: `MerchantEntry.Id` in the sender's own inventory, the same `entry_id` as the teammate's `buy_start`/`buy` row that follows |
| `OptionIndexChosenMessage` | `option_type` (`event` or `rest_site`), `option_index` |
| `VotedForSharedEventOptionMessage` | `option_index`, `page_index` |
| `SharedEventOptionChosenMessage` | `option_index`, `page_index` (from the host, on a client) |
| `GoldLostMessage` | `gold_lost` |
| `CrystalSphereRewardsMessage` | `rewards`: `[{type, card_rarity, potion_rarity, big_gold}]` |
| `RestSiteSkippedMessage`, `CardRemovedMessage`, `TreasureChestOpenedMessage` | none |

A message type this list does not know still gets a row with `msg`, `sender_slot` and
`location`. Not written: `RestSiteOptionHoveredMessage` (cosmetic) and the combat action
messages (`ActionEnqueuedMessage`, `RequestEnqueueActionMessage`, `HookActionEnqueuedMessage`,
`RequestEnqueueHookActionMessage`, `ResumeActionAfterPlayerChoiceMessage`,
`RequestResumeActionAfterPlayerChoiceMessage`), whose effects the combat rows already record
for both players (and which can carry the local player's own actions as well).

### Teammate shop buys

A teammate's purchase now reads as `peer_msg` (`MerchantEntryPurchasedMessage`, `entry_id`)
followed by `buy_start` and `buy` with `mine:false`, the teammate's `player_slot`, and (as in
v9) `kind`, `slot` (the entry's index in the teammate's own inventory), `id`, `entry_id`,
`entry_class`, `cost_current` and `gold_on_hand`. Match on `entry_id` (or `kind` + `slot` +
`id`), not on price. The teammate's stock is the `peer_shop` row below.

## `peer_shop` (new row)

The teammate's own merchant inventory (`MerchantSynchronizer.GetInventoryFor`), shaped like
`shop` (`gold` is the teammate's, then `removal_cost`, `removal_stocked`, `cards`, `relics`,
`potions`, `blitz`, `blitz_choices_left`) plus `player_slot` and `mine:false`. Written while
this client is at the merchant, whenever that inventory changes, so `slot` on the teammate's
`buy` rows indexes into it. Like `shop`, it does not list `entry_id`s.

## Also fixed in v10

- Treasure `outcome` rows: v0.114 added a third parameter to
  `TreasureRoomRelicSynchronizer.OnPicked`, so the v9 patch never matched and no treasure pick
  was written. v10 patches the three-argument form.
- `death` rows: v0.114 inserted a `CardModel? source` argument into `Hook.AfterDeath`, before
  `wasRemovalPrevented`, and the v9 patch failed to apply ("invalid program"), so no `death`
  row was written. v10 reads `removal_prevented` from the arguments by type.
- `effect` on `hit` rows (the power or relic behind a hit with no card and no dealer): v0.114
  moved the `CreatureCmd.Damage` funnel to an 8-parameter `IEnumerable<DamageTargetData>`
  overload, so the v9 patch found nothing. v10 patches that overload.
- `selector` on `pick` rows: v0.114 replaced the static `CardSelectCmd.Selector` with one
  selector stack per player (`RunManager.Instance.CardSelectorStack.GetSelector(player)`).
  v10 reads the picking player's stack; `"human"` still means no automated selector.
- `acquire` after a card reward was taken: the taken reward's offers stayed matchable, so a later
  card with the same id (a shop buy floors later, an event card) was written as `source:"reward"`
  with that old `decision_id`. A reward a card resolved is no longer matched again. In co-op the
  other player's still-open card reward keeps its place, so their pick still links to it; before,
  the first pick pushed it out and the second player's card read `source:"granted"`.
- `end.final_players` after save and quit: written from the last in-run state; v10 first
  checked co-op after the game had already left the run and wrote none.

## Not recorded for the teammate

- The combat action messages listed under `peer_msg`: the combat rows already record what
  they do, for both players.
- Whether this client sees the cards a teammate was offered as a card reward, or only the one
  they took (`RewardObtainedMessage`), is still to be checked in game.
