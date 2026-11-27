using System;
using System.Collections;
using System.Collections.Generic;
using SpireCodex.Core;

namespace SpireCodex.Replay;

// Co-op ownership on journal rows (replay_version 10). STS2 co-op runs in lockstep: every
// teammate decision reaches this client and is applied to this client's copy of the teammate,
// so one player's journal can describe both players if each row says whose it is.
//
// Everything here is written in co-op only, so a single-player journal is unchanged:
// - `mine` (existing): the row's player is the local one.
// - `player_slot` (new): that player's index in RunState.Players (RunState.GetPlayerSlotIndex),
//   the same on every machine and in the header's `players`. `slot` already means a shop or
//   potion slot on some rows, hence the longer name. No Steam id or net id is ever written.
// - `<x>_mine` / `<x>_player_slot` (new): for a row field `<x>` (src, dst, tgt, target) naming
//   a creature that is a player. `<x>_owner_mine` / `<x>_owner_slot` when it is a player's pet.
// - `peer_msg` rows (new): a teammate's out-of-combat decision as this client received it.
//
// Plain values in and out, so the shapes are testable without the game; ReplayHooks reads
// the game objects and calls these.
internal static class CoopRows
{
    // Index of `player` in `players` (RunState.Players), by reference, as
    // RunState.GetPlayerSlotIndex(Player) does with IndexOf.
    public static int? SlotIn(IEnumerable? players, object? player)
    {
        if (players == null || player == null) return null;
        var i = 0;
        foreach (var p in players)
        {
            if (ReferenceEquals(p, player)) return i;
            i++;
        }
        return null;
    }

    // RunState.GetPlayerSlotIndex(ulong netId): the first player whose NetId matches.
    public static int? SlotOfNetId(IEnumerable? players, ulong netId)
    {
        if (players == null) return null;
        var i = 0;
        foreach (var p in players)
        {
            var id = Reflect.GetMember(p, "NetId");
            try { if (id != null && Convert.ToUInt64(id) == netId) return i; }
            catch { }
            i++;
        }
        return null;
    }

    public static ReplayLine Owner(ReplayLine line, bool coop, bool? mine, int? slot)
    {
        if (!coop) return line;
        return line.Set("mine", mine).Set("player_slot", slot);
    }

    // `prefix` is the row's field for the creature (src, dst, tgt, target). A player creature
    // gets <prefix>_mine and <prefix>_player_slot; a pet gets <prefix>_owner_mine and
    // <prefix>_owner_slot; a monster gets nothing.
    public static ReplayLine Side(ReplayLine line, string prefix, bool coop,
                                  bool? mine, int? slot, bool? ownerMine, int? ownerSlot)
    {
        if (!coop) return line;
        if (mine != null || slot != null)
            return line.Set(prefix + "_mine", mine).Set(prefix + "_player_slot", slot);
        return line.Set(prefix + "_owner_mine", ownerMine).Set(prefix + "_owner_slot", ownerSlot);
    }

    // Messages that are cosmetic (hover) or that carry combat actions already written by the
    // combat hooks (ActionEnqueued and friends, which also echo the local player's own actions
    // back from the host).
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "RestSiteOptionHoveredMessage",
        "ActionEnqueuedMessage",
        "RequestEnqueueActionMessage",
        "HookActionEnqueuedMessage",
        "RequestEnqueueHookActionMessage",
        "ResumeActionAfterPlayerChoiceMessage",
        "RequestResumeActionAfterPlayerChoiceMessage",
    };

    public static bool Records(string messageType) => !Skipped.Contains(messageType);

    // One `peer_msg` row: the message class, who sent it, where it is for, and the fields that
    // say what was decided. Field names follow the message's own field names in snake_case.
    // An unknown message type still gets a row with msg, sender and location.
    public static ReplayLine? PeerMessage(ReplayLine line, string messageType, int? senderSlot, bool? mine,
                                          object message)
    {
        if (!Records(messageType)) return null;
        line.Set("msg", messageType)
            .Set("sender_slot", senderSlot)
            .Set("mine", mine)
            .Set("location", Location(Reflect.GetMember(message, "Location")));
        switch (messageType)
        {
            case "RewardSelectedMessage":
                line.Set("set_id", Int(message, "setId")).Set("reward_index", Int(message, "rewardIndex"));
                break;
            case "RewardSetSkippedMessage":
                line.Set("set_id", Int(message, "setId"));
                break;
            case "MerchantEntryPurchasedMessage":
                line.Set("entry_id", Long(message, "id"));
                break;
            case "OptionIndexChosenMessage":
                line.Set("option_type", Snake(Reflect.GetMember(message, "type")?.ToString()))
                    .Set("option_index", Long(message, "optionIndex"));
                break;
            case "VotedForSharedEventOptionMessage":
            case "SharedEventOptionChosenMessage":
                line.Set("option_index", Long(message, "optionIndex"))
                    .Set("page_index", Long(message, "pageIndex"));
                break;
            case "RewardObtainedMessage":
                line.Set("reward_type", Snake(Reflect.GetMember(message, "rewardType")?.ToString()))
                    .Set("card", ModelId(Reflect.GetMember(message, "cardModel")))
                    .Set("potion", ModelId(Reflect.GetMember(message, "potionModel")))
                    .Set("relic", ModelId(Reflect.GetMember(message, "relicModel")))
                    .Set("gold", Int(message, "goldAmount"))
                    .Set("skipped", Reflect.GetMember(message, "wasSkipped") as bool?);
                break;
            case "GoldLostMessage":
                line.Set("gold_lost", Int(message, "goldLost"));
                break;
            case "CrystalSphereRewardsMessage":
                if (Reflect.GetMember(message, "rewards") is IEnumerable rewards)
                {
                    var rows = new List<ReplayLine>();
                    foreach (var r in rewards)
                        rows.Add(new ReplayLine("r")
                            .Set("type", Snake(Reflect.GetMember(r, "type")?.ToString()))
                            .Set("card_rarity", Snake(Reflect.GetMember(r, "cardRarity")?.ToString()))
                            .Set("potion_rarity", Snake(Reflect.GetMember(r, "potionRarity")?.ToString()))
                            .Set("big_gold", Reflect.GetMember(r, "isBigGold") as bool?));
                    line.Set("rewards", rows);
                }
                break;
        }
        return line;
    }

    // RunLocation: act index, map coord and room id, as "act:col,row:room". Missing parts are
    // empty, e.g. "0:3,0:" for a map point with no room id.
    public static string? Location(object? location)
    {
        if (location == null) return null;
        var map = Reflect.GetMember(location, "mapLocation");
        if (map == null) return null;
        var act = Reflect.GetMember(map, "actIndex") as int?;
        var coord = Reflect.GetMember(map, "coord");
        var col = Reflect.GetMember(coord, "col") as int?;
        var row = Reflect.GetMember(coord, "row") as int?;
        var room = Reflect.GetMember(location, "roomId") as int?;
        return $"{act}:{(col != null && row != null ? $"{col},{row}" : "")}:{room}";
    }

    private static int? Int(object target, string name) => Reflect.GetMember(target, name) switch
    {
        int i => i,
        uint u => (int)u,
        _ => null,
    };

    private static long? Long(object target, string name) => Reflect.GetMember(target, name) switch
    {
        uint u => u,
        int i => i,
        long l => l,
        _ => null,
    };

    // Same as Ids.Bare: "CARD.CLAW" -> "CLAW".
    private static string? ModelId(object? model)
    {
        var id = Reflect.GetString(model, "Id");
        if (string.IsNullOrEmpty(id)) return null;
        var space = id.IndexOf(' ');
        if (space >= 0) id = id[..space];
        var dot = id.IndexOf('.');
        return dot >= 0 ? id[(dot + 1)..] : id;
    }

    // PascalCase enum name -> snake_case: RestSite -> rest_site, SpecialCard -> special_card.
    public static string? Snake(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsUpper(ch))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(ch));
            }
            else sb.Append(ch);
        }
        return sb.ToString();
    }
}
