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
            snap.CardReward = ReadCardRewardOptions(game);
            MapExport.Update(state, snap); // act map for the live spectator view (throttled)
            (snap.Event, snap.Shop) = RoomExport.Read(state); // live event / shop detail
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
        snap.MapCoord = Reflect.GetMember(state, "CurrentMapCoord")?.ToString();
        snap.Seed = Reflect.GetMember(Reflect.GetMember(state, "Rng"), "Seed")?.ToString();

        object? firstPlayer = null;
        if (Reflect.GetMember(state, "Players") is IEnumerable players)
        {
            foreach (var player in players)
            {
                if (player == null) continue;
                firstPlayer ??= player;
                snap.Players.Add(ReadPlayer(player));
            }
        }

        snap.PlayerCount = snap.Players.Count;
        if (firstPlayer != null && snap.Players.Count > 0)
        {
            var p = snap.Players[0];
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
            if (Config.DumpIntrospection) Introspect.DumpOnce(state, firstPlayer);

            // Full live contents (player 0). MP per-player lists can come later.
            snap.Deck = ReadDeck(firstPlayer);
            snap.Relics = ReadRelics(firstPlayer);
            snap.Potions = ReadPotions(firstPlayer);

            // Combat state (present only during a fight).
            snap.Combat = ReadCombat(state, firstPlayer);
            snap.Energy = snap.Combat?.Energy ?? 0;
        }
    }

    private static CombatSnapshot? ReadCombat(object state, object player)
    {
        var pcs = Reflect.GetMember(player, "PlayerCombatState");
        if (pcs == null) return null; // not in a fight

        var combat = new CombatSnapshot { Energy = Reflect.GetInt(pcs, "Energy") };

        var room = Reflect.GetMember(state, "CurrentRoom");
        combat.Turn = GetNullableInt(Reflect.GetMember(room, "CombatState"), "RoundNumber");

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
                    Powers = ReadPowerIds(e),
                    Intents = ReadIntents(monster),
                });
            }
        }

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

    private static List<string> ReadPowerIds(object creature)
    {
        var ids = new List<string>();
        if (Reflect.GetMember(creature, "Powers") is IEnumerable powers)
        {
            foreach (var p in powers)
            {
                if (p == null) continue;
                var id = BareId(Reflect.GetString(p, "Id"));
                if (!string.IsNullOrEmpty(id)) ids.Add(id!);
            }
        }
        return ids;
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

        return ps;
    }

    // The card-reward options live on the NRewardsScreen UI node: its reward buttons each
    // carry a Reward; the CardReward one exposes Cards (the offered CardModels). Returns the
    // bare card ids, or empty when no card reward screen is visible.
    private static List<string> ReadCardRewardOptions(Node game)
    {
        var result = new List<string>();
        try
        {
            var screen = game.GetNodeOrNull(
                "RootSceneContainer/Run/GlobalUi/OverlayScreensContainer/RewardsScreen");
            if (screen == null) return result;
            if (screen is CanvasItem ci && !ci.IsVisibleInTree()) return result;

            if (Reflect.GetMember(screen, "_rewardButtons") is not IEnumerable buttons) return result;
            foreach (var btn in buttons)
            {
                var reward = Reflect.GetMember(btn, "Reward");
                if (reward == null || reward.GetType().Name != "CardReward") continue;
                if (Reflect.GetMember(reward, "Cards") is IEnumerable cards)
                {
                    foreach (var card in cards)
                    {
                        var id = BareId(Reflect.GetString(card, "Id"));
                        if (!string.IsNullOrEmpty(id)) result.Add(id!);
                    }
                }
            }
        }
        catch
        {
            // tolerate any tree/shape change
        }
        return result;
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

    private static List<DeckEntry> ReadDeck(object player)
    {
        var list = new List<DeckEntry>();
        foreach (var card in Enumerate(Reflect.GetMember(player, "Deck")))
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
