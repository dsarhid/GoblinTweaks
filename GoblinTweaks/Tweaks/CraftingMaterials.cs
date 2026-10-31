using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dalamud.Game;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using GoblinTweaks.Core;
using GoblinTweaks.Crafting;
using GoblinTweaks.Services;
using GoblinTweaks.UI;
using GoblinTweaks.Localization;
using LuminaItem = Lumina.Excel.Sheets.Item;
using LuminaRecipe = Lumina.Excel.Sheets.Recipe;

namespace GoblinTweaks.Tweaks;

// ── Data models ──────────────────────────────────────────────────────────────

public enum InventorySource { Player, Saddlebag, Retainer, FreeCompany }

/// <summary>What one retainer held when it was last opened.</summary>
public sealed class RetainerData
{
    /// <summary>The retainer's id; 0 in data saved before retainers were told apart by id (they are matched by name then).</summary>
    public ulong    Id;
    public string   Name = string.Empty;
    /// <summary>When its bags were last read (UTC); <see cref="DateTime.MinValue"/> if unknown.</summary>
    public DateTime Seen;
    public readonly Dictionary<uint, int> Items   = new();
    public readonly Dictionary<uint, int> ItemsHQ = new();

    public RetainerData Copy()
    {
        var copy = new RetainerData { Id = Id, Name = Name, Seen = Seen };
        foreach (var (k, v) in Items)   copy.Items[k]   = v;
        foreach (var (k, v) in ItemsHQ) copy.ItemsHQ[k] = v;
        return copy;
    }
}

/// <summary>Aggregated item quantities per inventory source, built by a single scan.</summary>
public sealed class InventorySnapshot
{
    public string PlayerName = string.Empty;

    /// <summary>True when live retainer data was available in memory during this scan (at least one retainer visited).</summary>
    public bool HasRetainerData;
    /// <summary>True when the FC chest container was accessible during this scan (chest opened this session).</summary>
    public bool HasFCData;
    /// <summary>True when the saddlebag was loaded in memory during this scan (opened this session).</summary>
    public bool HasSaddlebagData;

    public readonly Dictionary<uint, int>                              Player          = new();
    public readonly Dictionary<uint, int>                              PlayerHQ        = new();
    public readonly Dictionary<uint, int>                              Saddlebag       = new();
    public readonly Dictionary<uint, int>                              SaddlebagHQ     = new();
    public readonly Dictionary<uint, int>                              Retainer        = new(); // aggregate
    public readonly Dictionary<uint, int>                              RetainerHQ      = new(); // aggregate HQ
    public readonly Dictionary<uint, int>                              FCChest         = new();
    public readonly Dictionary<uint, int>                              FCChestHQ       = new();
    /// <summary>Where each item sits in the FC chest, as 1-based (tab, slot) pairs. Tab 0 is the crystal page.</summary>
    public readonly Dictionary<uint, List<(int Tab, int Slot)>>        FCChestSlots    = new();
    public readonly List<RetainerData>                                 RetainerDetails = [];
    /// <summary>Raw contents per FC chest page (1-based, 0 = crystals), so pages can be kept or replaced one by one. The aggregates above derive from it.</summary>
    public readonly Dictionary<int, List<(int Slot, uint Id, int Qty, bool Hq)>> FCChestPages = new();
    /// <summary>When each FC chest page was last read (UTC).</summary>
    public readonly Dictionary<int, DateTime>                          FCChestPageSeen = new();
    /// <summary>When the saddlebag was last read (UTC); <see cref="DateTime.MinValue"/> if unknown.</summary>
    public DateTime SaddlebagSeen;

    /// <summary>Rebuilds FCChest / FCChestHQ / FCChestSlots from FCChestPages.</summary>
    public void RebuildFCChest()
    {
        FCChest.Clear(); FCChestHQ.Clear(); FCChestSlots.Clear();
        foreach (var (page, items) in FCChestPages.OrderBy(kv => kv.Key))
            foreach (var (slot, id, qty, hq) in items)
            {
                FCChest[id] = FCChest.GetValueOrDefault(id) + qty;
                if (hq) FCChestHQ[id] = FCChestHQ.GetValueOrDefault(id) + qty;
                if (!FCChestSlots.TryGetValue(id, out var list)) FCChestSlots[id] = list = [];
                list.Add((page, slot));
            }
    }

    /// <summary>
    /// A copy holding only the sources that are counted. The saved snapshot keeps every source, so turning one off
    /// in the settings hides it without forgetting what it holds.
    /// </summary>
    public InventorySnapshot Only(bool saddlebag, bool retainers, bool fcChest)
    {
        var view = new InventorySnapshot { PlayerName = PlayerName };
        Copy(Player, view.Player);
        Copy(PlayerHQ, view.PlayerHQ);

        if (saddlebag)
        {
            view.HasSaddlebagData = HasSaddlebagData;
            view.SaddlebagSeen    = SaddlebagSeen;
            Copy(Saddlebag, view.Saddlebag);
            Copy(SaddlebagHQ, view.SaddlebagHQ);
        }

        if (retainers)
        {
            view.HasRetainerData = HasRetainerData;
            Copy(Retainer, view.Retainer);
            Copy(RetainerHQ, view.RetainerHQ);
            foreach (var retainer in RetainerDetails)
                view.RetainerDetails.Add(retainer.Copy());
        }

        if (fcChest)
        {
            view.HasFCData = HasFCData;
            Copy(FCChest, view.FCChest);
            Copy(FCChestHQ, view.FCChestHQ);
            foreach (var (k, v) in FCChestSlots) view.FCChestSlots[k] = [.. v];
            foreach (var (k, v) in FCChestPages) view.FCChestPages[k] = [.. v];
            foreach (var (k, v) in FCChestPageSeen) view.FCChestPageSeen[k] = v;
        }

        return view;

        static void Copy(Dictionary<uint, int> from, Dictionary<uint, int> to)
        {
            foreach (var (k, v) in from) to[k] = v;
        }
    }

    public int Get(uint itemId, InventorySource src) => src switch
    {
        InventorySource.Player      => Player   .GetValueOrDefault(itemId),
        InventorySource.Saddlebag   => Saddlebag.GetValueOrDefault(itemId),
        InventorySource.Retainer    => Retainer .GetValueOrDefault(itemId),
        InventorySource.FreeCompany => FCChest  .GetValueOrDefault(itemId),
        _                           => 0,
    };

    public int GetTotal(uint itemId) =>
        Player.GetValueOrDefault(itemId)    +
        Saddlebag.GetValueOrDefault(itemId) +
        Retainer.GetValueOrDefault(itemId)  +
        FCChest.GetValueOrDefault(itemId);

    /// <summary>Returns every source that has at least one of this item (for CraftStatus analysis).</summary>
    public IEnumerable<(InventorySource Source, int Qty)> BySource(uint itemId)
    {
        if (Player   .TryGetValue(itemId, out var q) && q > 0) yield return (InventorySource.Player,      q);
        if (Saddlebag.TryGetValue(itemId, out     q) && q > 0) yield return (InventorySource.Saddlebag,   q);
        if (Retainer .TryGetValue(itemId, out     q) && q > 0) yield return (InventorySource.Retainer,    q);
        if (FCChest  .TryGetValue(itemId, out     q) && q > 0) yield return (InventorySource.FreeCompany, q);
    }

    /// <summary>
    /// Returns human-readable location strings for tooltip display, including retainer names and HQ counts, with when
    /// that place was last read (<see cref="DateTime.MinValue"/> for the bags, which are always live, or when unknown).
    /// </summary>
    public IEnumerable<(string Location, int Total, int Hq, DateTime Seen)> Locations(uint itemId)
    {
        var q  = Player.GetValueOrDefault(itemId);
        var hq = PlayerHQ.GetValueOrDefault(itemId);
        if (q > 0)
            yield return (string.IsNullOrEmpty(PlayerName) ? CraftLoc.Get("loc.yourbags") : CraftLoc.Fmt("loc.bagsof", PlayerName), q, hq, DateTime.MinValue);

        q  = Saddlebag.GetValueOrDefault(itemId);
        hq = SaddlebagHQ.GetValueOrDefault(itemId);
        if (q > 0) yield return (CraftLoc.Get("loc.saddlebag"), q, hq, SaddlebagSeen);

        if (RetainerDetails.Count > 0)
        {
            foreach (var retainer in RetainerDetails)
            {
                q  = retainer.Items.GetValueOrDefault(itemId);
                hq = retainer.ItemsHQ.GetValueOrDefault(itemId);
                if (q > 0) yield return (CraftLoc.Fmt("loc.bagsof", retainer.Name), q, hq, retainer.Seen);
            }
        }
        else
        {
            q  = Retainer.GetValueOrDefault(itemId);
            hq = RetainerHQ.GetValueOrDefault(itemId);
            if (q > 0) yield return (CraftLoc.Get("loc.retainers"), q, hq, DateTime.MinValue);
        }

        q  = FCChest.GetValueOrDefault(itemId);
        hq = FCChestHQ.GetValueOrDefault(itemId);
        if (q > 0)
        {
            // Crystals (tab 0) have no tab.slot position worth showing. The age is that of the oldest page holding the item.
            var where = string.Empty;
            var seen  = DateTime.MinValue;
            if (FCChestSlots.TryGetValue(itemId, out var all))
            {
                if (all.Where(t => t.Tab > 0).ToList() is { Count: > 0 } slots)
                    where = " (" + string.Join(", ", slots.Take(4).Select(t => $"{t.Tab}.{t.Slot}")) + (slots.Count > 4 ? ", ..." : "") + ")";

                var pages = all.Select(t => FCChestPageSeen.GetValueOrDefault(t.Tab)).Where(d => d > DateTime.MinValue).ToList();
                if (pages.Count > 0) seen = pages.Min();
            }
            yield return (CraftLoc.Get("loc.fcchest") + where, q, hq, seen);
        }
    }
}

/// <summary>Availability of a single ingredient for a recipe.</summary>
public sealed record IngredientCheck(
    uint   ItemId,
    string Name,
    uint   IconId,
    int    Required,
    int    InPlayer,
    int    InOther)
{
    public int  Available  => InPlayer + InOther;
    public int  Missing    => Math.Max(0, Required - Available);
    public bool Fulfilled  => Available >= Required;
}

public enum CraftStatus
{
    /// <summary>All ingredients in the main bags — craft without moving anything.</summary>
    Ready,
    /// <summary>All ingredients exist but some are in saddlebag / retainer / FC chest.</summary>
    ReadyElsewhere,
    /// <summary>Missing only a few ingredient types (configurable via Options).</summary>
    NearComplete,
    /// <summary>Missing too many ingredients.</summary>
    NotReady,
}

public sealed class CraftableEntry
{
    public uint                  RecipeId;
    public uint                  ItemId;
    public string                Name            = string.Empty;
    public uint                  IconId;
    public ushort                ResultAmount;
    public string                CraftClass      = string.Empty;
    /// <summary>CraftType row (0 Woodworking … 7 Cooking).</summary>
    public uint                  CraftTypeId;
    /// <summary>The Disciples of the Hand and Land (ClassJob rows) that can equip the item made.</summary>
    public uint[]                ForJobs         = [];
    public CraftStatus           Status;
    public int                   MissingCount;
    public bool                  IsCrafted;
    public int                   ItemLevel;
    public int                   CraftLevel;   // recipe class-job level (1-100)
    public List<IngredientCheck> Ingredients     = [];
}

// ── Tweak ─────────────────────────────────────────────────────────────────────

[Tweak(TweakCategory.Crafting)]
public sealed class CraftingMaterials : Tweak<CraftingMaterials.Options>
{
    public sealed class Options
    {
        public bool IncludeSaddlebag   { get; set; } = true;
        public bool IncludeRetainers   { get; set; } = true;
        public bool IncludeFCChest     { get; set; } = false;
        /// <summary>Outline, in the FC chest, the bags, the retainers and the saddlebag, what holds the selected recipe's ingredients.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("HighlightFCChest")]
        public bool Highlight          { get; set; } = true;
        /// <summary>Dim, in the shop that is open, the rows that do not sell an ingredient of the selected recipe.</summary>
        public bool HighlightShops     { get; set; } = true;
        /// <summary>Max missing ingredient types before a recipe moves from NearComplete → NotReady.</summary>
        public int  NearCompleteMissing { get; set; } = 2;
        /// <summary>Old per-tweak window language. Only read to move it to the common language choice of the tweak.</summary>
        public string DataLanguage { get; set; } = string.Empty;

        // What the window shows, remembered between sessions and when the window is rebuilt (language change).
        /// <summary>Status view ("all", "ready", "near", "notReady") or job ("job:&lt;ClassJob row&gt;").</summary>
        public string View           { get; set; } = "ready";
        public string SortMode       { get; set; } = string.Empty;
        public bool   SortDescending { get; set; }
        /// <summary>Crafter class name ("" = all classes).</summary>
        public string ClassFilter    { get; set; } = string.Empty;
        public bool   HideCrafted    { get; set; }
        /// <summary>Only recipes your crafter of that class has the level for.</summary>
        public bool   OnlyMyLevel    { get; set; }

        /// <summary>"My List": the recipes to craft and how many times, in the order they were added.</summary>
        public List<CartItem> Cart            { get; set; } = [];
        /// <summary>The totals of My List count only what is missing (what you own is deducted).</summary>
        public bool           CartDeductOwned { get; set; } = true;
    }

    public const int MaxNearCompleteMissing = 10;

    private CraftingMaterialsAddon?     _addon;
    private CraftingSettingsAddon?      _settingsAddon;
    private CraftingHelpAddon?          _helpAddon;
    private UniversalisService?         _universalis;
    private readonly FCChestHighlighter _fcHighlighter = new();
    private readonly InventoryHighlighter _bagHighlighter = new();
    private readonly ShopHighlighter _shopHighlighter = new();

    public UniversalisService      Universalis     => _universalis!;
    public uint WorldId => Svc.PlayerState.IsLoaded ? Svc.PlayerState.HomeWorld.RowId : 0u;

    /// <summary>Game-data language for item and recipe names: the one the game client uses, so they read as in the game.</summary>
    public static ClientLanguage DataLanguage => Svc.ClientState.ClientLanguage;

    protected internal override void Enable()
    {
        MoveOldLanguageSetting();
        SanitizeCart();

        _universalis = new UniversalisService();
        CraftLoc.Lang = UiLanguage;
        _addon = NewAddon();

        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.GameInventory.InventoryChanged += OnInventoryChanged;
        Svc.Framework.Update += OnCaptureUpdate;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,  "RetainerList",         OnRetainerUiClosed);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,    "SynthesisSimpleResult", OnSynthesisResult);

        _settingsAddon = NewSettingsAddon();

        _fcHighlighter.Source = () => Settings.Highlight && Settings.IncludeFCChest && _addon is { IsOpen: true } a
            ? a.FCChestMarks()
            : [];
        _fcHighlighter.Start();

        _bagHighlighter.Source = () => Settings.Highlight && _addon is { IsOpen: true } a
            ? a.IngredientItemIds()
            : [];
        _bagHighlighter.Start();

        _shopHighlighter.Source = () => Settings.HighlightShops && _addon is { IsOpen: true } a
            ? a.IngredientItemIds()
            : [];
        _shopHighlighter.Start();
    }

    protected internal override void Disable()
    {
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        Svc.GameInventory.InventoryChanged -= OnInventoryChanged;
        Svc.Framework.Update -= OnCaptureUpdate;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize,  "RetainerList",         OnRetainerUiClosed);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup,    "SynthesisSimpleResult", OnSynthesisResult);

        _shopHighlighter.Stop();
        _shopHighlighter.Source = null;
        _bagHighlighter.Stop();
        _bagHighlighter.Source = null;
        _fcHighlighter.Stop();
        _fcHighlighter.Source = null;

        _helpAddon?.Dispose();
        _helpAddon = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;
        _addon?.Dispose();
        _addon = null;

        _universalis?.Dispose();
        _universalis = null;
    }

    /// <summary>Earlier versions kept the window language in the options of this tweak; it now uses the language choice every tweak has.</summary>
    private void MoveOldLanguageSetting()
    {
        if (Settings.DataLanguage.Length == 0) return;

        if (Language.Length == 0) SetLanguage(Settings.DataLanguage);
        Settings.DataLanguage = string.Empty;
        SaveSettings();
    }

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;

        CraftLoc.Lang = UiLanguage;
        Rebuild(ref _addon, NewAddon);
        Rebuild(ref _settingsAddon, NewSettingsAddon);

        var helpWasOpen = _helpAddon is { IsOpen: true };
        _helpAddon?.Dispose();
        _helpAddon = null;
        if (helpWasOpen) Svc.Framework.RunOnTick(OpenHelp, delayTicks: 6);
    }

    private CraftingMaterialsAddon NewAddon() => new()
    {
        InternalName    = "GtkCraftingMaterials",
        Title           = CraftLoc.Get("title"),
        Size            = new System.Numerics.Vector2(1180f, 740f),
        Tweak           = this,
        RespectCloseAll = false, // stay open when the game closes windows (chest, retainer, NPC talk)
    };

    // Adds a "Crafting Materials" shortcut at the bottom of the inventory item right-click menu.
    // (The main-menu "Logs" popup is not a Dalamud-extensible context menu, so it can't be reached
    // through this API — only the right-click menus fire this event.)
    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (args.MenuType != ContextMenuType.Inventory) return;

            args.AddMenuItem(new MenuItem
            {
                Name        = CraftLoc.Get("title"),
                PrefixChar  = 'G',
                PrefixColor = 539,
                OnClicked   = _ => _addon?.Open(),
                Priority    = int.MaxValue, // sort to the bottom of the menu
            });
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private bool     _captureDirty;
    private DateTime _nextCapture = DateTime.MinValue;

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        _captureDirty = true;
        _addon?.NotifyInventoryChanged();
    }

    // Only one retainer lives in memory at a time, so save the snapshot while a retainer is open
    // (window open or not) instead of waiting for the window; otherwise visiting retainer 2 loses retainer 1.
    private unsafe void OnCaptureUpdate(IFramework _)
    {
        if (!_captureDirty || DateTime.UtcNow < _nextCapture) return;

        try
        {
            var retMgr = RetainerManager.Instance();
            var active = retMgr == null ? null : retMgr->GetActiveRetainer();
            if (active == null) { _captureDirty = false; return; }

            // A retainer that was just opened may still show the previous one's pages: wait (keeping the flag) until they are its own.
            if (!RetainerSettled(active->RetainerId)) return;

            _captureDirty = false;
            _nextCapture  = DateTime.UtcNow.AddSeconds(2);
            ScanAndMerge();
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "CraftingMaterials: background capture failed"); }
    }

    // Retainer inventory is cached when the RetainerList UI closes (after visiting the bell).
    private void OnRetainerUiClosed(AddonEvent type, AddonArgs args) =>
        _addon?.NotifyInventoryChanged();

    // Synthesis result popup appears → immediately refresh to update craft check marks.
    private void OnSynthesisResult(AddonEvent type, AddonArgs args) =>
        _addon?.RequestRefresh();

    // ── Snapshot persistence ──────────────────────────────────────────────────
    // One file per character, so alts don't overwrite each other's retainers, saddlebag and FC chest.

    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = false };

    private InventorySnapshot? _cachedSnapshot; // every source of the character below, as last saved
    private ulong              _snapshotOwner;  // content id of the character the cached snapshot belongs to
    private string?            _lastSavedJson;

    private static string SnapshotFilePath(ulong contentId) =>
        Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), $"CraftingSnapshot_{contentId:X16}.json");

    // Before there was one file per character there was a single one, for whoever played last.
    private static string LegacySnapshotFilePath =>
        Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "CraftingSnapshot.json");

    /// <summary>Makes the cached snapshot the logged-in character's. False when no character is logged in.</summary>
    private bool EnsureCharacter()
    {
        var contentId = Svc.PlayerState.IsLoaded ? Svc.PlayerState.ContentId : 0UL;
        if (contentId == 0) return false;
        if (contentId == _snapshotOwner) return true;

        _snapshotOwner  = contentId;
        _cachedSnapshot = LoadSnapshot(contentId);
        _lastSavedJson  = null;
        _seenFCPages.Clear();
        _activeRetainerId = 0;
        return true;
    }

    private void SaveSnapshot(InventorySnapshot snap)
    {
        _cachedSnapshot = snap;
        try
        {
            var json = JsonSerializer.Serialize(InventorySnapshotData.From(snap), SnapshotJson);
            if (json == _lastSavedJson) return;

            var path = SnapshotFilePath(_snapshotOwner);
            var tmp  = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
            _lastSavedJson = json;
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "CraftingMaterials: could not save snapshot"); }
    }

    private InventorySnapshot? LoadSnapshot(ulong contentId)
    {
        try
        {
            var path = SnapshotFilePath(contentId);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<InventorySnapshotData>(File.ReadAllText(path), SnapshotJson)?.ToSnapshot();

            // The old single file is taken over by the character it was saved for.
            var legacy = LegacySnapshotFilePath;
            if (!File.Exists(legacy)) return null;

            var data = JsonSerializer.Deserialize<InventorySnapshotData>(File.ReadAllText(legacy), SnapshotJson);
            if (data is null || data.PlayerName != Svc.PlayerState.CharacterName) return null;

            File.Move(legacy, path, true);
            return data.ToSnapshot();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "CraftingMaterials: could not load snapshot");
            return null;
        }
    }

    /// <summary>
    /// Scans live inventories and merges saved retainer / saddlebag / FC data for whatever is not in memory now
    /// (not visited this session). Saves every source, and returns only the ones the settings count.
    /// With no character logged in nothing is read or saved.
    /// </summary>
    public InventorySnapshot ScanAndMerge()
    {
        if (!EnsureCharacter()) return new InventorySnapshot();

        var live = ScanInventories();

        if (_cachedSnapshot is { } saved)
        {
            // Only one retainer is live at a time, so merge per retainer: the live one wins, the rest come from the saved copy.
            // When the game knows the character's retainers, the saved ones that are gone (dismissed) are dropped and the
            // others take their current name (renamed).
            var current = CurrentRetainers();
            foreach (var savedRetainer in saved.RetainerDetails)
            {
                var retainer = savedRetainer.Copy();

                // Saved before retainers had an id: the retainer of that name, if there is one.
                if (retainer.Id == 0 && current is not null)
                    retainer.Id = current.FirstOrDefault(pair => pair.Value == retainer.Name).Key;

                if (current is not null)
                {
                    if (!current.TryGetValue(retainer.Id, out var name)) continue;
                    retainer.Name = name;
                }

                var isLive = retainer.Id != 0
                    ? live.RetainerDetails.Any(r => r.Id == retainer.Id)
                    : live.RetainerDetails.Any(r => r.Name == retainer.Name);
                if (!isLive) live.RetainerDetails.Add(retainer);
            }

            live.Retainer.Clear();
            live.RetainerHQ.Clear();
            foreach (var retainer in live.RetainerDetails)
            {
                foreach (var (k, v) in retainer.Items)   live.Retainer[k]   = live.Retainer.GetValueOrDefault(k)   + v;
                foreach (var (k, v) in retainer.ItemsHQ) live.RetainerHQ[k] = live.RetainerHQ.GetValueOrDefault(k) + v;
            }
            live.HasRetainerData = live.RetainerDetails.Count > 0;

            // The saddlebag is only in memory after opening it; keep the last known contents.
            if (!live.HasSaddlebagData)
            {
                foreach (var (k, v) in saved.Saddlebag)   live.Saddlebag[k]   = v;
                foreach (var (k, v) in saved.SaddlebagHQ) live.SaddlebagHQ[k] = v;
                live.HasSaddlebagData = saved.HasSaddlebagData;
                live.SaddlebagSeen    = saved.SaddlebagSeen;
            }

            // The chest may be only partly loaded (pages not opened this session): keep the saved copy of every page the game didn't load.
            if (saved.FCChestPages.Count > 0)
            {
                foreach (var (page, items) in saved.FCChestPages)
                    if (live.FCChestPages.TryAdd(page, [.. items]) && saved.FCChestPageSeen.TryGetValue(page, out var seen))
                        live.FCChestPageSeen[page] = seen;
                live.RebuildFCChest();
                live.HasFCData = true;
            }
            else if (!live.HasFCData && saved.HasFCData)
            {
                foreach (var (k, v) in saved.FCChest)   live.FCChest[k]   = v;
                foreach (var (k, v) in saved.FCChestHQ) live.FCChestHQ[k] = v;
                foreach (var (k, v) in saved.FCChestSlots) live.FCChestSlots[k] = [.. v];
                live.HasFCData = true;
            }
        }

        SaveSnapshot(live);
        return live.Only(Settings.IncludeSaddlebag, Settings.IncludeRetainers, Settings.IncludeFCChest);
    }

    /// <summary>The character's retainers by id, with their current names; null while the game hasn't loaded the list.</summary>
    private static unsafe Dictionary<ulong, string>? CurrentRetainers()
    {
        var retMgr = RetainerManager.Instance();
        if (retMgr == null) return null;

        var count = retMgr->GetRetainerCount();
        if (count == 0) return null;

        var result = new Dictionary<ulong, string>();
        for (uint i = 0; i < count && i < 10; i++)
        {
            var retainer = retMgr->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0) continue;
            result[retainer->RetainerId] = RetainerName(retainer, i);
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>A retainer's name (a Span&lt;byte&gt; with a null terminator, decoded as UTF-8).</summary>
    private static unsafe string RetainerName(RetainerManager.Retainer* retainer, uint sortedIndex)
    {
        var nameSpan = retainer->Name;
        var nullIdx  = nameSpan.IndexOf((byte)0);
        var name     = Encoding.UTF8.GetString(nullIdx >= 0 ? nameSpan[..nullIdx] : nameSpan);
        return string.IsNullOrWhiteSpace(name) ? CraftLoc.Fmt("loc.retainern", sortedIndex + 1) : name;
    }

    // ── Snapshot DTO (for disk serialization) ─────────────────────────────────

    private sealed class InventorySnapshotData
    {
        public string                 PlayerName      { get; set; } = string.Empty;
        public bool                   HasRetainerData { get; set; }
        public bool                   HasFCData       { get; set; }
        public bool                   HasSaddlebagData { get; set; }
        public Dictionary<uint, int>  Player          { get; set; } = [];
        public Dictionary<uint, int>  PlayerHQ        { get; set; } = [];
        public Dictionary<uint, int>  Saddlebag       { get; set; } = [];
        public Dictionary<uint, int>  SaddlebagHQ     { get; set; } = [];
        public Dictionary<uint, int>  Retainer        { get; set; } = [];
        public Dictionary<uint, int>  RetainerHQ      { get; set; } = [];
        public Dictionary<uint, int>  FCChest         { get; set; } = [];
        public Dictionary<uint, int>  FCChestHQ       { get; set; } = [];
        public Dictionary<uint, List<int[]>> FCChestSlots { get; set; } = [];
        public Dictionary<int, List<long[]>> FCChestPages { get; set; } = [];
        public List<RetainerDetail>   RetainerDetails { get; set; } = [];
        public DateTime               SaddlebagSeen   { get; set; }
        public Dictionary<int, DateTime> FCChestPageSeen { get; set; } = [];

        public sealed class RetainerDetail
        {
            public ulong                 Id      { get; set; }
            public string                Name    { get; set; } = string.Empty;
            public DateTime              Seen    { get; set; }
            public Dictionary<uint, int> Items   { get; set; } = [];
            public Dictionary<uint, int> ItemsHQ { get; set; } = [];
        }

        public static InventorySnapshotData From(InventorySnapshot s) => new()
        {
            PlayerName      = s.PlayerName,
            HasRetainerData = s.HasRetainerData,
            HasFCData       = s.HasFCData,
            HasSaddlebagData = s.HasSaddlebagData,
            Player          = new(s.Player),
            PlayerHQ        = new(s.PlayerHQ),
            Saddlebag       = new(s.Saddlebag),
            SaddlebagHQ     = new(s.SaddlebagHQ),
            Retainer        = new(s.Retainer),
            RetainerHQ      = new(s.RetainerHQ),
            FCChest         = new(s.FCChest),
            FCChestHQ       = new(s.FCChestHQ),
            FCChestSlots    = s.FCChestSlots.ToDictionary(kv => kv.Key, kv => kv.Value.Select(t => new[] { t.Tab, t.Slot }).ToList()),
            FCChestPages    = s.FCChestPages.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => new long[] { e.Slot, e.Id, e.Qty, e.Hq ? 1 : 0 }).ToList()),
            RetainerDetails = s.RetainerDetails
                .Select(r => new RetainerDetail { Id = r.Id, Name = r.Name, Seen = r.Seen, Items = new(r.Items), ItemsHQ = new(r.ItemsHQ) })
                .ToList(),
            SaddlebagSeen   = s.SaddlebagSeen,
            FCChestPageSeen = new(s.FCChestPageSeen),
        };

        public InventorySnapshot ToSnapshot()
        {
            var s = new InventorySnapshot
            {
                PlayerName      = PlayerName,
                HasRetainerData = HasRetainerData,
                HasFCData       = HasFCData,
                HasSaddlebagData = HasSaddlebagData,
                SaddlebagSeen   = SaddlebagSeen,
            };
            foreach (var (k, v) in FCChestPageSeen) s.FCChestPageSeen[k] = v;
            foreach (var (k, v) in Player)      s.Player[k]      = v;
            foreach (var (k, v) in PlayerHQ)    s.PlayerHQ[k]    = v;
            foreach (var (k, v) in Saddlebag)   s.Saddlebag[k]   = v;
            foreach (var (k, v) in SaddlebagHQ) s.SaddlebagHQ[k] = v;
            foreach (var (k, v) in Retainer)    s.Retainer[k]    = v;
            foreach (var (k, v) in RetainerHQ)  s.RetainerHQ[k]  = v;
            foreach (var (k, v) in FCChest)     s.FCChest[k]     = v;
            foreach (var (k, v) in FCChestHQ)   s.FCChestHQ[k]   = v;
            foreach (var (k, v) in FCChestSlots) s.FCChestSlots[k] = v.Where(a => a.Length == 2).Select(a => (a[0], a[1])).ToList();
            foreach (var (page, items) in FCChestPages)
                s.FCChestPages[page] = items.Where(a => a.Length == 4).Select(a => ((int)a[0], (uint)a[1], (int)a[2], a[3] != 0)).ToList();
            foreach (var r in RetainerDetails)
            {
                var retainer = new RetainerData { Id = r.Id, Name = r.Name, Seen = r.Seen };
                foreach (var (k, v) in r.Items)   retainer.Items[k]   = v;
                foreach (var (k, v) in r.ItemsHQ) retainer.ItemsHQ[k] = v;
                s.RetainerDetails.Add(retainer);
            }
            return s;
        }
    }

    // Any settings change re-scans the window with the new options.
    protected override void OnSettingsChanged() => _addon?.RequestRefresh();

    public override TweakButton? OpenButton => new(TMain("Open"), TMain("Open.Help"), () => _addon?.Toggle());

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, OpenHelp);

    protected override IReadOnlyList<string> CommandNames => ["/gcm", "/gmaterials"];

    protected override void OnCommand() => _addon?.Toggle();

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(TMain("Settings"), null, ToggleSettings),
    ];

    /// <summary>Opens the settings window, or closes it if it is open.</summary>
    internal void ToggleSettings()
    {
        if (_settingsAddon is not { } window) return;
        if (window.IsOpen) window.Close(); else window.Open();
    }

    /// <summary>Opens the help of the window, or closes it if it is open. It belongs to the tweak, so it is closed with it.</summary>
    internal void OpenHelp()
    {
        _helpAddon ??= new CraftingHelpAddon
        {
            InternalName = "GtkCraftMatHelp",
            Title        = CraftLoc.Get("help.title"),
            Size         = new System.Numerics.Vector2(780f, 620f),
        };
        _helpAddon.Toggle();
    }

    /// <summary>Closes the help if it is open (the main window closed).</summary>
    internal void CloseHelp()
    {
        if (_helpAddon is { IsOpen: true } help) help.Close();
    }

    /// <summary>Settings window title, in the language chosen for this tweak.</summary>
    public string SettingsTitle => $"{Text("Name")} — {Text("Settings")}";

    private CraftingSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkCraftingSettings",
        Title           = SettingsTitle,
        Size            = new System.Numerics.Vector2(520f, 580f),
        Tweak           = this,
        RespectCloseAll = false,
    };

    // ── Access for the native windows ─────────────────────────────────────────

    internal Options Current => Settings;

    internal void SaveCurrent() => SaveSettings();

    /// <summary>Text of this tweak in the language chosen for its windows, not the plugin language.</summary>
    internal string Text(string key) => T(key);

    /// <summary>Something unexpected happened in the window: the tweak is disabled safely.</summary>
    internal void Fail(Exception exception) => ReportFailure(exception);

    /// <summary>Stores the window's filters (in <see cref="Current"/>) without re-scanning the inventories.</summary>
    internal void SaveFilters() => SaveSettings(notify: false);

    /// <summary>The player's level in the crafter class of a recipe's craft type (0 if unknown).</summary>
    public int CrafterLevel(uint craftTypeId)
    {
        // CraftType 0-7 (Woodworking … Cooking) are the classes Carpenter … Culinarian, ClassJob 8-15.
        if (!Svc.PlayerState.IsLoaded || craftTypeId > 7) return 0;
        return Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(8 + craftTypeId, out var job)
            ? Svc.PlayerState.GetClassJobLevel(job)
            : 0;
    }

    // ── Inventory scanning ────────────────────────────────────────────────────

    /// <summary>Reads every source (the settings only decide what is shown, see <see cref="InventorySnapshot.Only"/>).</summary>
    private unsafe InventorySnapshot ScanInventories()
    {
        var snap = new InventorySnapshot();
        var mgr  = InventoryManager.Instance();
        if (mgr == null) return snap;

        snap.PlayerName = Svc.PlayerState.IsLoaded ? Svc.PlayerState.CharacterName : string.Empty;

        ScanContainers(snap.Player, snap.PlayerHQ, mgr, false,
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.Crystals);

        snap.HasSaddlebagData = ScanContainers(snap.Saddlebag, snap.SaddlebagHQ, mgr, requireLoaded: true,
            InventoryType.SaddleBag1, InventoryType.SaddleBag2,
            InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2);
        if (snap.HasSaddlebagData) snap.SaddlebagSeen = DateTime.UtcNow;

        snap.HasRetainerData = ScanRetainers(snap.Retainer, snap.RetainerHQ, snap.RetainerDetails, mgr);
        snap.HasFCData       = ScanFCChest(snap, mgr);

        return snap;
    }

    // The FC chest containers exist in memory even when the chest was never opened, so only
    // count pages the game reports as loaded; otherwise an empty scan would wipe the saved data.
    // Pages seen with items, this session and for this character (cleared when the character changes).
    private readonly HashSet<int> _seenFCPages = [];

    private unsafe bool ScanFCChest(InventorySnapshot snap, InventoryManager* mgr)
    {
        (InventoryType Type, int Page)[] pages =
        [
            (InventoryType.FreeCompanyCrystals, 0),
            (InventoryType.FreeCompanyPage1, 1), (InventoryType.FreeCompanyPage2, 2),
            (InventoryType.FreeCompanyPage3, 3), (InventoryType.FreeCompanyPage4, 4),
            (InventoryType.FreeCompanyPage5, 5),
        ];

        var anyLoaded = false;
        foreach (var (type, page) in pages)
        {
            var container = mgr->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            var items = new List<(int, uint, int, bool)>();
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                items.Add((i + 1, slot->ItemId, (int)slot->Quantity, (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0));
            }
            // The game reports pages as loaded but empty after a restart and when the chest opens without visiting a tab.
            // An empty page only counts once we've seen it with items this session (i.e. it was really emptied).
            if (items.Count > 0) _seenFCPages.Add(page);
            else if (!_seenFCPages.Contains(page)) continue;

            anyLoaded = true;
            snap.FCChestPages[page]    = items;
            snap.FCChestPageSeen[page] = DateTime.UtcNow;
        }
        snap.RebuildFCChest();
        return anyLoaded;
    }

    // requireLoaded: skip containers the game reports as not loaded (e.g. an unopened saddlebag).
    // Returns true if at least one container was read.
    private static unsafe bool ScanContainers(
        Dictionary<uint, int> target, Dictionary<uint, int>? hqTarget, InventoryManager* mgr, bool requireLoaded, params InventoryType[] types)
    {
        var anyLoaded = false;
        foreach (var type in types)
        {
            var container = mgr->GetInventoryContainer(type);
            if (container == null || (requireLoaded && !container->IsLoaded)) continue;

            anyLoaded = true;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;

                var id = slot->ItemId;
                target[id] = target.GetValueOrDefault(id) + (int)slot->Quantity;

                if (hqTarget is not null && (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0)
                    hqTarget[id] = hqTarget.GetValueOrDefault(id) + (int)slot->Quantity;
            }
        }
        return anyLoaded;
    }

    // Right after picking another retainer its pages may still hold the previous one's items. They are only read
    // once the same retainer has been open for a moment.
    private static readonly TimeSpan RetainerSettleTime = TimeSpan.FromSeconds(1.5);
    private ulong    _activeRetainerId;
    private DateTime _activeRetainerSince;

    /// <summary>Whether the open retainer has been open long enough for its pages to be its own.</summary>
    private bool RetainerSettled(ulong retainerId)
    {
        if (retainerId != _activeRetainerId)
        {
            _activeRetainerId    = retainerId;
            _activeRetainerSince = DateTime.UtcNow;
        }
        return DateTime.UtcNow - _activeRetainerSince >= RetainerSettleTime;
    }

    // The game keeps ONE retainer's inventory in memory (RetainerPage1-7 + its crystals): the one opened last.
    // Reading it for any other retainer would attribute the wrong items, so only the open retainer is read.
    // Returns true if its data was read.
    private unsafe bool ScanRetainers(
        Dictionary<uint, int> aggregate, Dictionary<uint, int> aggregateHQ,
        List<RetainerData> details,
        InventoryManager* mgr)
    {
        var retMgr = RetainerManager.Instance();
        if (retMgr == null) return false;

        var activeRetainer = retMgr->GetActiveRetainer();
        if (activeRetainer == null) { _activeRetainerId = 0; return false; }
        var activeId = activeRetainer->RetainerId;
        if (!RetainerSettled(activeId)) return false;

        var count = retMgr->GetRetainerCount();
        for (uint ri = 0; ri < count && ri < 10; ri++)
        {
            var retainer = retMgr->GetRetainerBySortedIndex(ri);
            if (retainer == null || !retainer->Available || retainer->RetainerId != activeId) continue;

            var data = new RetainerData { Id = activeId, Name = RetainerName(retainer, ri), Seen = DateTime.UtcNow };
            var retainerItems   = data.Items;
            var retainerItemsHQ = data.ItemsHQ;
            var anyPage         = false;

            for (var page = 0; page <= 7; page++)
            {
                var type = page < 7
                    ? (InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page)
                    : InventoryType.RetainerCrystals;
                var container = mgr->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) continue;
                anyPage |= page < 7;

                for (var s = 0; s < container->Size; s++)
                {
                    var slot = container->GetInventorySlot(s);
                    if (slot == null || slot->ItemId == 0) continue;

                    var id = slot->ItemId;
                    retainerItems[id] = retainerItems.GetValueOrDefault(id) + (int)slot->Quantity;
                    aggregate[id]     = aggregate    .GetValueOrDefault(id) + (int)slot->Quantity;

                    if ((slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0)
                    {
                        retainerItemsHQ[id] = retainerItemsHQ.GetValueOrDefault(id) + (int)slot->Quantity;
                        aggregateHQ[id]     = aggregateHQ    .GetValueOrDefault(id) + (int)slot->Quantity;
                    }
                }
            }

            // Keep empty retainers too, so a retainer that was emptied replaces its stale saved copy.
            if (anyPage)
                details.Add(data);
            break;
        }

        return details.Count > 0;
    }

    // ── Recipe analysis ───────────────────────────────────────────────────────

    /// <summary>What a recipe is, independent of the inventory: read once per game-data language.</summary>
    private sealed record RecipeInfo(
        uint RecipeId, uint ItemId, string Name, uint IconId, ushort ResultAmount,
        string CraftClass, uint CraftTypeId, uint[] ForJobs, int ItemLevel, int CraftLevel,
        List<(uint Id, string Name, uint Icon, int Amount)> Ingredients);

    private ClientLanguage?                  _recipesLanguage;
    private List<RecipeInfo>                 _recipes    = [];
    private Dictionary<uint, RecipeInfo>     _recipeById = [];

    /// <summary>
    /// The recipes the window lists, the ones that count for the crafting log (see <see cref="LogRecipeIds"/>), with their
    /// ingredients. Built the first time, and again if the client language changes.
    /// </summary>
    private List<RecipeInfo> Recipes()
    {
        var lang = DataLanguage;
        if (_recipesLanguage == lang) return _recipes;

        // Load every sheet in the same language so item/recipe/category names (and the RowRefs they resolve)
        // all come back localized. RowRef.Value follows the owning sheet.
        var recipeSheet = Svc.Data.GetExcelSheet<LuminaRecipe>(lang);
        if (recipeSheet == null) return [];

        var craftTypeNames = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.CraftType>(lang)
            ?.ToDictionary(ct => ct.RowId, ct => ct.Name.ExtractText())
            ?? [];

        var logRecipeIds = LogRecipeIds();
        var list = new List<RecipeInfo>();

        foreach (var recipe in recipeSheet)
        {
            if (!logRecipeIds.Contains(recipe.RowId) || recipe.ItemResult.RowId == 0 || !recipe.ItemResult.IsValid)
                continue;

            var resultItem  = recipe.ItemResult.Value;
            var ingredients = BuildIngredients(recipe);
            if (ingredients.Count == 0) continue;

            list.Add(new RecipeInfo(
                recipe.RowId,
                recipe.ItemResult.RowId,
                resultItem.Name.ExtractText(),
                resultItem.Icon,
                recipe.AmountResult,
                craftTypeNames.GetValueOrDefault(recipe.CraftType.RowId, string.Empty),
                recipe.CraftType.RowId,
                ForJobsOf(resultItem),
                (int)resultItem.LevelItem.RowId,
                recipe.RecipeLevelTable.IsValid ? recipe.RecipeLevelTable.Value.ClassJobLevel : 0,
                ingredients));
        }

        _recipesLanguage = lang;
        _recipes         = list;
        _recipeById      = list.ToDictionary(r => r.RecipeId);
        _otherRecipes.Clear();
        return list;
    }

    // ClassJobCategory rows of the Disciples of the Hand and of the Land, as ClassJob tells each job's.
    private const uint HandCategory = 33;
    private const uint LandCategory = 32;

    /// <summary>
    /// A Disciple of the Hand or Land: its ClassJob row, its name in the language of the game, whether it crafts, its
    /// craft (the CraftType row, for a crafter) and its column of ClassJobCategory (one boolean per job, named after
    /// its abbreviation).
    /// </summary>
    public sealed record HandOrLandJob(uint Id, string Name, bool Crafter, uint CraftTypeId, PropertyInfo Column);

    private static HandOrLandJob[]? _handAndLand;
    private static ClientLanguage? _handAndLandLanguage;

    /// <summary>The Disciples of the Hand, then of the Land, in the game's order.</summary>
    public static HandOrLandJob[] HandAndLandJobs()
    {
        var lang = DataLanguage;
        if (_handAndLand is not null && _handAndLandLanguage == lang) return _handAndLand;

        var local = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>(lang);
        var jobs  = new List<HandOrLandJob>();
        foreach (var job in Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>(ClientLanguage.English))
        {
            var kind = job.ClassJobCategory.RowId;
            if (kind is not (HandCategory or LandCategory) || job.DohDolJobIndex < 0) continue;

            if (typeof(Lumina.Excel.Sheets.ClassJobCategory).GetProperty(job.Abbreviation.ExtractText()) is not { } column
                || column.PropertyType != typeof(bool))
                continue;

            var name = local.TryGetRow(job.RowId, out var row) ? row.Name.ExtractText() : string.Empty;
            if (name.Length == 0) continue;

            // A crafter's index among the Disciples of the Hand is its craft: 0 Woodworking (Carpenter) … 7 Cooking.
            jobs.Add(new HandOrLandJob(job.RowId, char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..],
                kind == HandCategory, (uint)job.DohDolJobIndex, column));
        }

        _handAndLandLanguage = lang;
        return _handAndLand = [.. jobs.OrderBy(job => job.Crafter ? 0 : 1).ThenBy(job => job.Id)];
    }

    /// <summary>The Disciples of the Hand and Land that can equip an item; none for what is not worn.</summary>
    private static uint[] ForJobsOf(LuminaItem item)
    {
        if (item.ClassJobCategory.RowId == 0 || !item.ClassJobCategory.IsValid) return [];

        var category = item.ClassJobCategory.Value;
        return [.. HandAndLandJobs().Where(job => job.Column.GetValue(category) is true).Select(job => job.Id)];
    }

    public List<CraftableEntry> Analyze(InventorySnapshot snap)
    {
        var recipes = Recipes();
        var results = new List<CraftableEntry>(recipes.Count);

        foreach (var info in recipes)
        {
            var missingCount = 0;
            var allInPlayer  = true;
            var checks       = Checks(info.Ingredients, snap);

            foreach (var check in checks)
            {
                if (!check.Fulfilled)
                    missingCount++;
                else if (check.InPlayer < check.Required)
                    allInPlayer = false;
            }

            // "Near complete" only if you have at least as many fulfilled ingredient types
            // as missing ones — avoids showing recipes where you have almost nothing.
            var fulfilledCount = checks.Count - missingCount;
            var status = missingCount switch
            {
                0 when allInPlayer  => CraftStatus.Ready,
                0                   => CraftStatus.ReadyElsewhere,
                _ when missingCount <= Settings.NearCompleteMissing && fulfilledCount >= missingCount
                                    => CraftStatus.NearComplete,
                _                   => CraftStatus.NotReady,
            };

            results.Add(new CraftableEntry
            {
                RecipeId     = info.RecipeId,
                ItemId       = info.ItemId,
                Name         = info.Name,
                IconId       = info.IconId,
                ResultAmount = info.ResultAmount,
                CraftClass   = info.CraftClass,
                CraftTypeId  = info.CraftTypeId,
                ForJobs      = info.ForJobs,
                Status       = status,
                MissingCount = missingCount,
                IsCrafted    = QuestManager.IsRecipeComplete(info.RecipeId),
                ItemLevel    = info.ItemLevel,
                CraftLevel   = info.CraftLevel,
                Ingredients  = checks,
            });
        }

        return results;
    }

    private static List<IngredientCheck> Checks(List<(uint Id, string Name, uint Icon, int Amount)> ingredients, InventorySnapshot snap)
        => ingredients.Select(i => Check(new Need(i.Id, i.Name, i.Icon, i.Amount), snap)).ToList();

    /// <summary>How much of a needed item is in the main bags and elsewhere.</summary>
    internal static IngredientCheck Check(Need need, InventorySnapshot snap)
    {
        var inPlayer = snap.Get(need.ItemId, InventorySource.Player);
        var inOther  = snap.Get(need.ItemId, InventorySource.Saddlebag)
                     + snap.Get(need.ItemId, InventorySource.Retainer)
                     + snap.Get(need.ItemId, InventorySource.FreeCompany);
        return new IngredientCheck(need.ItemId, need.Name, need.IconId, need.Amount, inPlayer, inOther);
    }

    private HashSet<uint>? _logRecipeIds;

    // NotebookDivisionCategory of the "Housing" group of the Special Recipes.
    private const uint HousingCategory = 2;

    /// <summary>
    /// The recipes that count for the crafting log: the level-based recipes of every class and, of the Special Recipes,
    /// the Housing ones. Master recipes and the other special groups (sidequests, deliveries, collectables...) don't.
    /// </summary>
    /// <remarks>
    /// RecipeNotebookList has one row per class and log division: <c>craftType * 40 + division</c> for the level-based
    /// divisions (0-39), <c>1000 + (division - 1000) * 8 + craftType</c> for the rest, whose NotebookDivision row says
    /// which group of the log it is under.
    /// </remarks>
    private HashSet<uint> LogRecipeIds()
    {
        if (_logRecipeIds is not null) return _logRecipeIds;

        var lists     = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.RecipeNotebookList>();
        var divisions = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.NotebookDivision>();

        var set = new HashSet<uint>();
        foreach (var list in lists)
        {
            var counts = list.RowId < 1000
                || (divisions.TryGetRow(1000 + (list.RowId - 1000) / 8, out var division)
                    && division.NotebookDivisionCategory.RowId == HousingCategory);
            if (!counts) continue;

            foreach (var r in list.Recipe)
                if (r.RowId != 0 && r.IsValid) set.Add(r.RowId);
        }

        return _logRecipeIds = set;
    }

    public int NearCompleteMissing => Settings.NearCompleteMissing;

    // ── Sub-recipe lookup (for expandable nested ingredients) ──────────────────

    private Dictionary<uint, uint>? _recipeByResult; // result itemId -> recipe RowId (language-independent)

    /// <summary>
    /// The recipe of each craftable item. Several crafters can make the same item: the one in the crafting log
    /// is preferred over master and special recipes, then the first one.
    /// </summary>
    private Dictionary<uint, uint> RecipeByResult()
    {
        if (_recipeByResult is not null) return _recipeByResult;

        var map   = new Dictionary<uint, uint>();
        var log   = LogRecipeIds();
        var sheet = Svc.Data.GetExcelSheet<LuminaRecipe>();
        if (sheet is not null)
            foreach (var r in sheet)
            {
                if (r.RowId == 0 || r.ItemResult.RowId == 0) continue;

                if (!map.TryGetValue(r.ItemResult.RowId, out var current)
                    || (log.Contains(r.RowId) && !log.Contains(current)))
                    map[r.ItemResult.RowId] = r.RowId;
            }

        return _recipeByResult = map;
    }

    /// <summary>Whether an item can itself be crafted (has a recipe producing it).</summary>
    public bool IsCraftable(uint itemId) => RecipeByResult().ContainsKey(itemId);

    /// <summary>Returns the recipe RowId that produces <paramref name="itemId"/>, or 0 if none.</summary>
    public uint GetRecipeId(uint itemId) => RecipeByResult().GetValueOrDefault(itemId);

    /// <summary>Ingredient availability for the recipe that produces <paramref name="itemId"/> (empty if none).</summary>
    public List<IngredientCheck> SubIngredients(uint itemId, InventorySnapshot snap)
    {
        if (!RecipeByResult().TryGetValue(itemId, out var recipeId)) return [];
        return RecipeParts(recipeId) is { } parts ? Checks(parts.Ingredients, snap) : [];
    }

    // Recipes the window doesn't list (not in the crafting log) that an ingredient is made with, read when first needed.
    private readonly Dictionary<uint, (ushort ResultAmount, List<(uint Id, string Name, uint Icon, int Amount)> Ingredients)> _otherRecipes = [];

    /// <summary>How many items a recipe makes and its ingredients, for any recipe; null if it doesn't exist.</summary>
    private (ushort ResultAmount, List<(uint Id, string Name, uint Icon, int Amount)> Ingredients)? RecipeParts(uint recipeId)
    {
        Recipes();
        if (_recipeById.TryGetValue(recipeId, out var info)) return (info.ResultAmount, info.Ingredients);
        if (_otherRecipes.TryGetValue(recipeId, out var parts)) return parts;

        if (!Svc.Data.GetExcelSheet<LuminaRecipe>(DataLanguage).TryGetRow(recipeId, out var recipe)) return null;
        return _otherRecipes[recipeId] = (recipe.AmountResult, BuildIngredients(recipe));
    }

    /// <summary>Deepest level of sub-recipes followed (same as the ingredient tree of the window).</summary>
    public const int MaxRecipeDepth = CraftMath.MaxDepth;

    /// <summary>
    /// The base materials one craft of a recipe needs, with every craftable ingredient you don't have broken down into
    /// what it is made of (as many crafts of it as are needed, taking into account how many each craft makes). What you
    /// already own is used first, intermediate items included, and the leftovers of a sub-craft serve the next one.
    /// Each entry's <see cref="IngredientCheck.Required"/> is the total needed; the ones still missing come first.
    /// </summary>
    public List<IngredientCheck> ShoppingList(uint recipeId, InventorySnapshot snap)
        => CraftMath.BaseMaterials([new CartItem { RecipeId = recipeId }], CraftData(snap), deductOwned: true)
            .Select(need => Check(need, snap))
            .OrderBy(check => check.Fulfilled)
            .ThenBy(check => check.Name)
            .ToList();

    /// <summary>The recipes of the game and what the snapshot holds, as the crafting calculations read them.</summary>
    internal ICraftData CraftData(InventorySnapshot snap) => new CraftDataSource(this, snap);

    private sealed class CraftDataSource(CraftingMaterials tweak, InventorySnapshot snap) : ICraftData
    {
        private readonly Dictionary<uint, CraftRecipe?> _recipes = [];

        public CraftRecipe? Recipe(uint recipeId)
        {
            if (_recipes.TryGetValue(recipeId, out var known)) return known;
            return _recipes[recipeId] = tweak.RecipeParts(recipeId) is { } parts
                ? new CraftRecipe(parts.ResultAmount, parts.Ingredients.Select(i => new CraftPart(i.Id, i.Name, i.Icon, i.Amount)).ToList())
                : null;
        }

        public uint RecipeFor(uint itemId) => tweak.GetRecipeId(itemId);
        public int  Owned(uint itemId)     => snap.GetTotal(itemId);
    }

    /// <summary>Drops from My List the recipes the game no longer has and fixes counts out of range (a hand-edited or old config).</summary>
    private void SanitizeCart()
    {
        // A hand-edited "Cart": null reads as null, not as an empty list.
        Settings.Cart ??= [];
        if (Settings.Cart.Count == 0) return;

        var before = Settings.Cart.Select(i => i is null ? (0u, 0) : (i.RecipeId, i.Count)).ToList();
        CraftCart.Sanitize(Settings.Cart, id => RecipeParts(id) is not null);
        if (!before.SequenceEqual(Settings.Cart.Select(i => (i.RecipeId, i.Count))))
            SaveSettings(notify: false);
    }

    private static List<(uint Id, string Name, uint Icon, int Amount)> BuildIngredients(LuminaRecipe recipe)
    {
        var list = new List<(uint, string, uint, int)>(8);

        foreach (var (itemRef, amount) in recipe.Ingredient.Zip(recipe.AmountIngredient))
        {
            if (amount <= 0 || itemRef.RowId == 0 || !itemRef.IsValid) continue;
            var item = itemRef.Value;
            list.Add((itemRef.RowId, item.Name.ExtractText(), item.Icon, amount));
        }

        return list;
    }
}
