using System.Collections.Generic;

namespace SpireCodex.Replay;

// The identity of a shop entry on the `buy_start` and `buy` rows, filled from plain values so
// the shape is testable without the game. The hooks in ReplayHooks read the MerchantEntry and
// call these.
//
// A Blitz merchant entry is bought for 0 gold and its reward (a card reward, an upgrade, a
// relic...) runs inside OnTryPurchaseInternal (BlitzMerchantEntry, v0.113), so the `buy` row
// lands after the rows it caused. `buy_start` is written before them; the two share
// `purchase_id`.
internal static class ShopRows
{
    // `typeChain` is the entry's class followed by its base classes. Every Blitz entry derives
    // from BlitzMerchantEntry, and several share a word with a normal entry
    // (BlitzMerchantRelicEntry, BlitzMerchantCardRemovalEntry), so the base class is checked first.
    public static string Kind(IEnumerable<string> typeChain)
    {
        string? type = null;
        foreach (var name in typeChain)
        {
            if (name == "BlitzMerchantEntry") return "blitz";
            type ??= name;
        }
        if (type == null) return "other";
        return type.Contains("Relic") ? "relic"
            : type.Contains("Potion") ? "potion"
            : type.Contains("CardRemoval") ? "removal_service"
            : type.Contains("Card") ? "card" : "other";
    }

    // The Blitz merchant's offers on the `shop` row, in MerchantInventory.BlitzEntries order, so
    // `slot` here is the `slot` a blitz `buy` row names. Status is the entry's
    // BlitzEntryStatus: Used once picked, Disabled for the rest once BlitzChoicesRemaining
    // reaches 0 (MerchantInventory.UpdateEntries).
    public static List<ReplayLine>? BlitzOffers(IReadOnlyList<(string? Entry, string Class, string? Status)> offers)
    {
        if (offers.Count == 0) return null;
        var rows = new List<ReplayLine>(offers.Count);
        for (var i = 0; i < offers.Count; i++)
            rows.Add(new ReplayLine("i")
                .Set("slot", i)
                .Set("blitz_entry", offers[i].Entry)
                .Set("entry_class", offers[i].Class)
                .Set("status", offers[i].Status?.ToLowerInvariant()));
        return rows;
    }

    // MerchantEntry.OnTryPurchaseUnsynchronized's own gate: out of stock fails, and too little
    // gold fails unless ignoreCost. Lord's Parasol buys the whole shop with ignoreCost: true
    // (LordsParasol.PurchaseEverythingLocked), so an item the player could not afford still
    // starts a purchase.
    public static bool PurchaseCanStart(bool isStocked, bool enoughGold, bool ignoreCost)
        => isStocked && (enoughGold || ignoreCost);

    // `purchaseId` on a buy is the `s` of the buy_start that opened it. `s` is unique within a
    // journal across save-and-quit reloads; a counter of our own would restart on reload.
    // `entryId` is MerchantEntry.Id, what MerchantInventory.TryPurchaseEntryWithIdUnsynchronized
    // takes. `blitzEntry` is BlitzMerchantEntry.MerchantEntryType, written for a Blitz entry only.
    // For a Blitz entry `slot` is the index in MerchantInventory.BlitzEntries.
    public static ReplayLine Entry(ReplayLine line, long? purchaseId, string kind, int? slot, string? id,
                                   uint? entryId, string entryClass, string? blitzEntry)
    {
        line.Set("purchase_id", purchaseId)
            .Set("kind", kind)
            .Set("slot", slot)
            .Set("id", id)
            .Set("entry_id", entryId is { } e ? (long)e : (long?)null)
            .Set("entry_class", entryClass);
        if (kind == "blitz") line.Set("blitz_entry", blitzEntry);
        return line;
    }
}
