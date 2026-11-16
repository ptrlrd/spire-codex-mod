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

    // `entryId` is MerchantEntry.Id, what MerchantInventory.TryPurchaseEntryWithIdUnsynchronized
    // takes. `blitzEntry` is BlitzMerchantEntry.MerchantEntryType, written for a Blitz entry only.
    // For a Blitz entry `slot` is the index in MerchantInventory.BlitzEntries.
    public static ReplayLine Entry(ReplayLine line, int? purchaseId, string kind, int? slot, string? id,
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
