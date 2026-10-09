using System.Text;
using System.Text.Json;
using Dalamud.Game;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using GoblinTweaks.Core;
using GoblinTweaks.Services;
using GoblinTweaks.UI;
using GoblinTweaks.Localization;
using LuminaItem = Lumina.Excel.Sheets.Item;
using LuminaRecipe = Lumina.Excel.Sheets.Recipe;

namespace GoblinTweaks.Tweaks;

// ── Data models ──────────────────────────────────────────────────────────────

public enum InventorySource { Player, Saddlebag, Retainer, FreeCompany }

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
    /// <summary>Where each item sits in the FC chest, as 1-based (tab, slot) pairs.</summary>
    public readonly Dictionary<uint, List<(int Tab, int Slot)>>        FCChestSlots    = new();
    public readonly List<(string Name, Dictionary<uint, int> Items, Dictionary<uint, int> ItemsHQ)> RetainerDetails = [];
    /// <summary>Raw contents per FC chest page (1-based), so pages can be kept or replaced one by one. The aggregates above derive from it.</summary>
    public readonly Dictionary<int, List<(int Slot, uint Id, int Qty, bool Hq)>> FCChestPages = new();

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

    /// <summary>Returns human-readable location strings for tooltip display, including retainer names and HQ counts.</summary>
    public IEnumerable<(string Location, int Total, int Hq)> Locations(uint itemId)
    {
        var q  = Player.GetValueOrDefault(itemId);
        var hq = PlayerHQ.GetValueOrDefault(itemId);
        if (q > 0)
            yield return (string.IsNullOrEmpty(PlayerName) ? CraftLoc.Get("loc.yourbags") : CraftLoc.Fmt("loc.bagsof", PlayerName), q, hq);

        q  = Saddlebag.GetValueOrDefault(itemId);
        hq = SaddlebagHQ.GetValueOrDefault(itemId);
        if (q > 0) yield return (CraftLoc.Get("loc.saddlebag"), q, hq);

        if (RetainerDetails.Count > 0)
        {
            foreach (var (name, items, itemsHQ) in RetainerDetails)
            {
                q  = items.GetValueOrDefault(itemId);
                hq = itemsHQ.GetValueOrDefault(itemId);
                if (q > 0) yield return (CraftLoc.Fmt("loc.bagsof", name), q, hq);
            }
        }
        else
        {
            q  = Retainer.GetValueOrDefault(itemId);
            hq = RetainerHQ.GetValueOrDefault(itemId);
            if (q > 0) yield return (CraftLoc.Get("loc.retainers"), q, hq);
        }

        q  = FCChest.GetValueOrDefault(itemId);
        hq = FCChestHQ.GetValueOrDefault(itemId);
        if (q > 0)
        {
            var where = string.Empty;
            if (FCChestSlots.TryGetValue(itemId, out var slots) && slots.Count > 0)
                where = " (" + string.Join(", ", slots.Take(4).Select(t => $"{t.Tab}.{t.Slot}")) + (slots.Count > 4 ? ", ..." : "") + ")";
            yield return (CraftLoc.Get("loc.fcchest") + where, q, hq);
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
    public string                CategoryName    = string.Empty;
    public CraftStatus           Status;
    public int                   MissingCount;
    public bool                  IsCrafted;
    /// <summary>
    /// True for basic recipes and special-category housing recipes — these count toward
    /// the in-game crafting log. False for master recipes (SecretRecipeBook) and other
    /// special-category recipes (seasonal, event, etc.).
    /// </summary>
    public bool                  IsLogRecipe;
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
        /// <summary>Outline the FC chest tabs/slots that hold the selected recipe's ingredients.</summary>
        public bool HighlightFCChest   { get; set; } = true;
        /// <summary>Max missing ingredient types before a recipe moves from NearComplete → NotReady.</summary>
        public int  NearCompleteMissing { get; set; } = 2;
        /// <summary>Game-data language for item/recipe names ("" = follow GoblinTweaks). Independent of the UI language.</summary>
        public string DataLanguage { get; set; } = string.Empty;
    }

    // Language choices for the window's item/recipe names. "" follows the GoblinTweaks language.
    private static readonly (string Code, string Label)[] DataLanguages =
    [
        ("",   ""),          // label resolved from Loc at draw time
        ("en", "English"),
        ("es", "Español"),
    ];

    private CraftingMaterialsAddon?     _addon;
    private UniversalisService?         _universalis;
    private InventorySnapshot?          _cachedSnapshot;

    private CraftingSettingsAddon?      _settingsAddon;
    private readonly FCChestHighlighter _fcHighlighter = new();

    public UniversalisService      Universalis     => _universalis!;
    /// <summary>Last known inventory snapshot, loaded from disk at startup and updated after each scan.</summary>
    public InventorySnapshot?      CachedSnapshot  => _cachedSnapshot;
    public uint WorldId => Svc.PlayerState.IsLoaded ? Svc.PlayerState.HomeWorld.RowId : 0u;

    /// <summary>Language code for the window's own UI strings ("" = follow GoblinTweaks).</summary>
    public string WindowUiLang => string.IsNullOrEmpty(Settings.DataLanguage) ? Loc.CurrentLanguage : Settings.DataLanguage;

    /// <summary>Game-data language used for item/recipe names in the window.</summary>
    public ClientLanguage DataLanguage => WindowUiLang switch
    {
        _    => ClientLanguage.English, // "en", "es" and anything without game data
    };

    protected internal override void Enable()
    {
        _cachedSnapshot = LoadSnapshot();
        _universalis = new UniversalisService();
        CraftLoc.Lang = WindowUiLang;
        _addon = new CraftingMaterialsAddon
        {
            InternalName = "GtkCraftingMaterials",
            Title        = CraftLoc.Get("title"),
            Size         = new System.Numerics.Vector2(1180f, 740f),
            Tweak        = this,
            RespectCloseAll = false, // stay open when the game closes windows (chest, retainer, NPC talk)
        };

        Svc.ContextMenu.OnMenuOpened += OnMenuOpened;
        Svc.GameInventory.InventoryChanged += OnInventoryChanged;
        Svc.Framework.Update += OnCaptureUpdate;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,  "RetainerList",         OnRetainerUiClosed);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,    "SynthesisSimpleResult", OnSynthesisResult);

        _settingsAddon = NewSettingsAddon();

        _fcHighlighter.Source = () => Settings.HighlightFCChest && Settings.IncludeFCChest && _addon is { IsOpen: true } a
            ? a.FCChestMarks()
            : [];
        _fcHighlighter.Start();
    }

    protected internal override void Disable()
    {
        Svc.ContextMenu.OnMenuOpened -= OnMenuOpened;
        Svc.GameInventory.InventoryChanged -= OnInventoryChanged;
        Svc.Framework.Update -= OnCaptureUpdate;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize,  "RetainerList",         OnRetainerUiClosed);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup,    "SynthesisSimpleResult", OnSynthesisResult);

        _fcHighlighter.Stop();
        _fcHighlighter.Source = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;

        _addon?.Dispose();
        _addon = null;
        _universalis?.Dispose();
        _universalis = null;
    }

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
            Svc.Log.Error(ex, "CraftingMaterials: OnMenuOpened failed");
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

        var retMgr = RetainerManager.Instance();
        if (retMgr == null || retMgr->GetActiveRetainer() == null) { _captureDirty = false; return; }

        _captureDirty = false;
        _nextCapture  = DateTime.UtcNow.AddSeconds(2);
        try   { ScanAndMerge(); }
        catch (Exception ex) { Svc.Log.Warning(ex, "CraftingMaterials: background capture failed"); }
    }

    // Retainer inventory is cached when the RetainerList UI closes (after visiting the bell).
    private void OnRetainerUiClosed(AddonEvent type, AddonArgs args) =>
        _addon?.NotifyInventoryChanged();

    // Synthesis result popup appears → immediately refresh to update craft check marks.
    private void OnSynthesisResult(AddonEvent type, AddonArgs args) =>
        _addon?.RequestRefresh();

    // ── Snapshot persistence ──────────────────────────────────────────────────

    private static readonly JsonSerializerOptions SnapshotJson = new() { WriteIndented = false };

    private string SnapshotFilePath =>
        Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "CraftingSnapshot.json");

    public void SaveSnapshot(InventorySnapshot snap)
    {
        try
        {
            var path = SnapshotFilePath;
            var tmp  = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(InventorySnapshotData.From(snap), SnapshotJson));
            File.Move(tmp, path, true);
            _cachedSnapshot = snap;
        }
        catch (Exception ex) { Svc.Log.Warning(ex, "CraftingMaterials: could not save snapshot"); }
    }

    private InventorySnapshot? LoadSnapshot()
    {
        try
        {
            var path = SnapshotFilePath;
            if (!File.Exists(path)) return null;
            var data = JsonSerializer.Deserialize<InventorySnapshotData>(File.ReadAllText(path), SnapshotJson);
            return data?.ToSnapshot();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "CraftingMaterials: could not load snapshot");
            return null;
        }
    }

    /// <summary>
    /// Scans live inventories and merges persisted retainer/FC data when those sources
    /// are not yet available in memory (not visited this session). Saves the result.
    /// </summary>
    public InventorySnapshot ScanAndMerge()
    {
        var live = ScanInventories();

        if (_cachedSnapshot != null)
        {
            // Saved data of another character must not leak into this one.
            var sameCharacter = _cachedSnapshot.PlayerName == live.PlayerName;

            // Only one retainer is live at a time, so merge per retainer: live ones win, the rest come from the saved copy.
            if (Settings.IncludeRetainers && sameCharacter && _cachedSnapshot.HasRetainerData)
            {
                var liveNames = live.RetainerDetails.Select(r => r.Name).ToHashSet();
                foreach (var (name, items, hq) in _cachedSnapshot.RetainerDetails)
                    if (!liveNames.Contains(name))
                        live.RetainerDetails.Add((name, new Dictionary<uint, int>(items), new Dictionary<uint, int>(hq)));

                live.Retainer.Clear();
                live.RetainerHQ.Clear();
                foreach (var (_, items, hq) in live.RetainerDetails)
                {
                    foreach (var (k, v) in items) live.Retainer[k]   = live.Retainer.GetValueOrDefault(k)   + v;
                    foreach (var (k, v) in hq)    live.RetainerHQ[k] = live.RetainerHQ.GetValueOrDefault(k) + v;
                }
                live.HasRetainerData = live.RetainerDetails.Count > 0;
            }

            // The saddlebag is only in memory after opening it; keep the last known contents.
            if (Settings.IncludeSaddlebag && sameCharacter && !live.HasSaddlebagData)
            {
                foreach (var (k, v) in _cachedSnapshot.Saddlebag)   live.Saddlebag[k]   = v;
                foreach (var (k, v) in _cachedSnapshot.SaddlebagHQ) live.SaddlebagHQ[k] = v;
                live.HasSaddlebagData = _cachedSnapshot.HasSaddlebagData;
            }

            // The chest may be only partly loaded (pages not opened this session): keep the saved copy of every page the game didn't load.
            if (Settings.IncludeFCChest && _cachedSnapshot.FCChestPages.Count > 0)
            {
                foreach (var (page, items) in _cachedSnapshot.FCChestPages)
                    live.FCChestPages.TryAdd(page, [.. items]);
                live.RebuildFCChest();
                live.HasFCData = true;
            }
            else if (Settings.IncludeFCChest && !live.HasFCData && _cachedSnapshot.HasFCData)
            {
                foreach (var (k, v) in _cachedSnapshot.FCChest)   live.FCChest[k]   = v;
                foreach (var (k, v) in _cachedSnapshot.FCChestHQ) live.FCChestHQ[k] = v;
                foreach (var (k, v) in _cachedSnapshot.FCChestSlots) live.FCChestSlots[k] = [.. v];
                live.HasFCData = true;
            }
        }

        SaveSnapshot(live);
        return live;
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

        public sealed class RetainerDetail
        {
            public string                Name    { get; set; } = string.Empty;
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
                .Select(r => new RetainerDetail { Name = r.Name, Items = new(r.Items), ItemsHQ = new(r.ItemsHQ) })
                .ToList(),
        };

        public InventorySnapshot ToSnapshot()
        {
            var s = new InventorySnapshot
            {
                PlayerName      = PlayerName,
                HasRetainerData = HasRetainerData,
                HasFCData       = HasFCData,
                HasSaddlebagData = HasSaddlebagData,
            };
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
                s.RetainerDetails.Add((r.Name, new Dictionary<uint, int>(r.Items), new Dictionary<uint, int>(r.ItemsHQ)));
            return s;
        }
    }

    // Any settings change re-scans the window with the new options.
    protected override void OnSettingsChanged() => _addon?.RequestRefresh();

    public override TweakButton? OpenButton => new(T("Open"), T("Open.Help"), () => _addon?.Toggle());

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, () => _addon?.OpenHelp());

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

    /// <summary>Rebuilds the open settings window (its texts follow the language chosen in it).</summary>
    internal void RefreshSettingsWindow()
    {
        var old = _settingsAddon;
        if (old is not { IsOpen: true }) return;

        // A moment later, never from inside the click of its own drop-down.
        Svc.Framework.RunOnTick(() =>
        {
            old.Dispose();
            if (_settingsAddon != old) return;
            _settingsAddon = NewSettingsAddon();
            Svc.Framework.RunOnTick(() => _settingsAddon?.Open(), delayTicks: 6);
        }, delayTicks: 2);
    }

    // ── Access for the native settings window ─────────────────────────────────

    internal Options Current => Settings;

    internal void SaveCurrent() => SaveSettings();

    /// <summary>Text of this tweak in the language chosen for it (the window language), not the plugin language.</summary>
    internal string Text(string key) => Loc.GetIn(WindowUiLang, $"Tweaks.{Id}.{key}");

    /// <summary>Language choices for the item and recipe names; the first follows the GoblinTweaks language.</summary>
    internal IReadOnlyList<(string Code, string Text)> LanguageChoices
        => DataLanguages.Select(l => (l.Code, l.Code.Length == 0 ? Text("Language.Same") : l.Label)).ToList();

    // ── Inventory scanning ────────────────────────────────────────────────────

    public unsafe InventorySnapshot ScanInventories()
    {
        var snap = new InventorySnapshot();
        var mgr  = InventoryManager.Instance();
        if (mgr == null) return snap;

        snap.PlayerName = Svc.PlayerState.IsLoaded ? Svc.PlayerState.CharacterName : string.Empty;

        ScanContainers(snap.Player, snap.PlayerHQ, mgr,
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.Crystals);

        if (Settings.IncludeSaddlebag)
            snap.HasSaddlebagData = ScanContainers(snap.Saddlebag, snap.SaddlebagHQ, mgr, requireLoaded: true,
                InventoryType.SaddleBag1, InventoryType.SaddleBag2,
                InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2);

        if (Settings.IncludeRetainers)
            snap.HasRetainerData = ScanRetainers(snap.Retainer, snap.RetainerHQ, snap.RetainerDetails, mgr);

        if (Settings.IncludeFCChest)
            snap.HasFCData = ScanFCChest(snap, mgr);

        return snap;
    }

    // The FC chest containers exist in memory even when the chest was never opened, so only
    // count pages the game reports as loaded; otherwise an empty scan would wipe the saved data.
    private static readonly HashSet<int> SeenFCPages = [];

    private static unsafe bool ScanFCChest(InventorySnapshot snap, InventoryManager* mgr)
    {
        InventoryType[] pages =
        [
            InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2,
            InventoryType.FreeCompanyPage3, InventoryType.FreeCompanyPage4,
            InventoryType.FreeCompanyPage5,
        ];

        var anyLoaded = false;
        for (var p = 0; p < pages.Length; p++)
        {
            var container = mgr->GetInventoryContainer(pages[p]);
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
            if (items.Count > 0) SeenFCPages.Add(p + 1);
            else if (!SeenFCPages.Contains(p + 1)) continue;

            anyLoaded = true;
            snap.FCChestPages[p + 1] = items;
        }
        snap.RebuildFCChest();
        return anyLoaded;
    }

    // Returns true if at least one container was non-null (data was accessible in memory).
    private static unsafe bool ScanContainers(
        Dictionary<uint, int> target, Dictionary<uint, int>? hqTarget, InventoryManager* mgr, params InventoryType[] types)
        => ScanContainers(target, hqTarget, mgr, false, types);

    // requireLoaded: skip containers the game reports as not loaded (e.g. an unopened saddlebag).
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

    // Retainer inventories are stored client-side after visiting the retainer bell.
    // Only the currently active retainer window exposes real-time data; cached data
    // from previous visits is available in RetainerPage1-7 of each retainer's slot.
    // Returns true if any retainer data was available (Available == true for at least one retainer).
    private static unsafe bool ScanRetainers(
        Dictionary<uint, int> aggregate, Dictionary<uint, int> aggregateHQ,
        List<(string Name, Dictionary<uint, int> Items, Dictionary<uint, int> ItemsHQ)> details,
        InventoryManager* mgr)
    {
        var retMgr = RetainerManager.Instance();
        if (retMgr == null) return false;

        // The game keeps ONE retainer's inventory in memory (RetainerPage1-7): the one opened last.
        // Reading it for any other retainer would attribute the wrong items.
        // Right after picking another retainer from the list the pages still hold the previous one's items,
        // so only trust them while a retainer is actually open.
        var activeRetainer = retMgr->GetActiveRetainer();
        if (activeRetainer == null) return false;
        var activeId = activeRetainer->RetainerId;

        var count = retMgr->GetRetainerCount();
        for (uint ri = 0; ri < count && ri < 10; ri++)
        {
            var retainer = retMgr->GetRetainerBySortedIndex(ri);
            if (retainer == null || !retainer->Available || retainer->RetainerId != activeId) continue;

            // Name is a Span<byte> with a null terminator — decode as UTF-8.
            var nameSpan     = retainer->Name;
            var nullIdx      = nameSpan.IndexOf((byte)0);
            var retainerName = Encoding.UTF8.GetString(nullIdx >= 0 ? nameSpan[..nullIdx] : nameSpan);
            if (string.IsNullOrWhiteSpace(retainerName))
                retainerName = $"Retainer {ri + 1}";

            var retainerItems   = new Dictionary<uint, int>();
            var retainerItemsHQ = new Dictionary<uint, int>();
            var anyPage         = false;

            for (var page = 0; page < 7; page++)
            {
                var type = (InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page);
                var container = mgr->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) continue;
                anyPage = true;

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
                details.Add((retainerName, retainerItems, retainerItemsHQ));
            break;
        }

        return details.Count > 0;
    }

    // ── Recipe analysis ───────────────────────────────────────────────────────

    public List<CraftableEntry> Analyze(InventorySnapshot snap)
    {
        // Load every sheet in the configured language so item/recipe/category names (and the
        // RowRefs they resolve) all come back localized. RowRef.Value follows the owning sheet.
        var lang        = DataLanguage;
        var itemSheet   = Svc.Data.GetExcelSheet<LuminaItem>(lang);
        var recipeSheet = Svc.Data.GetExcelSheet<LuminaRecipe>(lang);
        if (itemSheet == null || recipeSheet == null) return [];

        var craftTypeNames = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.CraftType>(lang)
            ?.ToDictionary(ct => ct.RowId, ct => ct.Name.ExtractText())
            ?? [];

        var logRecipeIds = LogRecipeIds();
        var results = new List<CraftableEntry>();

        foreach (var recipe in recipeSheet)
        {
            if (recipe.RowId == 0 || recipe.ItemResult.RowId == 0 || !recipe.ItemResult.IsValid)
                continue;

            var resultItem  = recipe.ItemResult.Value;
            var ingredients = BuildIngredients(recipe);
            if (ingredients.Count == 0) continue;

            var missingCount = 0;
            var allInPlayer  = true;
            var checks       = new List<IngredientCheck>(ingredients.Count);

            foreach (var (id, name, icon, required) in ingredients)
            {
                var inPlayer = snap.Get(id, InventorySource.Player);
                var inOther  = snap.Get(id, InventorySource.Saddlebag)
                             + snap.Get(id, InventorySource.Retainer)
                             + snap.Get(id, InventorySource.FreeCompany);

                var check = new IngredientCheck(id, name, icon, required, inPlayer, inOther);
                checks.Add(check);

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

            var categoryName = resultItem.ItemUICategory.IsValid
                ? resultItem.ItemUICategory.Value.Name.ExtractText()
                : string.Empty;

            // Counts for the in-game crafting log: basic recipes (IsSecondary=false) and
            // special-category housing recipes (FilterGroup 14). Master recipes and other
            // special-category recipes (seasonal/event) are not tracked by the log.
            var isLogRecipe = recipe.SecretRecipeBook.RowId == 0
                && (logRecipeIds is not null
                    ? logRecipeIds.Contains(recipe.RowId) || (resultItem.FilterGroup == 14 && !recipe.IsExpert)
                    : !recipe.IsSecondary || resultItem.FilterGroup == 14);

            results.Add(new CraftableEntry
            {
                RecipeId     = recipe.RowId,
                ItemId       = recipe.ItemResult.RowId,
                Name         = resultItem.Name.ExtractText(),
                IconId       = resultItem.Icon,
                ResultAmount = recipe.AmountResult,
                CraftClass   = craftTypeNames.GetValueOrDefault(recipe.CraftType.RowId, string.Empty),
                CategoryName = categoryName,
                Status       = status,
                MissingCount = missingCount,
                IsCrafted    = QuestManager.IsRecipeComplete(recipe.RowId),
                IsLogRecipe  = isLogRecipe,
                ItemLevel    = (int)resultItem.LevelItem.RowId,
                CraftLevel   = recipe.RecipeLevelTable.IsValid ? recipe.RecipeLevelTable.Value.ClassJobLevel : 0,
                Ingredients  = checks,
            });
        }

        return results;
    }

    private HashSet<uint>? _logRecipeIds;

    /// <summary>Recipes listed in the crafting log's class tabs (RecipeNotebookList); null if the sheet is unavailable.</summary>
    private HashSet<uint>? LogRecipeIds()
    {
        if (_logRecipeIds is not null) return _logRecipeIds;

        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.RecipeNotebookList>();
        if (sheet is null) return null;

        var set = new HashSet<uint>();
        foreach (var page in sheet)
            foreach (var r in page.Recipe)
                if (r.RowId != 0) set.Add(r.RowId);

        return set.Count > 0 ? _logRecipeIds = set : null;
    }

    public int NearCompleteMissing => Settings.NearCompleteMissing;

    // ── Sub-recipe lookup (for expandable nested ingredients) ──────────────────

    private Dictionary<uint, uint>? _recipeByResult; // result itemId -> recipe RowId (language-independent)

    private Dictionary<uint, uint> RecipeByResult()
    {
        if (_recipeByResult is not null) return _recipeByResult;

        var map   = new Dictionary<uint, uint>();
        var sheet = Svc.Data.GetExcelSheet<LuminaRecipe>();
        if (sheet is not null)
            foreach (var r in sheet)
                if (r.RowId != 0 && r.ItemResult.RowId != 0)
                    map.TryAdd(r.ItemResult.RowId, r.RowId);

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

        var sheet = Svc.Data.GetExcelSheet<LuminaRecipe>(DataLanguage);
        if (sheet is null) return [];

        var checks = new List<IngredientCheck>(8);
        foreach (var (id, name, icon, required) in BuildIngredients(sheet.GetRow(recipeId)))
        {
            var inPlayer = snap.Get(id, InventorySource.Player);
            var inOther  = snap.Get(id, InventorySource.Saddlebag)
                         + snap.Get(id, InventorySource.Retainer)
                         + snap.Get(id, InventorySource.FreeCompany);
            checks.Add(new IngredientCheck(id, name, icon, required, inPlayer, inOther));
        }

        return checks;
    }

    private static List<(uint Id, string Name, uint Icon, int Amount)> BuildIngredients(LuminaRecipe recipe)
    {
        var list = new List<(uint, string, uint, int)>(8);

        foreach (var (itemRef, amount) in recipe.Ingredient.Zip(recipe.AmountIngredient))
        {
            if (amount <= 0 || itemRef.RowId == 0 || !itemRef.IsValid) continue;
            var item = itemRef.Value;

            // Discard the whole recipe if it requires collectables, untradeable items (includes
            // mission/key items) — these are not regular crafting materials.
            if (item.IsUntradable || item.IsCollectable) return [];

            list.Add((itemRef.RowId, item.Name.ExtractText(), item.Icon, amount));
        }

        return list;
    }
}
