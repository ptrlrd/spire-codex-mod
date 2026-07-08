using System;
using System.Collections;
using Godot;
using SpireCodex.Producer;

namespace SpireCodex.Core;

// The single place that touches Slay the Spire 2 game types. Everything here is
// reflection over the live scene tree, using the access path the spire-compendium
// payload already proved against the live game:
//
//   Engine.GetMainLoop() (SceneTree)
//     -> Root -> child "Game" (NGame)
//     -> CurrentRunNode (NRun)
//     -> _state (RunState)
//
// Member names are accurate as of the last check, but the game is Early Access and
// will rename things. When a name is wrong the read returns null and the snapshot
// status downgrades instead of crashing. After a game patch, re-confirm names
// against the decompiled sts2.dll (see docs/GAME-STATE.md) and fix them HERE ONLY.
public static class Sts2Access
{
    public static Snapshot ReadSnapshot()
    {
        var snap = new Snapshot
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sts2Version = Sts2Version.Current,
            Status = "no-run",
            InRun = false,
        };

        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree) { snap.Status = "no-mainloop"; return snap; }
            var root = tree.Root;
            if (root == null) { snap.Status = "no-root"; return snap; }

            var game = FindChild(root, "Game");
            if (game == null) { snap.Status = "no-game-node"; return snap; }

            var run = Reflect.GetMember(game, "CurrentRunNode");
            if (run == null) { snap.Status = "no-run"; return snap; }

            var state = Reflect.GetMember(run, "_state");
            if (state == null) { snap.Status = "no-state"; return snap; }

            snap.Screen = DetectScreen(run);
            ReadRunState(state, snap);
            snap.Loot = ReadRewardsScreen(game);
            snap.CardReward = snap.Loot?.Cards ?? new();
            if (snap.Screen == "treasure") MergeTreasureRelics(state, snap); // chest relics aren't on the rewards screen
            MergeCardSelect(game, snap); // choose-a-card / Share-Knowledge grids live on a separate screen
            MapExport.Update(state, snap); // act map for the live spectator view (throttled)
            snap.FloorHistory = MapExport.ReadHistory(state, snap); // per-cleared-floor summaries
            (snap.Event, snap.Shop, snap.Rest) = RoomExport.Read(state); // live event / shop / rest detail
            snap.Route = EncounterRoute.Read(state); // the act's specific upcoming fights/events
            snap.InRun = !snap.IsGameOver;
            snap.Status = "ok";
        }
        catch (Exception e)
        {
            snap.Status = "error";
            snap.Error = e.Message;
        }

        return snap;
    }

    // Set by the overlay (F11) to dump the live room/reward structures for development.
    public static bool DumpRoomRequested;

    // Find up to 6 scene-tree nodes whose type/name looks like a reward or card-select screen.
    private static void CollectScreenNodes(Node node, List<Node> acc)
    {
        if (acc.Count >= 6) return;
        foreach (var child in node.GetChildren())
        {
            if (acc.Count >= 6) return;
            var s = (child.GetType().Name + " " + child.Name).ToLowerInvariant();
            if (s.Contains("reward") || s.Contains("loot") || s.Contains("cardselect")
                || s.Contains("cardchoice") || s.Contains("cardchoose") || s.Contains("choosecard"))
            {
                acc.Add(child);
            }
            CollectScreenNodes(child, acc);
        }
    }

    // Find up to 6 descendant nodes whose type/name contains a keyword.
    private static void CollectByKeyword(Node node, List<Node> acc, string keyword)
    {
        if (acc.Count >= 6) return;
        foreach (var child in node.GetChildren())
        {
            if (acc.Count >= 6) return;
            var s = (child.GetType().Name + " " + child.Name).ToLowerInvariant();
            if (s.Contains(keyword)) acc.Add(child);
            CollectByKeyword(child, acc, keyword);
        }
    }

    private static void ReadRunState(object state, Snapshot snap)
    {
        if (DumpRoomRequested)
        {
            DumpRoomRequested = false;
            var room = Reflect.GetMember(state, "CurrentRoom");
            var items = new List<(string, object?)>
            {
                ("RunState", state),
                ("CurrentRoom", room),
            };
            // Expand ExtraRewards (Dictionary<Player, List<Reward>>) to find the card reward.
            if (Reflect.GetMember(room, "ExtraRewards") is IEnumerable dict)
            {
                var ri = 0;
                foreach (var kv in dict)
                {
                    if (Reflect.GetMember(kv, "Value") is IEnumerable rewards)
                    {
                        foreach (var rw in rewards)
                        {
                            if (rw != null) items.Add(($"Reward[{ri++}] ({rw.GetType().Name})", rw));
                            if (ri >= 8) break;
                        }
                    }
                }
            }

            // The card-choice screen is a UI node in the scene tree, not in the run model.
            // Scan for reward/card-select screen nodes and dump them.
            if (Engine.GetMainLoop() is SceneTree tree && tree.Root != null)
            {
                var matches = new List<Node>();
                CollectScreenNodes(tree.Root, matches);
                foreach (var n in matches)
                {
                    items.Add(($"Node {n.GetType().Name} '{n.Name}'", n));
                    // Reward buttons carry a Reward (GoldReward/CardReward/...) - dump it too.
                    if (Reflect.GetMember(n, "Reward") is not { } rw) continue;
                    items.Add(($"  ^Reward ({rw.GetType().Name})", rw));

                    if (rw.GetType().Name != "CardReward") continue;
                    // The 3-card selection screen and its per-card nodes.
                    if (Reflect.GetMember(rw, "_currentlyShownScreen") is Node shown)
                    {
                        items.Add(($"  ^^ShownScreen ({shown.GetType().Name})", shown));
                        var cardNodes = new List<Node>();
                        CollectByKeyword(shown, cardNodes, "card");
                        foreach (var cn in cardNodes)
                            items.Add(($"    CardNode {cn.GetType().Name} '{cn.Name}'", cn));
                    }
                }
            }

            Introspect.DumpToFile("spire-codex-dump.txt", items.ToArray());
        }

        snap.Ascension = Reflect.GetInt(state, "AscensionLevel");
        snap.Act = Reflect.GetInt(state, "CurrentActIndex") + 1; // 0-indexed in game
        snap.ActName = EncounterRoute.ReadActName(state);
        snap.ActFloor = Reflect.GetInt(state, "ActFloor");
        snap.TotalFloor = Reflect.GetInt(state, "TotalFloor");
        snap.IsGameOver = Reflect.GetBool(state, "IsGameOver");
        snap.GameMode = Reflect.GetString(state, "GameMode");
        snap.Modifiers = ReadBareIds(Reflect.GetMember(state, "Modifiers")); // daily/custom run mutators
        snap.RunTime = ReadRunTimeSeconds(state); // elapsed seconds, from the RunManager singleton
        snap.MapCoord = Reflect.GetMember(state, "CurrentMapCoord")?.ToString();
        snap.Seed = Reflect.GetMember(Reflect.GetMember(state, "Rng"), "Seed")?.ToString();
        DamageTracker.NoteRun(snap.Seed); // resets the run's damage aggregate on a new seed
        DeathCapture.NoteRun(snap.Seed);  // clears any prior run's captured death on a new seed
        snap.Death = DeathCapture.Latest; // set once the player dies (RunEvents.DeathPrefix captures it)

        // Flatten the LOCAL player to the top level (vitals/hand/deck/combat), not just player 0,
        // so a co-op spectator sees your own run. In single-player the one player is local.
        object? firstPlayer = null;
        object? localPlayer = null;
        var localIndex = 0;
        if (Reflect.GetMember(state, "Players") is IEnumerable players)
        {
            foreach (var player in players)
            {
                if (player == null) continue;
                var idx = snap.Players.Count;
                firstPlayer ??= player;
                snap.Players.Add(ReadPlayer(player));
                if (localPlayer == null && LocalPlayer.IsLocalPlayer(player))
                {
                    localPlayer = player;
                    localIndex = idx;
                }
            }
        }
        localPlayer ??= firstPlayer; // fallback if the local net id wasn't matched (e.g. pre-lobby)

        snap.PlayerCount = snap.Players.Count;
        if (localPlayer != null && localIndex < snap.Players.Count)
        {
            var p = snap.Players[localIndex];
            p.IsLocal = true; // tag the local player so the co-op live view can mark "you"
            snap.Character = p.Character;
            snap.CurrentHp = p.CurrentHp;
            snap.MaxHp = p.MaxHp;
            snap.Block = p.Block;
            snap.IsAlive = p.IsAlive;
            snap.Gold = p.Gold;
            snap.MaxEnergy = p.MaxEnergy;
            snap.DeckSize = p.DeckSize;
            snap.RelicCount = p.RelicCount;
            snap.PotionCount = p.PotionCount;

            // Dev-only: dump live type surfaces to learn member names after a patch.
            if (Config.DumpIntrospection) Introspect.DumpOnce(state, localPlayer);

            // Full live contents for the local player.
            snap.Deck = ReadDeck(localPlayer);
            snap.Relics = ReadRelics(localPlayer);
            snap.Potions = ReadPotions(localPlayer);

            // Combat state (present only during a fight).
            snap.Combat = ReadCombat(state, localPlayer);
            snap.Energy = snap.Combat?.Energy ?? 0;
        }
    }

    private static CombatSnapshot? ReadCombat(object state, object player)
    {
        var pcs = Reflect.GetMember(player, "PlayerCombatState");
        if (pcs == null) return null; // not in a fight

        var combat = new CombatSnapshot { Energy = Reflect.GetInt(pcs, "Energy") };

        var room = Reflect.GetMember(state, "CurrentRoom");
        var cs = Reflect.GetMember(room, "CombatState");
        combat.Turn = GetNullableInt(cs, "RoundNumber");
        combat.TurnSide = Reflect.GetString(cs, "CurrentSide")?.ToLowerInvariant(); // player / enemy

        if (Reflect.GetMember(room, "Enemies") is IEnumerable enemies)
        {
            foreach (var e in enemies)
            {
                if (e == null) continue;
                var monster = Reflect.GetMember(e, "Monster");
                combat.Enemies.Add(new EnemySnapshot
                {
                    Id = BareId(Reflect.GetString(e, "ModelId")) ?? "",
                    Name = Reflect.GetString(e, "Name"),
                    CurrentHp = Reflect.GetInt(e, "CurrentHp"),
                    MaxHp = Reflect.GetInt(e, "MaxHp"),
                    Block = Reflect.GetInt(e, "Block"),
                    IsAlive = Reflect.GetBool(e, "IsAlive", true),
                    IntendsToAttack = Reflect.GetBool(monster, "IntendsToAttack"),
                    Powers = ReadPowers(e),
                    Intents = ReadIntents(monster),
                });
            }
        }

        // Friendly summoned creatures (the Necrobinder's Osty and any future pet). CombatState.Allies
        // holds every allies-side creature; the players are IsPlayer, the pets are IsPet. Read from
        // the shared combat state so co-op partners' pets show too, tagged with their owner.
        if (Reflect.GetMember(cs, "Allies") is IEnumerable allies)
        {
            List<object>? playerObjs = null;
            foreach (var ally in allies)
            {
                if (ally == null || !Reflect.GetBool(ally, "IsPet")) continue;
                playerObjs ??= EnumeratePlayers(state);
                combat.Pets.Add(new PetEntry
                {
                    Id = BareId(Reflect.GetString(ally, "ModelId")) ?? "",
                    Name = Reflect.GetString(ally, "Name"),
                    CurrentHp = Reflect.GetInt(ally, "CurrentHp"),
                    MaxHp = Reflect.GetInt(ally, "MaxHp"),
                    Block = Reflect.GetInt(ally, "Block"),
                    IsAlive = Reflect.GetBool(ally, "IsAlive", true),
                    Owner = OwnerIndex(playerObjs, Reflect.GetMember(ally, "PetOwner")),
                });
            }
        }

        combat.Hand = ReadCardList(Reflect.GetMember(pcs, "Hand")); // cards in hand this turn
        combat.DrawPile = ReadCardList(Reflect.GetMember(pcs, "DrawPile"));
        combat.DiscardPile = ReadCardList(Reflect.GetMember(pcs, "DiscardPile"));
        combat.ExhaustPile = ReadCardList(Reflect.GetMember(pcs, "ExhaustPile"));
        combat.DrawCount = combat.DrawPile.Count; // counts derived from the lists above
        combat.DiscardCount = combat.DiscardPile.Count;
        combat.ExhaustCount = combat.ExhaustPile.Count;
        combat.PlayerPowers = ReadPowers(Reflect.GetMember(player, "Creature")); // your own buffs/debuffs
        var orbQueue = Reflect.GetMember(pcs, "OrbQueue");
        combat.Orbs = ReadOrbs(orbQueue);                          // channeled orbs (Regent)
        combat.OrbSlots = Reflect.GetInt(orbQueue, "Capacity");    // current orb slot count
        DamageTracker.FillCombat(combat); // live damage counters for this fight
        return combat;
    }

    // The enemy's upcoming move as a list of intents, from MonsterModel.NextMove.Intents
    // (the same source the game's own intent tip reads). For attack intents, the base
    // per-hit damage and the hit count, so spectators see "16 x2 incoming".
    private static List<IntentInfo> ReadIntents(object? monster)
    {
        var list = new List<IntentInfo>();
        var move = Reflect.GetMember(monster, "NextMove");
        if (Reflect.GetMember(move, "Intents") is not IEnumerable intents) return list;

        foreach (var intent in intents)
        {
            if (intent == null) continue;
            var type = Reflect.GetString(intent, "IntentType")?.ToLowerInvariant() ?? "unknown";

            int? damage = null, hits = null;
            // AttackIntent.DamageCalc (a Func<decimal>) gives the base per-hit damage and is
            // null on non-attacks. We deliberately do NOT use AttackIntent.GetSingleDamage:
            // it fires Hook.ModifyDamage, and this runs on every ~10Hz producer tick, so we
            // keep the read fully side-effect-free. Cost: in-combat modifiers (e.g. strength)
            // aren't reflected, so this is the base incoming damage.
            if (InvokeDamageCalc(intent) is { } dmg)
            {
                try { damage = (int)System.Convert.ToDecimal(dmg); } catch { }
                var r = Reflect.GetInt(intent, "Repeats", 0);
                hits = r <= 0 ? 1 : r;
            }

            list.Add(new IntentInfo(type, damage, hits));
            if (list.Count >= 6) break; // compound moves are small; cap defensively
        }
        return list;
    }

    // AttackIntent.DamageCalc is a Func<decimal>; invoke it for the base per-hit damage.
    // Null (no delegate) on non-attack intents, which leaves damage/hits unset.
    private static object? InvokeDamageCalc(object intent)
    {
        try { return Reflect.GetMember(intent, "DamageCalc") is System.Delegate d ? d.DynamicInvoke() : null; }
        catch { return null; }
    }

    // The full power list on a creature (id + stack amount), used for both the local player's
    // buffs/debuffs and each enemy's, so a viewer sees "Strength 3", not just "Strength".
    private static List<PowerEntry> ReadPowers(object? creature)
    {
        var list = new List<PowerEntry>();
        if (Reflect.GetMember(creature, "Powers") is IEnumerable powers)
        {
            foreach (var p in powers)
            {
                if (p == null) continue;
                var id = BareId(Reflect.GetString(p, "Id"));
                if (!string.IsNullOrEmpty(id)) list.Add(new PowerEntry(id!, Reflect.GetInt(p, "Amount")));
            }
        }
        return list;
    }

    // The player's channeled orbs (Regent), in slot order: id + passive (per-turn) / evoke values.
    private static List<OrbEntry> ReadOrbs(object? orbQueue)
    {
        var list = new List<OrbEntry>();
        if (Reflect.GetMember(orbQueue, "Orbs") is IEnumerable orbs)
        {
            foreach (var o in orbs)
            {
                if (o == null) continue;
                var id = BareId(Reflect.GetString(o, "Id"));
                if (!string.IsNullOrEmpty(id))
                    list.Add(new OrbEntry(id!, Reflect.GetInt(o, "PassiveVal"), Reflect.GetInt(o, "EvokeVal")));
            }
        }
        return list;
    }

    // Read a collection of game models (run modifiers, etc.) into a list of bare ids.
    private static List<string> ReadBareIds(object? container)
    {
        var list = new List<string>();
        if (container is IEnumerable items)
        {
            foreach (var item in items)
            {
                if (item == null) continue;
                var id = BareId(Reflect.GetString(item, "Id"));
                if (!string.IsNullOrEmpty(id)) list.Add(id!);
            }
        }
        return list;
    }

    // Elapsed run time in seconds, from the RunManager singleton (RunManager.Instance.RunTime;
    // freezes at the win time once the run is won). Read via reflection off RunState's own
    // assembly so the mod keeps no hard type reference; 0 if the singleton can't be resolved.
    private static long ReadRunTimeSeconds(object state)
    {
        try
        {
            var rm = Reflect.GetStatic(
                state.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            var v = Reflect.GetMember(rm, "RunTime");
            return v == null ? 0 : Convert.ToInt64(v);
        }
        catch { return 0; }
    }

    private static PlayerState ReadPlayer(object player)
    {
        var ps = new PlayerState
        {
            Character = BareId(Reflect.GetString(player, "Character")),
            Gold = Reflect.GetInt(player, "Gold"),
            MaxEnergy = Reflect.GetInt(player, "MaxEnergy"),
        };

        // HP / block live on the player's Creature, not the player directly.
        var creature = Reflect.GetMember(player, "Creature");
        ps.CurrentHp = Reflect.GetInt(creature, "CurrentHp");
        ps.MaxHp = Reflect.GetInt(creature, "MaxHp");
        ps.Block = Reflect.GetInt(creature, "Block");
        ps.IsAlive = Reflect.GetBool(creature, "IsAlive", true);

        ps.DeckSize = CountAny(Reflect.GetMember(player, "Deck"));
        ps.RelicCount = CountAny(Reflect.GetMember(player, "Relics"));
        ps.PotionCount = CountAny(Reflect.GetMember(player, "Potions"));

        // Current energy + co-op turn state (both meaningful only in combat). Energy is 0 and
        // EndedTurn false outside a fight (no PlayerCombatState / cleared end-turn set).
        ps.Energy = Reflect.GetInt(Reflect.GetMember(player, "PlayerCombatState"), "Energy");
        ps.EndedTurn = PlayerEndedTurn(player);

        return ps;
    }

    // Whether this player has hit end-turn this round (CombatManager.IsPlayerReadyToEndTurn), the
    // per-player half of the turn indicator in co-op. False out of combat / on any read failure.
    private static Type? _combatMgrType;
    private static bool _combatMgrResolved;
    private static bool PlayerEndedTurn(object player)
    {
        try
        {
            if (!_combatMgrResolved)
            {
                _combatMgrResolved = true;
                _combatMgrType = player.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Combat.CombatManager");
            }
            var mgr = Reflect.GetStatic(_combatMgrType, "Instance");
            return Reflect.CallWith(mgr, "IsPlayerReadyToEndTurn", player) is true;
        }
        catch { return false; }
    }

    // The run's Player objects as a list, in the same order Snapshot.Players is built, so a pet's
    // owner resolves to the matching token index.
    private static List<object> EnumeratePlayers(object state)
    {
        var list = new List<object>();
        foreach (var p in Enumerate(Reflect.GetMember(state, "Players")))
            if (p != null) list.Add(p);
        return list;
    }

    // Index of a pet's owner among the run's players (matched by reference), or 0 as a safe default
    // (single-player, or an owner we couldn't line up).
    private static int OwnerIndex(List<object> players, object? owner)
    {
        if (owner == null) return 0;
        for (var i = 0; i < players.Count; i++)
            if (ReferenceEquals(players[i], owner)) return i;
        return 0;
    }

    // The combat rewards/loot screen (NRewardsScreen): its reward buttons each carry a Reward.
    // We read every reward type into a LootInfo: GoldReward.Amount, CardReward.Cards,
    // RelicReward.Relic, PotionReward.Potion, plus a card-removal flag. Null when no rewards
    // screen is visible. snap.CardReward is derived from Loot.Cards for the overlay/plates.
    private static LootInfo? ReadRewardsScreen(Node game)
    {
        try
        {
            var screen = game.GetNodeOrNull(
                "RootSceneContainer/Run/GlobalUi/OverlayScreensContainer/RewardsScreen");
            if (screen == null) return null;
            if (screen is CanvasItem ci && !ci.IsVisibleInTree()) return null;
            if (Reflect.GetMember(screen, "_rewardButtons") is not IEnumerable buttons) return null;

            var loot = new LootInfo();
            var any = false;
            foreach (var btn in buttons)
            {
                var reward = Reflect.GetMember(btn, "Reward");
                if (reward == null) continue;
                any = true;
                switch (reward.GetType().Name)
                {
                    case "GoldReward":
                        var gold = Reflect.GetInt(reward, "Amount", -1);
                        if (gold >= 0) loot.Gold = (loot.Gold ?? 0) + gold;
                        break;
                    case "CardReward":
                        if (Reflect.GetMember(reward, "Cards") is IEnumerable cards)
                            foreach (var card in cards)
                            {
                                var id = BareId(Reflect.GetString(card, "Id"));
                                if (!string.IsNullOrEmpty(id)) loot.Cards.Add(id!);
                            }
                        break;
                    case "RelicReward":
                        var relic = BareId(Reflect.GetString(Reflect.GetMember(reward, "Relic"), "Id"));
                        if (!string.IsNullOrEmpty(relic)) loot.Relics.Add(relic!);
                        break;
                    case "PotionReward":
                        var potion = BareId(Reflect.GetString(Reflect.GetMember(reward, "Potion"), "Id"));
                        if (!string.IsNullOrEmpty(potion)) loot.Potions.Add(potion!);
                        break;
                    case "CardRemovalReward":
                        loot.CardRemoval = true;
                        break;
                }
            }
            return any ? loot : null;
        }
        catch
        {
            return null; // tolerate any tree/shape change
        }
    }

    // Treasure chests deliver their relics on a separate screen (NTreasureRoomRelicCollection),
    // not the rewards screen ReadRewardsScreen reads, so the offered relics live on the
    // RunManager singleton's TreasureRoomRelicSynchronizer.CurrentRelics. Merge them into the
    // loot context (creating it if the rewards screen had nothing) so the spectator gets a
    // chest panel alongside screen=="treasure". Empty/unopened chest -> no-op.
    private static void MergeTreasureRelics(object state, Snapshot snap)
    {
        try
        {
            var rm = Reflect.GetStatic(
                state.GetType().Assembly.GetType("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            var sync = Reflect.GetMember(rm, "TreasureRoomRelicSynchronizer");
            if (Reflect.GetMember(sync, "CurrentRelics") is not IEnumerable relics) return;

            var ids = new List<string>();
            foreach (var r in relics)
            {
                var id = BareId(Reflect.GetString(r, "Id"));
                if (!string.IsNullOrEmpty(id)) ids.Add(id!);
            }
            if (ids.Count == 0) return;

            snap.Loot ??= new LootInfo();
            snap.Loot.Relics.AddRange(ids);
        }
        catch { /* best-effort; the treasure screen flag still tells the viewer it's a chest */ }
    }

    // Choose-a-card reward grids (Brain Leech's "Share Knowledge" and other choose-a-card events)
    // live on a separate card-select screen, not the rewards screen ReadRewardsScreen reads. When a
    // reward selector is up, surface its offered cards as the card reward so the spectator sees the
    // options. Reward/choose selectors only, not deck-op selectors (upgrade/transform/your-deck),
    // whose cards are the player's own, not rewards. The cards are rolled when the option is chosen,
    // so they only exist once the grid is up (nothing to show before the choice).
    private static void MergeCardSelect(Node game, Snapshot snap)
    {
        try
        {
            var container = game.GetNodeOrNull(
                "RootSceneContainer/Run/GlobalUi/OverlayScreensContainer");
            if (container == null) return;

            // ScrollBoxes / "Choose a Bundle": the offered cards are grouped into packs, which the
            // flat _cards read below would collapse into one undifferentiated blob. Emit the grouping
            // as loot.packs and DON'T also flatten them into loot.cards (consumers hide the flat card
            // list when packs is present, so leaving them in cards would double-render them).
            if (FindVisibleScreen(container, "NChooseABundleSelectionScreen") is { } bundleScreen)
            {
                var packs = ReadBundles(bundleScreen);
                if (packs.Count > 0)
                {
                    snap.Loot ??= new LootInfo();
                    snap.Loot.Packs = packs;
                }
                return;
            }

            var screen = FindVisibleCardSelect(container);
            if (screen == null || Reflect.GetMember(screen, "_cards") is not IEnumerable cards) return;

            var ids = new List<string>();
            foreach (var c in cards)
            {
                var id = BareId(Reflect.GetString(c, "Id"));
                if (!string.IsNullOrEmpty(id)) ids.Add(id!);
            }
            if (ids.Count == 0) return;

            snap.Loot ??= new LootInfo();
            snap.Loot.Cards.AddRange(ids);
            snap.CardReward = snap.Loot.Cards;
        }
        catch { /* best-effort; the card-select grid just won't surface */ }
    }

    // The visible reward card-select grid among the overlay stack's children, if any. Reward
    // selectors only (NSimpleCardSelectScreen / NChooseACardSelectionScreen).
    private static Node? FindVisibleCardSelect(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            var n = child.GetType().Name;
            if ((n == "NSimpleCardSelectScreen" || n == "NChooseACardSelectionScreen")
                && child is CanvasItem ci && ci.IsVisibleInTree())
                return child;
            if (FindVisibleCardSelect(child) is { } nested) return nested;
        }
        return null;
    }

    // First visible node of the given runtime type name under this subtree, or null. Used to find
    // the bundle screen the same way FindVisibleCardSelect finds the flat card-select screens.
    private static Node? FindVisibleScreen(Node node, string typeName)
    {
        foreach (var child in node.GetChildren())
        {
            if (child.GetType().Name == typeName && child is CanvasItem ci && ci.IsVisibleInTree())
                return child;
            if (FindVisibleScreen(child, typeName) is { } nested) return nested;
        }
        return null;
    }

    // The bundle screen's packs as lists of bare card ids, from its _bundles field (an
    // IReadOnlyList<IReadOnlyList<CardModel>>). Empty bundles are dropped.
    private static List<List<string>> ReadBundles(Node screen)
    {
        var packs = new List<List<string>>();
        if (Reflect.GetMember(screen, "_bundles") is not IEnumerable bundles) return packs;
        foreach (var bundle in bundles)
        {
            var ids = new List<string>();
            foreach (var card in Enumerate(bundle))
            {
                var id = BareId(Reflect.GetString(card, "Id"));
                if (!string.IsNullOrEmpty(id)) ids.Add(id!);
            }
            if (ids.Count > 0) packs.Add(ids);
        }
        return packs;
    }

    private static string DetectScreen(object run)
    {
        if (Reflect.GetMember(run, "CombatRoom") != null) return "combat";
        if (Reflect.GetMember(run, "EventRoom") != null) return "event";
        if (Reflect.GetMember(run, "MapRoom") != null) return "map";
        if (Reflect.GetMember(run, "MerchantRoom") != null) return "merchant";
        if (Reflect.GetMember(run, "RestSiteRoom") != null) return "rest";
        if (Reflect.GetMember(run, "TreasureRoom") != null) return "treasure";
        return "menu-or-transition";
    }

    // The Game node is a direct child of the scene root. Matches compendium, which
    // found GetChildren works on the root Window where GetChild/GetChildCount did not.
    private static Node? FindChild(Node parent, string name)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child.Name.ToString() == name) return child;
        }
        return null;
    }

    // Count via .Count, then .Cards, else enumerate. Mirrors compendium's CountAny.
    private static int CountAny(object? pile)
    {
        if (pile == null) return 0;
        if (Reflect.GetMember(pile, "Count") is int n) return n;
        if (Reflect.GetMember(pile, "Cards") is IEnumerable cards) return Count(cards);
        if (pile is IEnumerable e) return Count(e);
        return 0;
    }

    private static int Count(IEnumerable e)
    {
        var n = 0;
        foreach (var _ in e) n++;
        return n;
    }

    private static List<DeckEntry> ReadDeck(object player) => ReadCardList(Reflect.GetMember(player, "Deck"));

    // Read a card collection (the player's Deck, or a combat pile like Hand) into DeckEntry list.
    private static List<DeckEntry> ReadCardList(object? container)
    {
        var list = new List<DeckEntry>();
        foreach (var card in Enumerate(container))
        {
            if (card == null) continue;
            var ench = Reflect.GetMember(card, "Enchantment");
            list.Add(new DeckEntry
            {
                Id = BareId(Reflect.GetString(card, "Id")) ?? "",
                Upgraded = Reflect.GetBool(card, "IsUpgraded"),
                Enchantment = ench == null ? null : BareId(Reflect.GetString(ench, "Id")),
                FloorAddedToDeck = GetNullableInt(card, "FloorAddedToDeck"),
            });
        }
        return list;
    }

    private static List<RelicEntry> ReadRelics(object player)
    {
        var list = new List<RelicEntry>();
        foreach (var relic in Enumerate(Reflect.GetMember(player, "Relics")))
        {
            if (relic == null) continue;
            list.Add(new RelicEntry
            {
                Id = BareId(Reflect.GetString(relic, "Id")) ?? "",
                FloorAddedToDeck = GetNullableInt(relic, "FloorAddedToDeck"),
            });
        }
        return list;
    }

    private static List<PotionEntry> ReadPotions(object player)
    {
        var list = new List<PotionEntry>();
        foreach (var potion in Enumerate(Reflect.GetMember(player, "Potions")))
        {
            if (potion == null) continue;
            list.Add(new PotionEntry { Id = BareId(Reflect.GetString(potion, "Id")) ?? "" });
        }
        return list;
    }

    // A CardPile exposes its items via .Cards; relic/potion collections are plain
    // IEnumerable lists. Try .Cards first, then the container itself.
    private static IEnumerable Enumerate(object? container)
    {
        if (container == null) yield break;
        var seq = Reflect.GetMember(container, "Cards") as IEnumerable ?? container as IEnumerable;
        if (seq == null) yield break;
        foreach (var item in seq) yield return item;
    }

    private static int? GetNullableInt(object? target, string name)
    {
        var v = Reflect.GetMember(target, name);
        if (v == null) return null;
        try { return Convert.ToInt32(v); }
        catch { return null; }
    }

    // "CHARACTER.IRONCLAD (123)" -> "IRONCLAD". Strip the trailing id and the prefix
    // so the value drops straight into /api/cards-style routes and CDN urls.
    private static string? BareId(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var s = raw;
        var space = s.IndexOf(' ');
        if (space >= 0) s = s.Substring(0, space);
        var dot = s.IndexOf('.');
        if (dot >= 0) s = s.Substring(dot + 1);
        return s;
    }
}
