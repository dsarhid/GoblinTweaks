using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace GoblinTweaks.Core;

/// <summary>One stack the player could put up on the Market Board.</summary>
/// <param name="Key">Unique per container + slot, so a selection survives tab switches and rescans.</param>
internal sealed record SellableItem(
    string Key, uint ItemId, bool Hq, string Name, uint IconId, byte Rarity, int ItemLevel, int Quantity);

internal enum SellSource { Bags, Armoury, Retainer }

/// <summary>A tab of the sell window: bags, armoury chest or one retainer.</summary>
internal sealed record SellTab(string Id, SellSource Source, ulong RetainerId, string Title);

/// <summary>
/// Reads the sellable (marketable) stacks of every inventory the sell window shows. The game only keeps the
/// ONE retainer's inventory in memory (the last one opened), so everything seen is remembered
/// per character and saved to disk for later sessions.
/// </summary>
internal static unsafe class SellInventory
{
    internal static readonly InventoryType[] BagTypes =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    internal static readonly InventoryType[] ArmouryTypes =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    private static readonly InventoryType[] RetainerTypes =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    ];

    // Last known contents of containers the game unloads, keyed "<contentId>:<tabId>".
    private static Dictionary<string, List<SellableItem>>? _cache;

    private static string CachePath => Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "RetainerSellCache.json");

    private static string CacheKey(SellTab tab) => $"{Svc.PlayerState.ContentId}:{tab.Id}";

    public static bool IsArmoury(InventoryType type) => Array.IndexOf(ArmouryTypes, type) >= 0;

    /// <summary>Tabs in display order: bags, armoury, then every available retainer.</summary>
    public static List<SellTab> BuildTabs(Func<SellSource, string> title)
    {
        var tabs = new List<SellTab>
        {
            new("bags", SellSource.Bags, 0, title(SellSource.Bags)),
            new("armoury", SellSource.Armoury, 0, title(SellSource.Armoury)),
        };

        var retMgr = RetainerManager.Instance();
        if (retMgr == null) return tabs;

        var count = retMgr->GetRetainerCount();
        for (uint i = 0; i < count && i < 10; i++)
        {
            var ret = retMgr->GetRetainerBySortedIndex(i);
            if (ret == null || !ret->Available) continue;

            tabs.Add(new SellTab($"ret{ret->RetainerId}", SellSource.Retainer, ret->RetainerId, RetainerName(ret, i)));
        }

        return tabs;
    }

    /// <summary>Saves the contents of the retainer that is open right now (call regularly, window open or not).</summary>
    public static void CaptureActiveRetainer()
    {
        var retMgr = RetainerManager.Instance();
        var active = retMgr == null ? null : retMgr->GetActiveRetainer();
        if (active == null) return;

        Read(new SellTab($"ret{active->RetainerId}", SellSource.Retainer, active->RetainerId, string.Empty));
    }

    public static string RetainerName(RetainerManager.Retainer* ret, uint index)
    {
        var span = ret->Name;
        var end  = span.IndexOf((byte)0);
        var name = Encoding.UTF8.GetString(end >= 0 ? span[..end] : span);
        return string.IsNullOrWhiteSpace(name) ? $"Retainer {index + 1}" : name;
    }

    /// <summary>Returns the sellable stacks of a tab; falls back to the last known contents when the game has unloaded it.</summary>
    public static List<SellableItem> Read(SellTab tab)
    {
        var mgr = InventoryManager.Instance();
        if (mgr == null) return Cached(tab);

        var types = tab.Source switch
        {
            SellSource.Bags      => BagTypes,
            SellSource.Armoury   => ArmouryTypes,
            _                    => RetainerTypes,
        };

        // RetainerPage1-7 always hold the retainer opened last, whichever tab asks for them.
        if (tab.Source == SellSource.Retainer)
        {
            // Only trust the pages while that retainer is actually open; right after picking another one
            // from the list they still hold the previous retainer's items.
            var retMgr = RetainerManager.Instance();
            var active = retMgr == null ? null : retMgr->GetActiveRetainer();
            if (active == null || active->RetainerId != tab.RetainerId) return Cached(tab);
        }

        var items  = new List<SellableItem>();
        var loaded = false;
        var sheet  = Svc.Data.GetExcelSheet<LuminaItem>();
        if (sheet == null) return [];

        foreach (var type in types)
        {
            var container = mgr->GetInventoryContainer(type);
            if (container == null || container->Size == 0 || !container->IsLoaded) continue;

            loaded = true;
            for (var s = 0; s < container->Size; s++)
            {
                var slot = container->GetInventorySlot(s);
                if (slot == null || slot->ItemId == 0) continue;

                if (!sheet.TryGetRow(slot->ItemId, out var item)) continue;

                var hq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
                if (!Sellable(item, hq)) continue;

                // Gear that has been worn (spiritbond above 0) is Market Prohibited, whatever the item itself allows.
                var collectable = (slot->Flags & InventoryItem.ItemFlags.Collectable) != 0;
                if (!collectable && slot->SpiritbondOrCollectability > 0) continue;
                items.Add(new SellableItem(
                    $"{tab.Id}|{(int)type}|{s}", slot->ItemId, hq, item.Name.ExtractText(), item.Icon,
                    item.Rarity, (int)item.LevelItem.RowId, (int)slot->Quantity));
            }
        }

        if (!loaded) return Cached(tab);

        // Only the inventories the game unloads need remembering.
        if (tab.Source == SellSource.Retainer)
            Remember(tab, items);
        return items;
    }

    /// <summary>
    /// Whether the Market Board accepts the stack. Besides having a search category and being tradable, a stack
    /// flagged HQ of an item that cannot be HQ is "Market Prohibited" in game, and so are collectables.
    /// </summary>
    private static bool Sellable(LuminaItem item, bool hq)
        => item.ItemSearchCategory.RowId != 0 && !item.IsUntradable && !item.IsCollectable && (!hq || item.CanBeHq);

    // Saved lists may predate the current rules, so they are filtered again.
    private static List<SellableItem> Cached(SellTab tab)
    {
        var saved = Load().GetValueOrDefault(CacheKey(tab));
        if (saved is null) return [];

        var sheet = Svc.Data.GetExcelSheet<LuminaItem>();
        return sheet == null ? saved : saved.Where(i => sheet.TryGetRow(i.ItemId, out var item) && Sellable(item, i.Hq)).ToList();
    }

    private static void Remember(SellTab tab, List<SellableItem> items)
    {
        var cache = Load();
        var key   = CacheKey(tab);
        if (cache.TryGetValue(key, out var old) && old.SequenceEqual(items)) return;

        cache[key] = items;
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "SellInventory: could not save the inventory cache");
        }
    }

    private static Dictionary<string, List<SellableItem>> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            if (File.Exists(CachePath))
                _cache = JsonSerializer.Deserialize<Dictionary<string, List<SellableItem>>>(File.ReadAllText(CachePath));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "SellInventory: could not read the inventory cache");
        }

        return _cache ??= new();
    }
}
