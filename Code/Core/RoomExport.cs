using System.Collections;
using System.Collections.Generic;
using SpireCodex.Producer;

namespace SpireCodex.Core;

// Reads the current room's spectator detail off RunState.CurrentRoom: the live event
// (name, prompt, the options on offer) when it's an event room, and the merchant
// inventory (items + costs) when it's a shop. Both are small and change rarely, so they
// are computed per snapshot (no separate throttle) and returned as plain DTOs; all game
// reflection stays here in Core.
//
// Reachability confirmed against the decompiled sts2.dll:
//  - RunState.CurrentRoom -> AbstractRoom? (the room stack's last entry).
//  - EventRoom.LocalMutableEvent -> the live EventModel clone that actually holds state
//    (BeginEvent runs on CanonicalEvent.ToMutable(), so CanonicalEvent itself stays bare).
//    Reading it is side-effect-free: GetLocalEvent() is a verified pure _events[slot] list
//    lookup. EventModel.Title/Description are LocStrings (resolve via GetFormattedText()),
//    and CurrentOptions is the live IReadOnlyList<EventOption>.
//  - MerchantRoom.Inventory.{CardEntries,RelicEntries,PotionEntries,CardRemovalEntry};
//    each entry exposes Id (via Model/CreationResult.Card), Cost, IsStocked, IsOnSale.
internal static class RoomExport
{
    public static (EventInfo? Event, ShopInfo? Shop) Read(object? state)
    {
        var room = Reflect.GetMember(state, "CurrentRoom");
        if (room == null) return (null, null);

        return room.GetType().Name switch
        {
            "EventRoom" => (ReadEvent(room), null),
            "MerchantRoom" => (null, ReadShop(room)),
            _ => (null, null),
        };
    }

    private static EventInfo? ReadEvent(object room)
    {
        // The live mutable event holds the current page + options; fall back to the
        // canonical instance for the id if the synchronizer isn't ready yet.
        var live = Reflect.GetMember(room, "LocalMutableEvent");
        var idSource = live ?? Reflect.GetMember(room, "CanonicalEvent");
        var id = Ids.Bare(Reflect.GetString(room, "ModelId"))
                 ?? Ids.Bare(Reflect.GetString(idSource, "Id"));
        if (id == null) return null;

        var options = new List<EventOptionInfo>();
        if (Reflect.GetMember(live, "CurrentOptions") is IEnumerable opts)
        {
            foreach (var opt in opts)
            {
                if (opt == null) continue;
                var key = Reflect.GetString(opt, "TextKey") ?? "";
                var text = LocText(Reflect.GetMember(opt, "Title")) ?? key;
                options.Add(new EventOptionInfo(
                    key, text,
                    Reflect.GetBool(opt, "IsLocked"),
                    Reflect.GetBool(opt, "IsProceed"),
                    Reflect.GetBool(opt, "WasChosen")));
            }
        }

        return new EventInfo(
            id,
            LocText(Reflect.GetMember(live, "Title")),
            LocText(Reflect.GetMember(live, "Description")),
            options);
    }

    private static ShopInfo? ReadShop(object room)
    {
        var inv = Reflect.GetMember(room, "Inventory");
        if (inv == null) return null;

        var cards = new List<ShopItemInfo>();
        AddCards(Reflect.GetMember(inv, "CharacterCardEntries"), cards, "character");
        AddCards(Reflect.GetMember(inv, "ColorlessCardEntries"), cards, "colorless");

        var relics = new List<ShopItemInfo>();
        AddModelItems(Reflect.GetMember(inv, "RelicEntries"), relics);
        var potions = new List<ShopItemInfo>();
        AddModelItems(Reflect.GetMember(inv, "PotionEntries"), potions);

        ShopRemovalInfo? removal = null;
        if (Reflect.GetMember(inv, "CardRemovalEntry") is { } rm)
            removal = new ShopRemovalInfo(Reflect.GetInt(rm, "Cost"), Reflect.GetBool(rm, "IsStocked"));

        return new ShopInfo(cards, relics, potions, removal);
    }

    // Card entries: id lives under CreationResult.Card; IsOnSale flags the discounted one.
    private static void AddCards(object? entries, List<ShopItemInfo> into, string slot)
    {
        if (entries is not IEnumerable list) return;
        foreach (var e in list)
        {
            if (e == null) continue;
            var stocked = Reflect.GetBool(e, "IsStocked");
            var id = stocked
                ? Ids.Bare(Reflect.GetString(Reflect.GetMember(Reflect.GetMember(e, "CreationResult"), "Card"), "Id"))
                : null;
            into.Add(new ShopItemInfo(id, Reflect.GetInt(e, "Cost"), stocked, Reflect.GetBool(e, "IsOnSale"), slot));
        }
    }

    // Relic/potion entries: id lives under Model; no on-sale flag.
    private static void AddModelItems(object? entries, List<ShopItemInfo> into)
    {
        if (entries is not IEnumerable list) return;
        foreach (var e in list)
        {
            if (e == null) continue;
            var stocked = Reflect.GetBool(e, "IsStocked");
            var id = stocked ? Ids.Bare(Reflect.GetString(Reflect.GetMember(e, "Model"), "Id")) : null;
            into.Add(new ShopItemInfo(id, Reflect.GetInt(e, "Cost"), stocked, false, null));
        }
    }

    // A LocString resolves its localized text only through GetFormattedText(); its
    // ToString() is unhelpful. Null/empty -> null so the payload omits it.
    private static string? LocText(object? locString)
    {
        var s = Reflect.CallString(locString, "GetFormattedText");
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
