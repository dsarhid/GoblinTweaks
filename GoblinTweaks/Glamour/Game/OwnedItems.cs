using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace GoblinTweaks.Glamour.Game;

/// <summary>
/// What the player owns: item ids (HQ folded to NQ) and unlocked facewear (as <see cref="GlamourIds.FacewearBase"/> + row).
/// <c>DresserKnown</c> is false while the Glamour Dresser has not been opened this session (its contents are not loaded).
/// </summary>
internal sealed record OwnedSnapshot(HashSet<uint> ItemIds, bool DresserKnown);

/// <summary>Reads the items the player owns: armoire, glamour dresser, bags, armoury chest and equipped gear. Framework thread only.</summary>
internal static unsafe class OwnedItems
{
    private static readonly InventoryType[] Containers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.EquippedItems,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    public static OwnedSnapshot Read()
    {
        var ids = new HashSet<uint>();

        var inventory = InventoryManager.Instance();
        if (inventory != null)
        {
            foreach (var type in Containers)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;
                    ids.Add(slot->ItemId % 1_000_000);
                    if (slot->GlamourId != 0)
                        ids.Add(slot->GlamourId % 1_000_000);
                }
            }
        }

        var uiState = UIState.Instance();
        if (uiState != null && uiState->Cabinet.IsCabinetLoaded())
            foreach (var row in Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>())
                if (row.Item.RowId != 0 && uiState->Cabinet.IsItemInCabinet(row.RowId))
                    ids.Add(row.Item.RowId);

        var dresserKnown = false;
        var mirage = MirageManager.Instance();
        if (mirage != null && mirage->PrismBoxLoaded)
        {
            dresserKnown = true;
            foreach (var id in mirage->PrismBoxItemIds)
                if (id != 0)
                    ids.Add(id % 1_000_000);
        }

        foreach (var glasses in Svc.Data.GetExcelSheet<Glasses>())
            if (glasses.RowId != 0 && Svc.UnlockState.IsGlassesUnlocked(glasses))
                ids.Add(GlamourIds.FacewearBase + glasses.RowId);

        return new OwnedSnapshot(ids, dresserKnown);
    }
}

/// <summary>Narrows the catalog to what this character can wear.</summary>
internal static class CatalogFilter
{
    /// <summary>
    /// Items the character's race and gender can wear; weapons only of the current job's kind; with
    /// <paramref name="jobOnly"/>, armor only the current job can equip; with <paramref name="owned"/>, only owned items.
    /// </summary>
    public static List<CatalogItem> For(GlamourCatalog catalog, CharacterInfo who, OwnedSnapshot? owned, bool jobOnly)
    {
        var jobBit = who.JobId < 64 ? 1UL << (int)who.JobId : 0UL;
        var result = new List<CatalogItem>();
        foreach (var item in catalog.Items)
        {
            if (catalog.RaceRules.TryGetValue(item.RaceCategory, out var rule) && !Wearability.CanWear(rule, who.Race, who.Male))
                continue;
            var weapon = item.Slot is GlamourSlot.MainHand or GlamourSlot.OffHand;
            if ((weapon || jobOnly) && catalog.ItemJobs.TryGetValue(item.Id, out var jobs) && (jobs & jobBit) == 0)
                continue;
            if (owned is not null && !owned.ItemIds.Contains(item.Id))
                continue;
            result.Add(item);
        }
        return result;
    }
}
