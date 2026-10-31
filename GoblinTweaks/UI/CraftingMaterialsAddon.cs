using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using GoblinTweaks.Crafting;
using GoblinTweaks.Localization;
using GoblinTweaks.Services;
using GoblinTweaks.Tweaks;
using GoblinTweaks.UI.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// FFXIV-native Crafting Materials window (native AtkUnitBase addon).
/// Three columns: sidebar (search + status + category) | recipe list | ingredient detail panel,
/// with native item tooltips, search, a refresh button and Universalis market data.
/// </summary>
internal unsafe class CraftingMaterialsAddon : NativeAddon
{
    // ── Palette (FFXIV UI colors) ─────────────────────────────────────────────
    private static readonly Vector4 CategoryGold  = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 SubtitleBrown = new(157 / 255f, 131 / 255f,  91 / 255f, 1f);
    private static readonly Vector4 BodyGrey      = new(204 / 255f, 204 / 255f, 204 / 255f, 1f);
    private static readonly Vector4 MutedGrey     = new(0.50f,      0.50f,      0.50f,      1f);
    private static readonly Vector4 MarketColor   = new(0.60f,      0.75f,      0.60f,      1f);

    // ── Column widths ─────────────────────────────────────────────────────────
    private const float SidebarW    = 210f;
    private const float RecipeListW = 310f;
    private const float DivW        = 3f;

    // Sidebar geometry
    private const float SearchBarH  = 30f;
    private const float StatusTopY  = 4f;
    private const float HeaderH     = 20f;
    private const float StatusRowH  = 26f;
    private const float SepH        = 10f;

    // Detail panel geometry (header holds icon + 3 text rows)
    private const float DetailHeaderH = 88f;
    private const float DetailIconSz  = 36f;

    // Top filter bar (over columns 2+3) and bottom status bar (full width)
    private const float TopBarH    = 34f;
    private const float BottomBarH = 28f;

    // Minimum scrollbar thumb height — prevents it from shrinking to near-invisible on large lists
    private const float MinScrollThumbH = 16f;

    // ── Public init properties ─────────────────────────────────────────────────
    public CraftingMaterials? Tweak { get; init; }

    // ── Data ──────────────────────────────────────────────────────────────────
    private List<CraftableEntry> _entries       = [];
    private List<CraftableEntry> _filtered      = [];
    private InventorySnapshot    _snapshot      = new();
    private CraftableEntry?      _selectedEntry;
    private string               _searchText    = string.Empty;

    // Ingredient tree for the selected recipe (nested craftable sub-ingredients).
    private const int MaxIngDepth = 6;
    private sealed class IngTreeNode
    {
        public required IngredientCheck Check;
        public required string          PrimaryLocation;
        public IReadOnlyList<(string Location, int Total, int Hq, DateTime Seen)> AllLocations = [];
        public int                      Depth;
        public bool                     Craftable;
        public bool                     Expanded;
        public List<IngTreeNode>?       Children;
        public int                      Id;
        /// <summary>My List totals: item ids from the root down ("/2/4"), so an open branch stays open after a recount.</summary>
        public string                   Path = string.Empty;
    }
    private readonly List<IngTreeNode>            _ingRoots   = [];
    private readonly Dictionary<int, IngTreeNode> _ingById   = new();
    private int                                   _ingIdSeq;

    // My List totals: their own tree (the ingredient tree of a recipe is left as it is), the branches open, and the base
    // materials the gil total is made of.
    private readonly List<IngTreeNode>            _cartRoots    = [];
    private readonly Dictionary<int, IngTreeNode> _cartById     = new();
    private readonly HashSet<string>              _cartExpanded = [];
    private List<Need>                            _cartBase     = [];
    // Items of the totals with a price answer at the last check: when it grows, the prices are written again.
    private int      _cartPricedCount = -1;
    private DateTime _nextCartPriceCheck = DateTime.MinValue;
    private static readonly TimeSpan CartPriceCheckEvery = TimeSpan.FromSeconds(0.5);

    private bool   _pendingRefresh = true;
    private bool   _pendingFilter  = false;
    private int    _collisionRebuildFrames;
    private uint   _pendingOpenRecipeId; // deferred crafting-log open (0 = none)
    private string _selectedView   = "ready";

    // A view of the recipes of what a job can equip: this, then the ClassJob row.
    private const string JobView = "job:";

    // My List: the recipes ticked to craft, with the total of their materials on the right.
    private const string CartView = "cart";
    private bool InCartView => _selectedView == CartView;
    private List<CartItem> Cart => Tweak?.Current.Cart ?? [];
    // The recipes the window lists: an entry of My List whose recipe left the crafting log is neither shown nor counted.
    private HashSet<uint>  _entryIds = [];
    private List<CartItem> ListedCart => CraftCart.Listed(Cart, _entryIds.Contains);
    // When a My List box was last clicked: a row selection right after it came from the same click.
    private DateTime _cartToggledAt = DateTime.MinValue;
    private static readonly TimeSpan CartClickWindow = TimeSpan.FromMilliseconds(250);
    private TextButtonNode? _clearCartBtn;

    // Auto-refresh when inventory changes while the window is open
    private DateTime _inventoryChangedAt = DateTime.MinValue;
    // Count of filtered items with market data at last sort pass; used to detect new arrivals.
    private int _marketDataReadyCount = -1;
    // Prices arrive in batches: the market sort is redone at most this often, keeping the selection and the scroll.
    private static readonly TimeSpan MarketResortEvery = TimeSpan.FromSeconds(2);
    private DateTime _nextMarketCheck = DateTime.MinValue;
    private bool     _marketResort;
    // Set once an error was reported, so it is not reported again every frame until the tweak is disabled.
    private bool     _failed;
    private uint     _pendingRestoreId;   // RecipeId to re-select after a refresh
    private bool     _keepScroll;         // a refresh of the data, not a change of filter: the list stays where it was
    private const double InventoryDebounceMs = 1500.0;

    // Filter / sort state (driven by the top bar)
    // Loaded from and saved to the tweak options (LoadFilters / SaveFilters), so they survive a rebuild or a restart.
    private bool   _hideCrafted;
    private bool   _onlyMyLevel;
    private string _sortMode       = SortName;
    private string _classFilter    = AllClasses;
    private bool   _sortDescending;

    // Detail panel mode: the ingredient tree, or the base materials of one craft (shopping list).
    private bool                  _showShopping;
    private List<IngredientCheck> _shoppingRows = [];

    private const string SortName    = "Name";
    private const string SortStatus  = "Craft status";
    private const string SortLevel   = "Item level";
    private const string SortCraft   = "Craft level";

    private static List<string> SortModes => [SortName, SortStatus, SortLevel, SortCraft, SortMissing, SortMarket];
    private const string SortMissing = "Fewest missing";
    private const string SortMarket  = "Market price";
    private const string AllClasses  = "All classes";

    // ── Sidebar node refs ─────────────────────────────────────────────────────
    private TextInputNode?     _searchInput;
    private CircleButtonNode?  _refreshBtn;
    private readonly List<SelectableTextNode> _statusItems = [];
    private ListNode<CraftableEntry,  RecipeRowNode>?   _recipeList;
    private ListNode<CategoryEntry,   CategoryRowNode>? _catListNode;

    // ── Filter bar + bottom bar node refs ──────────────────────────────────────
    private CheckboxNode?      _hideCraftedCheck;
    private CheckboxNode?      _myLevelCheck;
    private StringDropDownNode? _sortDropdown;
    private CircleButtonNode?  _sortDirBtn;
    private StringDropDownNode? _classDropdown;
    private TextNode?          _hdrStatus;
    private TextNode?          _hdrCategory;
    private TextNode?          _lblSort;
    private TextNode?          _lblClass;
    private CircleButtonNode?  _helpButton;
    private TextNode?          _bottomShowing;
    private TextNode?          _bottomCrafted;

    // ── Detail panel node refs ────────────────────────────────────────────────
    private TextNode?           _detailPlaceholder;
    private IconImageNode?      _detailIcon;
    private TextNode?           _detailName;
    private TextNode?           _detailSub;
    private TextNode?           _detailMarket;
    private HorizontalLineNode? _detailSep;
    private TextNode?           _detailIngLabel;
    private TextButtonNode?     _shoppingButton;
    private CollisionNode?      _detailCollision;
    private ListNode<IngredientRow, IngredientRowNode>? _ingredientList;

    // ── My List totals node refs (the detail panel in the My List view) ─────────
    private TextNode?     _cartTitle;
    private CheckboxNode? _cartDeductCheck;
    private TextNode?     _cartColItem;
    private TextNode?     _cartColQty;
    private TextNode?     _cartColPrice;
    private TextNode?     _cartTotal;
    private TextNode?     _cartEmpty;
    private ListNode<IngredientRow, IngredientRowNode>? _cartList;

    // ── FC chest highlight ────────────────────────────────────────────────────

    /// <summary>The items the detail panel lists: the ingredient tree (open branches included) or the shopping list.</summary>
    private IEnumerable<uint> ListedItemIds()
        => InCartView    ? _cartById.Values.Select(node => node.Check.ItemId)
         : _showShopping ? _shoppingRows.Select(row => row.ItemId)
         : _ingById.Values.Select(node => node.Check.ItemId);

    /// <summary>(tab, slot) pairs in the FC chest holding any listed ingredient of the selected recipe (or of My List).</summary>
    public IReadOnlyCollection<(int Tab, int Slot)> FCChestMarks()
    {
        if (_selectedEntry is null && !InCartView) return [];

        var marks = new HashSet<(int, int)>();
        foreach (var id in ListedItemIds())
            if (_snapshot.FCChestSlots.TryGetValue(id, out var list))
                foreach (var m in list) marks.Add(m);
        return marks;
    }

    /// <summary>Ids of the listed ingredients of the selected recipe, for marking them in the bags, retainers and saddlebag.</summary>
    public IReadOnlyCollection<uint> IngredientItemIds()
    {
        if (_selectedEntry is null && !InCartView) return [];

        return ListedItemIds().ToHashSet();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);
        _selectedEntry  = null;
        _pendingRefresh = true;
        LoadFilters();
        BuildLayout();
        BuildSettingsButton();

        // Route row right-clicks: ingredients → native item menu; recipes → item menu if
        // the result is in your bags, otherwise the native Crafting Log for that recipe.
        RecipeRowNode.OnItemRightClick     = OnRecipeRightClick;
        IngredientRowNode.OnItemRightClick = id => OpenItemContextMenu(id);
        RecipeRowNode.IsInCart             = id => CraftCart.Contains(Cart, id);
        RecipeRowNode.OnToggleCart         = ToggleCart;
        RecipeRowNode.CountOf              = id => Cart.Find(i => i.RecipeId == id)?.Count ?? CraftCart.MinCount;
        RecipeRowNode.OnCountChanged       = SetCartCount;
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);
        if (_failed) return;

        try
        {
            Update();
        }
        catch (Exception ex)
        {
            // Disables the tweak a moment later, outside this update.
            _failed = true;
            Tweak?.Fail(ex);
        }
    }

    private void Update()
    {
        if (_pendingRefresh) LoadData();
        if (_pendingFilter)  ApplyFilter();

        _recipeList?.Update();
        _catListNode?.Update();
        _ingredientList?.Update();
        _cartList?.Update();

        if (_recipeList is not null)     EnforceScrollbarMinThumb(_recipeList.ScrollBarNode);
        if (_catListNode is not null)    EnforceScrollbarMinThumb(_catListNode.ScrollBarNode);
        if (_ingredientList is not null) EnforceScrollbarMinThumb(_ingredientList.ScrollBarNode);
        if (_cartList is not null)       EnforceScrollbarMinThumb(_cartList.ScrollBarNode);

        PollCartPrices();

        // The ListNode only rebuilds the addon's collision node list on scroll, so after
        // any repopulation (filter change, new selection) freshly recycled rows are absent
        // from it and lose hover + click. Rebuild it here for a couple frames after a change.
        if (_collisionRebuildFrames > 0)
        {
            _collisionRebuildFrames--;
            if (InternalAddon is not null)
                InternalAddon->UpdateCollisionNodeList(false);
        }

        // Deferred Crafting Log open (set by a recipe right-click), run outside event dispatch.
        if (_pendingOpenRecipeId != 0)
        {
            var recipeId = _pendingOpenRecipeId;
            _pendingOpenRecipeId = 0;
            AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeId);
        }

        // Debounced auto-refresh when inventory changes (e.g. moving items, visiting retainers)
        if (_inventoryChangedAt != DateTime.MinValue &&
            (DateTime.UtcNow - _inventoryChangedAt).TotalMilliseconds >= InventoryDebounceMs)
        {
            _inventoryChangedAt = DateTime.MinValue;
            _pendingRestoreId   = _selectedEntry?.RecipeId ?? 0;
            _keepScroll         = true;
            _pendingRefresh     = true;
        }

        // Refresh market data display when Universalis responds
        PollMarketData();

        // Re-sort when new market data arrives while the market-price sort is active
        if (_sortMode == SortMarket && _marketDataReadyCount >= 0 && DateTime.UtcNow >= _nextMarketCheck
            && Tweak?.Universalis is { } uniPoll)
        {
            _nextMarketCheck = DateTime.UtcNow + MarketResortEvery;
            var readyNow = _filtered.Count(e => uniPoll.HasData(e.ItemId));
            if (readyNow != _marketDataReadyCount)
            {
                _marketResort  = true;
                _pendingFilter = true;
            }
        }
    }

    /// <summary>Takes the filters the window had last time (they are kept in the tweak options).</summary>
    private void LoadFilters()
    {
        if (Tweak?.Current is not { } options) return;

        // A view of an item category is from before the list was by job: it shows everything.
        _selectedView   = string.IsNullOrEmpty(options.View) ? "ready" : options.View.StartsWith("cat:") ? "all" : options.View;
        _sortMode       = options.SortMode is SortName or SortStatus or SortLevel or SortCraft or SortMissing or SortMarket ? options.SortMode : SortName;
        _sortDescending = options.SortDescending;
        _classFilter    = string.IsNullOrEmpty(options.ClassFilter) ? AllClasses : options.ClassFilter;
        _hideCrafted    = options.HideCrafted;
        _onlyMyLevel    = options.OnlyMyLevel;
    }

    /// <summary>Remembers the filters, when they changed.</summary>
    private void SaveFilters()
    {
        if (Tweak?.Current is not { } options) return;

        var classFilter = _classFilter == AllClasses ? string.Empty : _classFilter;
        if (options.View == _selectedView && options.SortMode == _sortMode && options.SortDescending == _sortDescending
            && options.ClassFilter == classFilter && options.HideCrafted == _hideCrafted && options.OnlyMyLevel == _onlyMyLevel)
            return;

        options.View           = _selectedView;
        options.SortMode       = _sortMode;
        options.SortDescending = _sortDescending;
        options.ClassFilter    = classFilter;
        options.HideCrafted    = _hideCrafted;
        options.OnlyMyLevel    = _onlyMyLevel;
        Tweak.SaveFilters();
    }

    /// <summary>Queues a native collision-node-list rebuild for the next few frames.</summary>
    private void RequestCollisionRebuild() => _collisionRebuildFrames = 3;

    /// <summary>Re-scans inventories and re-analyses recipes next frame, preserving the current selection.</summary>
    public void RequestRefresh()
    {
        _pendingRestoreId = _selectedEntry?.RecipeId ?? 0;
        _keepScroll       = true;
        _pendingRefresh   = true;
    }

    /// <summary>Schedules a debounced re-scan triggered by an inventory-change event.</summary>
    public void NotifyInventoryChanged() => _inventoryChangedAt = DateTime.UtcNow;

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        Svc.Log.Information("CraftingMaterials: window finalizing.");
        _statusItems.Clear();
        // The search box is rebuilt empty when the window opens again, so the search it filtered by goes with it.
        _searchText     = string.Empty;
        _searchInput    = null;
        _refreshBtn     = null;
        _recipeList     = null;
        _catListNode    = null;
        _ingredientList = null;
        _detailPlaceholder = null;
        _detailIcon     = null;
        _detailName     = null;
        _detailSub      = null;
        _detailMarket   = null;
        _detailSep      = null;
        _detailIngLabel = null;
        _detailCollision = null;

        _hideCraftedCheck = null;
        _myLevelCheck     = null;
        _shoppingButton   = null;
        _sortDropdown     = null;
        _sortDirBtn       = null;
        _classDropdown    = null;
        _hdrStatus        = null;
        _hdrCategory      = null;
        _lblSort          = null;
        _lblClass         = null;
        _helpButton       = null;
        _bottomShowing    = null;
        _bottomCrafted    = null;

        Tweak?.CloseHelp();

        RecipeRowNode.OnItemRightClick     = null;
        IngredientRowNode.OnItemRightClick = null;
        RecipeRowNode.IsInCart             = null;
        RecipeRowNode.OnToggleCart         = null;
        RecipeRowNode.CountOf              = null;
        RecipeRowNode.OnCountChanged       = null;
        RecipeRowNode.ShowCounter          = false;
        _clearCartBtn                      = null;
        _cartTitle       = null;
        _cartDeductCheck = null;
        _cartColItem     = null;
        _cartColQty      = null;
        _cartColPrice    = null;
        _cartTotal       = null;
        _cartEmpty       = null;
        _cartList        = null;
        _cartRoots.Clear();
        _cartById.Clear();
        _cartBase = [];

        base.OnFinalize(addon);
    }

    // ── Native item context menu ────────────────────────────────────────────────

    /// <summary>
    /// Opens the game's native inventory item menu for items in the player's bags or saddlebag.
    /// For items not found in an accessible inventory, falls back to opening the crafting-log
    /// recipe that produces the item (if one exists). Returns false if nothing could be done.
    /// </summary>
    private bool OpenItemContextMenu(uint itemId)
    {
        if (itemId == 0) return false;

        var (type, slot) = FindAnyItemSlot(itemId);
        if (slot >= 0)
        {
            var agent = AgentInventoryContext.Instance();
            if (agent is null) return false;
            agent->OpenForItemSlot(type, slot, 0, (uint)AddonId);
            return true;
        }

        // Item not in any accessible slot — if the ingredient is itself craftable, open its recipe.
        var recipeId = Tweak?.GetRecipeId(itemId) ?? 0u;
        if (recipeId != 0 && _pendingOpenRecipeId == 0)
        {
            _pendingOpenRecipeId = recipeId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Recipe right-click: show the native item menu if the result is in your bags; otherwise
    /// open the game's Crafting Log for the recipe (the result usually isn't owned).
    /// </summary>
    private void OnRecipeRightClick(CraftableEntry entry)
    {
        if (OpenItemContextMenu(entry.ItemId)) return;

        // Defer opening the Crafting Log to the next frame: launching a heavy native addon
        // from inside our own event dispatch (while the list is repopulating) traps the UI.
        if (entry.RecipeId != 0)
            _pendingOpenRecipeId = entry.RecipeId;
    }

    // Searches player bags, crystals, and saddlebag — all containers safe to pass to
    // AgentInventoryContext.OpenForItemSlot with a4=0.
    private static unsafe (InventoryType Type, int Slot) FindAnyItemSlot(uint itemId)
    {
        var mgr = InventoryManager.Instance();
        if (mgr is null) return (InventoryType.Inventory1, -1);

        ReadOnlySpan<InventoryType> containers =
        [
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.Crystals,
            InventoryType.SaddleBag1,        InventoryType.SaddleBag2,
            InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
        ];

        foreach (var type in containers)
        {
            var container = mgr->GetInventoryContainer(type);
            if (container is null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item is not null && item->ItemId == itemId)
                    return (type, i);
            }
        }

        return (InventoryType.Inventory1, -1);
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private void LoadData()
    {
        _pendingRefresh = false;
        _selectedEntry  = null;
        ClearDetailPanel();

        if (Tweak is null) return;

        // Pick up the window language before (re)building any text.
        CraftLoc.Lang = Tweak.UiLanguage;
        Title         = CraftLoc.Get("title");
        ApplyLanguage();

        try
        {
            _snapshot = Tweak.ScanAndMerge();
            _entries  = Tweak.Analyze(_snapshot);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "CraftingMaterialsAddon: LoadData failed");
            _entries = [];
        }
        _entryIds = _entries.Select(e => e.RecipeId).ToHashSet();

        UpdateStatusCounts();
        UpdateCategoryList();
        UpdateClassFilterOptions();
        _pendingFilter = true;
    }

    private void UpdateClassFilterOptions()
    {
        if (_classDropdown is null) return;

        var classes = _entries
            .Where(e => !string.IsNullOrEmpty(e.CraftClass))
            .Select(e => e.CraftClass)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        _classDropdown.Options = [AllClasses, .. classes];

        // Keep the current selection valid after a rescan.
        if (_classFilter != AllClasses && !classes.Contains(_classFilter))
            _classFilter = AllClasses;
        _classDropdown.SelectedOption = _classFilter;
    }

    private void UpdateStatusCounts()
    {
        var ready    = _entries.Count(e => e.Status is CraftStatus.Ready or CraftStatus.ReadyElsewhere);
        var near     = _entries.Count(e => e.Status == CraftStatus.NearComplete);
        var notReady = _entries.Count(e => e.Status == CraftStatus.NotReady);

        if (_statusItems.Count >= 4)
        {
            _statusItems[0].String = $"{CraftLoc.Get("status.all")} ({_entries.Count})";
            _statusItems[1].String = $"{CraftLoc.Get("status.ready")} ({ready})";
            _statusItems[2].String = $"{CraftLoc.Get("status.almost")} ({near})";
            _statusItems[3].String = $"{CraftLoc.Get("status.cant")} ({notReady})";
        }
        UpdateCartCount();
    }

    private void UpdateCartCount()
    {
        if (_statusItems.Count >= 5)
            _statusItems[4].String = $"{CraftLoc.Get("status.cart")} ({ListedCart.Count})";
    }

    // ── My List ───────────────────────────────────────────────────────────────

    /// <summary>Puts a recipe in My List, or takes it out.</summary>
    private void ToggleCart(uint recipeId)
    {
        if (Tweak is null) return;

        _cartToggledAt = DateTime.UtcNow;
        CraftCart.Toggle(Tweak.Current.Cart, recipeId);
        RecipeRowNode.CartVersion++;
        Tweak.SaveFilters();
        UpdateCartCount();

        // In My List an unticked recipe leaves the list (the rows elsewhere update their box on their own).
        if (InCartView)
        {
            _keepScroll    = true;
            _pendingFilter = true;
        }
    }

    /// <summary>Sets how many times a recipe of My List will be crafted.</summary>
    private void SetCartCount(uint recipeId, int count)
    {
        if (Tweak is null) return;

        var before = Cart.Find(i => i.RecipeId == recipeId)?.Count;
        CraftCart.SetCount(Tweak.Current.Cart, recipeId, count);
        if (Cart.Find(i => i.RecipeId == recipeId)?.Count == before) return;

        Tweak.SaveFilters();
        if (InCartView) RebuildCart(keepScroll: true);
    }

    /// <summary>Empties My List.</summary>
    private void ClearCart()
    {
        if (Tweak is null || Cart.Count == 0) return;

        Tweak.Current.Cart.Clear();
        RecipeRowNode.CartVersion++;
        Tweak.SaveFilters();
        UpdateCartCount();
        _pendingFilter = true;
    }

    /// <summary>Shows the counters of My List in its view and hides them elsewhere.</summary>
    private void UpdateCartControls()
    {
        RecipeRowNode.ShowCounter = InCartView;
        RecipeRowNode.CartVersion++;
    }

    /// <summary>Shows the totals of My List in the place of the recipe details, or puts the details back.</summary>
    private void ShowCartPanel(bool show)
    {
        var empty = ListedCart.Count == 0;

        if (show)
        {
            if (_detailPlaceholder is not null) _detailPlaceholder.IsVisible = false;
            if (_ingredientList is not null)    _ingredientList.IsVisible    = false;
        }
        else if (_ingredientList is not null)
        {
            _ingredientList.IsVisible = true;
        }

        foreach (var node in new NodeBase?[] { _cartTitle, _cartDeductCheck, _clearCartBtn, _cartColItem, _cartColQty, _cartColPrice, _cartList, _cartTotal })
            if (node is not null) node.IsVisible = show && !empty;
        if (_cartEmpty is not null) _cartEmpty.IsVisible = show && empty;

        if (!show)
        {
            _cartRoots.Clear();
            _cartById.Clear();
            _cartBase = [];
            if (_cartList is not null) _cartList.OptionsList = [];
        }
        RequestCollisionRebuild();
    }

    /// <summary>Works out the totals of My List again: its tree (open branches kept open), base materials and prices.</summary>
    private void RebuildCart(bool keepScroll)
    {
        _cartRoots.Clear();
        _cartById.Clear();
        _cartBase = [];
        if (Tweak is null || !InCartView) return;

        ShowCartPanel(true);

        var data   = Tweak.CraftData(_snapshot);
        var deduct = Tweak.Current.CartDeductOwned;
        var listed = ListedCart;
        foreach (var need in CraftMath.Direct(listed, data))
            _cartRoots.Add(MakeCartNode(need, 0, string.Empty, data, deduct));
        _cartBase = CraftMath.BaseMaterials(listed, data, deduct);

        // Prices of everything listed and of the base materials, ahead of the rest of the queue.
        foreach (var id in _cartById.Values.Select(n => n.Check.ItemId).Concat(_cartBase.Select(b => b.ItemId)).Distinct())
            Tweak.Universalis.RequestIfNeeded(id, Tweak.WorldId, urgent: true);

        _cartPricedCount = -1;
        RebuildCartList(keepScroll);
    }

    private IngTreeNode MakeCartNode(Need need, int depth, string parentPath, ICraftData data, bool deduct)
    {
        var node = MakeIngNode(CraftingMaterials.Check(need, _snapshot), depth, _cartById);
        node.Path = $"{parentPath}/{need.ItemId}";

        // Only what still has to be crafted opens: an intermediate you already have enough of has nothing under it.
        var children = node.Craftable ? CraftMath.Children(need.ItemId, need.Amount, data, deduct) : [];
        node.Craftable = children.Count > 0;

        if (node.Craftable && _cartExpanded.Contains(node.Path))
        {
            node.Expanded = true;
            node.Children = children.Select(child => MakeCartNode(child, depth + 1, node.Path, data, deduct)).ToList();
        }
        return node;
    }

    /// <summary>Opens or closes a craftable row of the totals.</summary>
    private void ToggleCartIngredient(IngredientRow row)
    {
        if (!_cartById.TryGetValue(row.NodeId, out var node) || !node.Craftable) return;

        if (!_cartExpanded.Remove(node.Path))
            _cartExpanded.Add(node.Path);
        RebuildCart(keepScroll: true);
    }

    /// <summary>Writes the rows of the totals (with their prices) and the gil total.</summary>
    private void RebuildCartList(bool keepScroll)
    {
        if (_cartList is null || Tweak is null) return;

        var uni    = Tweak.Universalis;
        var deduct = Tweak.Current.CartDeductOwned;
        var rows   = new List<IngredientRow>();

        void Add(IngTreeNode n)
        {
            var (price, tip) = CartPrice(n.Check, uni, deduct);
            rows.Add(new IngredientRow(n.Check, n.PrimaryLocation, n.AllLocations, n.Depth, n.Craftable, n.Expanded, n.Id, price, tip));
            if (n.Expanded && n.Children is not null)
                foreach (var child in n.Children)
                    Add(child);
        }

        foreach (var root in _cartRoots)
            Add(root);

        _cartList.AutoResetScroll = !keepScroll;
        _cartList.OptionsList     = rows;
        _cartList.AutoResetScroll = true;

        if (_cartTotal is not null)
        {
            var cost = CraftMath.Cost(
                _cartBase.Select(b => (b.ItemId, deduct ? Math.Max(0, b.Amount - _snapshot.GetTotal(b.ItemId)) : b.Amount)),
                id => CartUnitPrice(id, uni));
            _cartTotal.String = cost.Loading ? CraftLoc.Get("cart.loading")
                : cost.Partial ? CraftLoc.Fmt("cart.totalmin", cost.Gil.ToString("N0"))
                : CraftLoc.Fmt(deduct ? "cart.total" : "cart.totalall", cost.Gil.ToString("N0"));
        }

        RequestCollisionRebuild();
    }

    /// <summary>
    /// The price column of a row: what buying what is missing costs (all of it when nothing is deducted), "—" when nothing
    /// has to be bought, "…" while loading, empty without a price. Also the unit price, for the tooltip.
    /// </summary>
    private static (string Price, string? Tip) CartPrice(IngredientCheck check, UniversalisService uni, bool deduct)
    {
        var qty = deduct ? check.Missing : check.Required;
        if (qty <= 0) return ("—", null);

        var unit = CartUnitPrice(check.ItemId, uni);
        if (unit is null) return ("…", null);
        if (unit <= 0)    return (string.Empty, null);

        return ((unit.Value * qty).ToString("N0"), CraftLoc.Fmt("cart.unit", unit.Value.ToString("N0")));
    }

    /// <summary>Price of one unit; null while Universalis hasn't answered, 0 when the item has no price.</summary>
    private static long? CartUnitPrice(uint itemId, UniversalisService uni)
    {
        if (uni.HasData(itemId)) return UnitPrice(uni.Get(itemId));
        return uni.HasFailed(itemId) ? 0 : null;
    }

    /// <summary>While My List is open, writes the prices again when more of them have arrived.</summary>
    private void PollCartPrices()
    {
        if (!InCartView || Tweak is null || _cartList is null || DateTime.UtcNow < _nextCartPriceCheck) return;
        _nextCartPriceCheck = DateTime.UtcNow + CartPriceCheckEvery;

        var uni   = Tweak.Universalis;
        var ids   = _cartById.Values.Select(n => n.Check.ItemId).Concat(_cartBase.Select(b => b.ItemId)).Distinct().ToList();
        var known = ids.Count(id => uni.HasData(id) || uni.HasFailed(id));
        if (known == _cartPricedCount) return;

        _cartPricedCount = known;
        RebuildCartList(keepScroll: true);

        // A failed request may be retried once its wait is over.
        foreach (var id in ids.Where(id => !uni.HasData(id) && !uni.IsPending(id)))
            uni.RequestIfNeeded(id, Tweak.WorldId, urgent: true);
    }

    /// <summary>The recipes of My List, in the order they were added (the ones the game no longer lists are skipped).</summary>
    private List<CraftableEntry> CartEntries()
    {
        var byId = new Dictionary<uint, CraftableEntry>();
        foreach (var e in _entries) byId.TryAdd(e.RecipeId, e);
        return Cart.Select(i => byId.GetValueOrDefault(i.RecipeId)).OfType<CraftableEntry>().ToList();
    }

    private void UpdateCategoryList()
    {
        // One entry per Disciple of the Hand and Land, in the game's order.
        var cats = new List<CategoryEntry>();
        foreach (var job in CraftingMaterials.HandAndLandJobs())
        {
            var recipes = _entries.Where(e => IsOfJob(e, job)).ToList();
            if (recipes.Count > 0)
                cats.Add(new CategoryEntry($"{JobView}{job.Id}", job.Name, recipes.Count(e => e.IsCrafted), recipes.Count));
        }

        if (_catListNode is not null)
            _catListNode.OptionsList = cats;

        RequestCollisionRebuild();
    }

    /// <summary>
    /// Whether a recipe goes under a job: for a crafter, the recipes it crafts; for a gatherer, which crafts nothing,
    /// what only that gatherer can equip (its tools). Gear every gatherer shares would put the same list under each.
    /// </summary>
    private static bool IsOfJob(CraftableEntry entry, CraftingMaterials.HandOrLandJob job)
        => job.Crafter
            ? entry.CraftTypeId == job.CraftTypeId
            : entry.ForJobs is [var only] && only == job.Id;

    private void ApplyFilter()
    {
        _pendingFilter = false;

        // A re-sort because prices arrived keeps the selected recipe, its open ingredients and the scroll position.
        var resort = _marketResort;
        _marketResort = false;

        // So does a refresh of the data (the inventory changed while crafting): the recipe is selected again after it.
        var keepScroll = resort || _keepScroll;
        _keepScroll = false;
        if (!resort)
        {
            _selectedEntry = null;
            ClearDetailPanel();
        }

        _filtered = _selectedView switch
        {
            "all"      => _entries.ToList(),
            "ready"    => _entries.Where(e => e.Status is CraftStatus.Ready or CraftStatus.ReadyElsewhere).ToList(),
            "near"     => _entries.Where(e => e.Status == CraftStatus.NearComplete).ToList(),
            "notReady" => _entries.Where(e => e.Status == CraftStatus.NotReady).ToList(),
            var v when v.StartsWith(JobView) && uint.TryParse(v[JobView.Length..], out var id)
                       && CraftingMaterials.HandAndLandJobs().FirstOrDefault(j => j.Id == id) is { } job
                       => _entries.Where(e => IsOfJob(e, job)).ToList(),
            CartView   => CartEntries(),
            _          => _entries.ToList(),
        };

        // My List shows every recipe in it, in the order they were added: no search, filter or sort applies.
        // Search text filter
        if (!InCartView && !string.IsNullOrWhiteSpace(_searchText))
        {
            var term = _searchText.Trim();
            _filtered = _filtered.Where(e =>
                e.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.CraftClass.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Ingredients.Any(i => i.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        // Top-bar filters
        if (_hideCrafted && !InCartView)
            _filtered = _filtered.Where(e => !e.IsCrafted).ToList();

        if (_classFilter != AllClasses && !InCartView)
            _filtered = _filtered.Where(e => e.CraftClass == _classFilter).ToList();

        if (_onlyMyLevel && !InCartView && Tweak is { } tweak)
        {
            var levels = new Dictionary<uint, int>();
            _filtered = _filtered.Where(e =>
            {
                if (!levels.TryGetValue(e.CraftTypeId, out var level))
                    levels[e.CraftTypeId] = level = tweak.CrafterLevel(e.CraftTypeId);
                return e.CraftLevel <= level;
            }).ToList();
        }

        // Sort (base ascending order, then reversed if the direction toggle is set)
        // Market price sort is handled separately to always put unknown prices at the bottom.
        if (InCartView)
        {
            _marketDataReadyCount = -1;
        }
        else if (_sortMode == SortMarket)
        {
            _filtered = SortByMarket(_filtered);
            // Trigger market data requests for all visible items.
            var tw = Tweak;
            if (tw is not null)
                foreach (var e in _filtered)
                    tw.Universalis.RequestIfNeeded(e.ItemId, tw.WorldId);
            _marketDataReadyCount = _filtered.Count(e => tw?.Universalis.HasData(e.ItemId) == true);
        }
        else
        {
            var ordered = _sortMode switch
            {
                SortStatus  => _filtered.OrderBy(e => (int)e.Status).ThenBy(e => e.Name),
                SortLevel   => _filtered.OrderBy(e => e.ItemLevel).ThenBy(e => e.Name),
                SortCraft   => _filtered.OrderBy(e => e.CraftLevel).ThenBy(e => e.ItemLevel).ThenBy(e => e.Name),
                SortMissing => _filtered.OrderBy(e => e.MissingCount).ThenBy(e => e.Name),
                _           => _filtered.OrderBy(e => e.Name),
            };
            _filtered = ordered.ToList();
            if (_sortDescending)
                _filtered.Reverse();
            _marketDataReadyCount = -1;
        }

        UpdateCartControls();

        if (_recipeList is not null)
        {
            _recipeList.AutoResetScroll = !keepScroll;
            _recipeList.OptionsList     = _filtered;
            _recipeList.AutoResetScroll = true;
        }

        if (resort && _selectedEntry is not null && !_filtered.Contains(_selectedEntry))
        {
            _selectedEntry = null;
            ClearDetailPanel();
        }

        // Restore the previously selected recipe after an inventory-triggered refresh
        if (_pendingRestoreId != 0)
        {
            var toRestore = _filtered.FirstOrDefault(e => e.RecipeId == _pendingRestoreId);
            _pendingRestoreId = 0;
            if (toRestore is not null && !InCartView)
                ShowDetailPanel(toRestore);
        }

        // My List: its totals take the place of the recipe details.
        ShowCartPanel(InCartView);
        if (InCartView) RebuildCart(keepScroll);

        UpdateBottomBar();
        RequestCollisionRebuild();
        SaveFilters();
    }

    // ── Detail panel ──────────────────────────────────────────────────────────

    private void ShowDetailPanel(CraftableEntry entry)
    {
        _selectedEntry = entry;

        if (_detailPlaceholder is not null) _detailPlaceholder.IsVisible = false;

        if (_detailIcon is not null)  { _detailIcon.IconId = entry.IconId; _detailIcon.IsVisible = true; }
        if (_detailName is not null)  { _detailName.String = entry.Name;   _detailName.IsVisible = true; }

        var sub = entry.CraftClass;
        if (entry.ResultAmount > 1) sub += $"   ×{entry.ResultAmount}";
        if (entry.IsCrafted)        sub += $"   ✓ {CraftLoc.Get("detail.crafted")}";

        // How many times it can be made with what you own, and with only what is in your bags.
        var crafts = entry.Ingredients.Count == 0 ? 0 : entry.Ingredients.Min(i => i.Available / i.Required);
        var inBags = entry.Ingredients.Count == 0 ? 0 : entry.Ingredients.Min(i => i.InPlayer / i.Required);
        if (crafts > 0) sub += "   " + CraftLoc.Fmt("detail.crafts", crafts, inBags);
        if (_detailSub is not null)  { _detailSub.String = sub; _detailSub.IsVisible = !string.IsNullOrEmpty(sub); }

        // Request Universalis (ahead of the rest of the queue); PollMarketData shows "loading…" or the prices.
        // The ingredients too, for the cost of the materials.
        if (Tweak?.Universalis is { } uni)
        {
            foreach (var ing in entry.Ingredients)
                uni.RequestIfNeeded(ing.ItemId, Tweak.WorldId, urgent: true);
            uni.RequestIfNeeded(entry.ItemId, Tweak.WorldId, urgent: true);
            if (_detailMarket is not null)
            {
                _detailMarket.String    = string.Empty;
                _detailMarket.IsVisible = true;
                PollMarketData();
            }
        }

        if (_detailSep is not null)      _detailSep.IsVisible      = true;
        if (_detailIngLabel is not null)  _detailIngLabel.IsVisible = true;
        if (_shoppingButton is not null)  _shoppingButton.IsVisible = true;

        if (_detailCollision is not null)
        {
            _detailCollision.ItemTooltip = entry.ItemId;
            _detailCollision.IsVisible   = true;
        }

        // Build the ingredient tree (roots = this recipe's ingredients).
        _ingRoots.Clear();
        _ingById.Clear();
        _ingIdSeq = 0;
        foreach (var ing in entry.Ingredients)
            _ingRoots.Add(MakeIngNode(ing, 0));
        RebuildIngredientList();

        RequestCollisionRebuild();
    }

    /// <summary>Where an item is, the place with the most first.</summary>
    private List<(string Location, int Total, int Hq, DateTime Seen)> LocationsOf(uint itemId)
        => _snapshot.Locations(itemId).OrderByDescending(l => l.Total).ToList();

    private IngTreeNode MakeIngNode(IngredientCheck ing, int depth) => MakeIngNode(ing, depth, _ingById);

    private IngTreeNode MakeIngNode(IngredientCheck ing, int depth, Dictionary<int, IngTreeNode> into)
    {
        var locs = LocationsOf(ing.ItemId);
        var node = new IngTreeNode
        {
            Check           = ing,
            PrimaryLocation = locs.Count > 0 ? locs[0].Location : string.Empty,
            AllLocations    = locs,
            Depth           = depth,
            Craftable       = depth < MaxIngDepth && (Tweak?.IsCraftable(ing.ItemId) ?? false),
            Id              = ++_ingIdSeq,
        };
        into[node.Id] = node;
        return node;
    }

    /// <summary>Expand/collapse a craftable ingredient, lazily computing its sub-ingredients.</summary>
    private void ToggleIngredient(IngredientRow row)
    {
        if (!_ingById.TryGetValue(row.NodeId, out var node) || !node.Craftable) return;

        node.Expanded = !node.Expanded;
        if (node.Expanded && node.Children is null && Tweak is not null)
            node.Children = Tweak.SubIngredients(node.Check.ItemId, _snapshot)
                .Select(sub => MakeIngNode(sub, node.Depth + 1))
                .ToList();

        RebuildIngredientList();
    }

    private void RebuildIngredientList()
    {
        if (_ingredientList is null) return;

        var rows = new List<IngredientRow>();

        if (_showShopping)
        {
            _shoppingRows = _selectedEntry is not null && Tweak is not null
                ? Tweak.ShoppingList(_selectedEntry.RecipeId, _snapshot)
                : [];
            foreach (var check in _shoppingRows)
            {
                var locs = LocationsOf(check.ItemId);
                rows.Add(new IngredientRow(check, locs.Count > 0 ? locs[0].Location : string.Empty, locs, 0, false, false, 0));
            }

            _ingredientList.OptionsList = rows;
            RequestCollisionRebuild();
            return;
        }

        void Add(IngTreeNode n)
        {
            rows.Add(new IngredientRow(n.Check, n.PrimaryLocation, n.AllLocations, n.Depth, n.Craftable, n.Expanded, n.Id));
            if (n.Expanded && n.Children is not null)
                foreach (var child in n.Children)
                    Add(child);
        }

        foreach (var root in _ingRoots)
            Add(root);

        _ingredientList.OptionsList = rows;
        RequestCollisionRebuild();
    }

    /// <summary>Switches the detail panel between the ingredient tree and the shopping list.</summary>
    private void ToggleShopping()
    {
        _showShopping = !_showShopping;
        UpdateShoppingTexts();
        RebuildIngredientList();
    }

    private void UpdateShoppingTexts()
    {
        if (_shoppingButton is not null) _shoppingButton.String = CraftLoc.Get(_showShopping ? "detail.tree" : "detail.shopping");
        if (_detailIngLabel is not null) _detailIngLabel.String = CraftLoc.Get(_showShopping ? "detail.shoppinglabel" : "detail.ingredients");
    }

    private void ClearDetailPanel()
    {
        if (_detailPlaceholder is not null) _detailPlaceholder.IsVisible = true;
        if (_detailIcon is not null)        _detailIcon.IsVisible        = false;
        if (_detailName is not null)        _detailName.IsVisible        = false;
        if (_detailSub is not null)         _detailSub.IsVisible         = false;
        if (_detailMarket is not null)      _detailMarket.IsVisible      = false;
        if (_detailSep is not null)         _detailSep.IsVisible         = false;
        if (_detailIngLabel is not null)    _detailIngLabel.IsVisible    = false;
        if (_shoppingButton is not null)    _shoppingButton.IsVisible    = false;
        if (_detailCollision is not null)   _detailCollision.IsVisible   = false;
        if (_ingredientList is not null)    _ingredientList.OptionsList  = [];

        _ingRoots.Clear();
        _ingById.Clear();
        _shoppingRows = [];

        RequestCollisionRebuild();
    }

    private void PollMarketData()
    {
        if (_selectedEntry is null || Tweak?.Universalis is not { } uni) return;
        if (_detailMarket is null || !_detailMarket.IsVisible) return;

        var itemId = _selectedEntry.ItemId;

        if (uni.IsPending(itemId))
        {
            _detailMarket.String    = CraftLoc.Get("market.loading");
            _detailMarket.TextColor = MutedGrey;
            return;
        }

        if (!uni.HasData(itemId))
        {
            if (uni.HasFailed(itemId))
            {
                _detailMarket.String    = CraftLoc.Get("market.error");
                _detailMarket.TextColor = MutedGrey;
            }
            else
            {
                // A failed request may be retried once its wait is over.
                uni.RequestIfNeeded(itemId, Tweak.WorldId, urgent: true);
            }
            return;
        }

        var data = uni.Get(itemId);
        _detailMarket.String    = (data is { AveragePrice: > 0 } or { MinPrice: > 0 }
            ? CraftLoc.Fmt("market.price", data.AveragePrice.ToString("N0"), data.MinPrice.ToString("N0"))
            : CraftLoc.Get("market.none"))
            + MaterialsCost(_selectedEntry, uni, data);
        _detailMarket.TextColor = data is { AveragePrice: > 0 } ? MarketColor : MutedGrey;
    }

    /// <summary>
    /// "· Materials Xg · Margin Yg": what the ingredients of one craft cost on the market and what the craft is worth
    /// minus that. Without the margin when some ingredient has no price (the cost is then a minimum) or the result has none.
    /// </summary>
    private static string MaterialsCost(CraftableEntry entry, UniversalisService uni, UniversalisData? result)
    {
        long cost     = 0;
        var  unpriced = 0;
        foreach (var ing in entry.Ingredients)
        {
            if (uni.IsPending(ing.ItemId)) return "  ·  " + CraftLoc.Get("market.costloading");

            var price = UnitPrice(uni.Get(ing.ItemId));
            if (price <= 0) { unpriced++; continue; }
            cost += price * ing.Required;
        }

        if (cost == 0) return string.Empty;
        if (unpriced > 0) return "  ·  " + CraftLoc.Fmt("market.costmin", cost.ToString("N0"));

        var value = UnitPrice(result) * entry.ResultAmount;
        if (value <= 0) return "  ·  " + CraftLoc.Fmt("market.cost", cost.ToString("N0"));

        var margin = value - cost;
        return "  ·  " + CraftLoc.Fmt("market.margin", cost.ToString("N0"), (margin >= 0 ? "+" : "") + margin.ToString("N0"));
    }

    /// <summary>Price of one unit: the average, or the cheapest listing when there is no average.</summary>
    private static long UnitPrice(UniversalisData? data)
        => data is null ? 0 : data.AveragePrice > 0 ? (long)Math.Round(data.AveragePrice) : data.MinPrice;

    // ── Market sort ───────────────────────────────────────────────────────────

    // Sorts by market price (ascending or descending) keeping items with no price data last,
    // regardless of direction.  Items with AveragePrice == 0 (fetched but no listings) are
    // treated the same as unfetched items.
    private List<CraftableEntry> SortByMarket(List<CraftableEntry> source)
    {
        var uni      = Tweak?.Universalis;
        var priced   = source.Where(e => (uni?.Get(e.ItemId)?.AveragePrice ?? 0f) > 0f);
        var unpriced = source.Where(e => (uni?.Get(e.ItemId)?.AveragePrice ?? 0f) <= 0f);

        var sorted = _sortDescending
            ? priced.OrderByDescending(e => uni!.Get(e.ItemId)!.AveragePrice).ThenBy(e => e.Name)
            : priced.OrderBy(e => uni!.Get(e.ItemId)!.AveragePrice).ThenBy(e => e.Name);

        return [.. sorted, .. unpriced];
    }

    // ── Scrollbar helpers ─────────────────────────────────────────────────────

    // Clamps the scrollbar thumb to MinScrollThumbH after each Update().
    // The ListNode recalculates thumb height and Y internally; this runs every frame
    // to re-apply the visual minimum without changing the underlying scroll state.
    private static void EnforceScrollbarMinThumb(ScrollBarNode scrollBar)
    {
        var thumb = scrollBar.ForegroundButtonNode;
        if (thumb.Height >= MinScrollThumbH) return;

        var trackH    = scrollBar.Height;
        var scrollMax = (float)scrollBar.ScrollMaxPosition;

        thumb.Height = MinScrollThumbH;
        if (scrollMax > 0f)
            thumb.Y = scrollBar.ScrollPosition / scrollMax * (trackH - MinScrollThumbH);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        var c  = ContentStartPosition;
        var cs = ContentSize;

        var bodyH  = cs.Y - BottomBarH;   // reserve the bottom status bar
        var colTop = c.Y + TopBarH;       // columns 2+3 start under the filter bar
        var colH   = bodyH - TopBarH;     // recipe list / detail height

        // Sidebar (full body height — no top bar over column 1)
        BuildSidebar(c, new Vector2(cs.X, bodyH));

        // Divider 1: sidebar | rest (full body height)
        new VerticalLineNode { Position = new Vector2(c.X + SidebarW, c.Y), Height = bodyH, Width = DivW }
            .AttachNode(this);

        var listX      = c.X + SidebarW + DivW;
        var colsRightW = cs.X - SidebarW - DivW;

        // Top filter bar over columns 2+3
        BuildFilterBar(new Vector2(listX, c.Y), new Vector2(colsRightW, TopBarH));
        new HorizontalLineNode
        {
            Position = new Vector2(listX, colTop - 1f),
            Size     = new Vector2(colsRightW, 2f),
        }.AttachNode(this);

        // Recipe list (middle column)
        _recipeList = new ListNode<CraftableEntry, RecipeRowNode>
        {
            Position               = new Vector2(listX, colTop),
            Size                   = new Vector2(RecipeListW, colH),
            ItemSpacing            = 2f,
            ShowNoResultsPlaceholder = true,
            AllowMultipleSelection = false,
            OptionsList            = [],
        };
        _recipeList.OnItemSelected = entry =>
        {
            // In My List the totals stay on the right; a click on a My List box isn't a click on the recipe.
            if (entry is null || InCartView || DateTime.UtcNow - _cartToggledAt < CartClickWindow) return;
            ShowDetailPanel(entry);
        };
        _recipeList.AttachNode(this);

        // Divider 2: recipe list | detail panel (below the filter bar)
        var div2X = listX + RecipeListW;
        new VerticalLineNode { Position = new Vector2(div2X, colTop), Height = colH, Width = DivW }
            .AttachNode(this);

        // Detail panel (right column)
        var detailX = div2X + DivW;
        var detailW = cs.X - (detailX - c.X);
        BuildDetailPanel(new Vector2(detailX, colTop), new Vector2(detailW, colH));

        // Bottom status bar (full width)
        BuildBottomBar(new Vector2(c.X, c.Y + bodyH), new Vector2(cs.X, BottomBarH));
    }

    // ── Top filter bar ──────────────────────────────────────────────────────────

    private void BuildFilterBar(Vector2 pos, Vector2 size)
    {
        var cy = pos.Y + (size.Y - 22f) / 2f;

        _hideCraftedCheck = new CheckboxNode
        {
            String    = CraftLoc.Get("filter.hidecrafted"),
            IsChecked = _hideCrafted,
            Position  = new Vector2(pos.X + 8f, cy),
            Size      = new Vector2(150f, 22f),
        };
        _hideCraftedCheck.OnClick = isChecked => { _hideCrafted = isChecked; _pendingFilter = true; };
        _hideCraftedCheck.AttachNode(this);

        _myLevelCheck = new CheckboxNode
        {
            String      = CraftLoc.Get("filter.mylevel"),
            IsChecked   = _onlyMyLevel,
            Position    = new Vector2(pos.X + 8f + 154f, cy),
            Size        = new Vector2(150f, 22f),
            TextTooltip = CraftLoc.Get("tip.mylevel"),
        };
        _myLevelCheck.OnClick = isChecked => { _onlyMyLevel = isChecked; _pendingFilter = true; };
        _myLevelCheck.AttachNode(this);

        _lblSort = MakeLabel(pos.X + 326f, cy + 3f, 40f, CraftLoc.Get("filter.sort"), SubtitleBrown, 12);
        _sortDropdown = new StringDropDownNode
        {
            Position         = new Vector2(pos.X + 368f, cy),
            Size             = new Vector2(150f, 24f),
            MaxListOptions   = SortModes.Count,
            GetLabelFunction = key => SortLabel(key),
        };
        _sortDropdown.OnOptionSelected = s => { _sortMode = s; _pendingFilter = true; };
        _sortDropdown.AttachNode(this);

        // Its options are set again when the language changes, so they are set only once it is attached: option buttons
        // built before attaching are left behind in the window's node list when replaced, and closing the window then crashes.
        _sortDropdown.Options        = SortModes;
        _sortDropdown.SelectedOption = _sortMode;

        // Sort-direction toggle (native up/down arrow button)
        _sortDirBtn = new CircleButtonNode
        {
            Icon        = _sortDescending ? CircleButtonIcon.ArrowDown : CircleButtonIcon.UpArrow,
            Position    = new Vector2(pos.X + 522f, cy),
            Size        = new Vector2(24f, 24f),
            TextTooltip = CraftLoc.Get("tip.sortdir"),
        };
        _sortDirBtn.OnClick = () =>
        {
            _sortDescending = !_sortDescending;
            if (_sortDirBtn is not null)
                _sortDirBtn.Icon = _sortDescending ? CircleButtonIcon.ArrowDown : CircleButtonIcon.UpArrow;
            _pendingFilter = true;
        };
        _sortDirBtn.AttachNode(this);

        _lblClass = MakeLabel(pos.X + 558f, cy + 3f, 46f, CraftLoc.Get("filter.class"), SubtitleBrown, 12);
        _classDropdown = new StringDropDownNode
        {
            Position         = new Vector2(pos.X + 606f, cy),
            Size             = new Vector2(170f, 24f),
            MaxListOptions   = 10,
            GetLabelFunction = key => key == AllClasses ? CraftLoc.Get("class.all") : key,
        };
        _classDropdown.OnOptionSelected = s => { _classFilter = s; _pendingFilter = true; };
        _classDropdown.AttachNode(this);

        // Its options change after each rescan, so they are set only once it is attached: option buttons built before
        // attaching are left behind in the window's node list when replaced, and closing the window then crashes.
        _classDropdown.Options        = [AllClasses];
        _classDropdown.SelectedOption = _classFilter;
    }

    private static string SortLabel(string key) => key switch
    {
        SortStatus  => CraftLoc.Get("sort.status"),
        SortLevel   => CraftLoc.Get("sort.level"),
        SortCraft   => CraftLoc.Get("sort.craft"),
        SortMissing => CraftLoc.Get("sort.missing"),
        SortMarket  => CraftLoc.Get("sort.market"),
        _           => CraftLoc.Get("sort.name"),
    };

    /// <summary>Re-applies the current window language to the static (built-once) labels.</summary>
    private void ApplyLanguage()
    {
        if (_hdrStatus is not null)        _hdrStatus.String       = CraftLoc.Get("hdr.status");
        if (_hdrCategory is not null)      _hdrCategory.String     = CraftLoc.Get("hdr.category");
        if (_lblSort is not null)          _lblSort.String         = CraftLoc.Get("filter.sort");
        if (_lblClass is not null)         _lblClass.String        = CraftLoc.Get("filter.class");
        if (_hideCraftedCheck is not null) _hideCraftedCheck.String = CraftLoc.Get("filter.hidecrafted");
        if (_searchInput is not null)      _searchInput.PlaceholderString = CraftLoc.Get("search");
        if (_refreshBtn is not null)       _refreshBtn.TextTooltip = CraftLoc.Get("tip.refresh");
        if (_sortDirBtn is not null)       _sortDirBtn.TextTooltip = CraftLoc.Get("tip.sortdir");
        if (_helpButton is not null)       _helpButton.TextTooltip = CraftLoc.Get("tip.help");
        if (_myLevelCheck is not null)     _myLevelCheck.String    = CraftLoc.Get("filter.mylevel");
        UpdateShoppingTexts();
        if (_detailPlaceholder is not null) _detailPlaceholder.String = CraftLoc.Get("detail.placeholder");
        if (_clearCartBtn is not null)      _clearCartBtn.String      = CraftLoc.Get("cart.clear");
        if (_cartTitle is not null)         _cartTitle.String         = CraftLoc.Get("cart.title");
        if (_cartDeductCheck is not null)   _cartDeductCheck.String   = CraftLoc.Get("cart.deduct");
        if (_cartColItem is not null)       _cartColItem.String       = CraftLoc.Get("cart.col.item");
        if (_cartColQty is not null)        _cartColQty.String        = CraftLoc.Get("cart.col.qty");
        if (_cartColPrice is not null)      _cartColPrice.String      = CraftLoc.Get("cart.col.price");
        if (_cartEmpty is not null)         _cartEmpty.String         = CraftLoc.Get("cart.empty");

        // Force the sort dropdown to re-render its localized labels.
        if (_sortDropdown is not null)
        {
            _sortDropdown.Options        = SortModes;
            _sortDropdown.SelectedOption = _sortMode;
        }
    }

    // ── Bottom status bar ─────────────────────────────────────────────────────────

    private void BuildBottomBar(Vector2 pos, Vector2 size)
    {
        new HorizontalLineNode
        {
            Position = new Vector2(pos.X, pos.Y),
            Size     = new Vector2(size.X, 2f),
        }.AttachNode(this);

        var cy = pos.Y + (size.Y - 22f) / 2f;

        _helpButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(pos.X + 4f, cy),
            Size        = new Vector2(22f, 22f),
            TextTooltip = CraftLoc.Get("tip.help"),
        };
        _helpButton.OnClick = () => Tweak?.OpenHelp();
        _helpButton.AttachNode(this);

        // Right-aligned counters
        var textY = pos.Y + (size.Y - 16f) / 2f;
        _bottomCrafted = new TextNode
        {
            Position      = new Vector2(pos.X + size.X - 236f, textY),
            Size          = new Vector2(230f, 16f),
            TextColor     = BodyGrey,
            FontSize      = 12,
            AlignmentType = AlignmentType.Right,
        };
        _bottomCrafted.AttachNode(this);

        _bottomShowing = new TextNode
        {
            Position      = new Vector2(pos.X + size.X - 480f, textY),
            Size          = new Vector2(240f, 16f),
            TextColor     = MutedGrey,
            FontSize      = 12,
            AlignmentType = AlignmentType.Right,
        };
        _bottomShowing.AttachNode(this);
    }

    private void UpdateBottomBar()
    {
        if (_bottomShowing is not null)
            _bottomShowing.String = CraftLoc.Fmt("bottom.showing", _filtered.Count);

        if (_bottomCrafted is not null)
        {
            var crafted = _entries.Count(e => e.IsCrafted);
            _bottomCrafted.String = CraftLoc.Fmt("bottom.crafted", crafted, _entries.Count);
        }
    }

    // ── Sidebar ───────────────────────────────────────────────────────────────

    private void BuildSidebar(Vector2 c, Vector2 cs)
    {
        var y = c.Y + StatusTopY;

        // ── Search + refresh row ──────────────────────────────────────────────
        // Search fills the row; a small refresh button sits at the far right.
        const float btnSz = 28f;
        const float gap   = 5f;

        var inputW = SidebarW - gap * 3 - btnSz;
        _searchInput = new TextInputNode
        {
            Position          = new Vector2(c.X + gap, y),
            Size              = new Vector2(inputW, SearchBarH),
            PlaceholderString = CraftLoc.Get("search"),
            MaxCharacters     = 64,
        };
        _searchInput.OnInputReceived = str =>
        {
            _searchText    = str.ToString();
            _pendingFilter = true;
        };
        _searchInput.OnInputComplete = str =>
        {
            _searchText    = str.ToString();
            _pendingFilter = true;
        };
        _searchInput.AttachNode(this);

        _refreshBtn = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.Refresh,
            Position    = new Vector2(c.X + gap * 2 + inputW, y + 1f),
            Size        = new Vector2(btnSz, btnSz),
            TextTooltip = CraftLoc.Get("tip.refresh"),
        };
        _refreshBtn.OnClick = () =>
        {
            _pendingRestoreId = _selectedEntry?.RecipeId ?? 0;
            _keepScroll       = true;
            _pendingRefresh   = true;
        };
        _refreshBtn.AttachNode(this);

        y += SearchBarH + gap + 2f;

        // ── STATUS section ────────────────────────────────────────────────────
        _hdrStatus = MakeTitle(c.X + 6f, y, SidebarW - 12f, CraftLoc.Get("hdr.status"));
        y += 26f;

        AddStatusItem(c, ref y, "all",      CraftLoc.Get("status.all"));
        AddStatusItem(c, ref y, "ready",    CraftLoc.Get("status.ready"));
        AddStatusItem(c, ref y, "near",     CraftLoc.Get("status.almost"));
        AddStatusItem(c, ref y, "notReady", CraftLoc.Get("status.cant"));
        AddStatusItem(c, ref y, CartView,   CraftLoc.Get("status.cart"));

        y += 6f;
        new HorizontalLineNode { Position = new Vector2(c.X + 6f, y), Size = new Vector2(SidebarW - 14f, 2f) }
            .AttachNode(this);
        y += SepH;

        // ── CATEGORY section ──────────────────────────────────────────────────
        _hdrCategory = MakeTitle(c.X + 6f, y, SidebarW - 12f, CraftLoc.Get("hdr.category"));
        y += 26f;

        var catH = cs.Y - (y - c.Y) - 2f;
        _catListNode = new ListNode<CategoryEntry, CategoryRowNode>
        {
            Position               = new Vector2(c.X, y),
            Size                   = new Vector2(SidebarW, catH),
            ItemSpacing            = 0f,
            AllowMultipleSelection = false,
            OptionsList            = [],
        };
        _catListNode.OnItemSelected = cat =>
        {
            foreach (var si in _statusItems) { si.IsSelected = false; si.TextNode.TextColor = BodyGrey; }
            _catListNode?.ClearSelection();
            _selectedView  = cat.Key;
            _pendingFilter = true;
        };
        _catListNode.AttachNode(this);
    }

    // ── Detail panel ──────────────────────────────────────────────────────────

    private void BuildDetailPanel(Vector2 pos, Vector2 size)
    {
        // Placeholder
        _detailPlaceholder = new TextNode
        {
            String    = CraftLoc.Get("detail.placeholder"),
            Position  = new Vector2(pos.X + 12f, pos.Y + size.Y / 2f - 8f),
            Size      = new Vector2(size.X - 24f, 18f),
            TextColor = MutedGrey,
            FontSize  = 13,
        };
        _detailPlaceholder.AttachNode(this);

        // Recipe icon
        _detailIcon = new IconImageNode
        {
            FitTexture = true,
            Position   = new Vector2(pos.X + 8f, pos.Y + (DetailHeaderH - DetailIconSz) / 2f),
            Size       = new Vector2(DetailIconSz, DetailIconSz),
            IsVisible  = false,
        };
        _detailIcon.AttachNode(this);

        // Text rows (right of icon)
        var textX = pos.X + 8f + DetailIconSz + 8f;
        var textW = size.X - 8f - DetailIconSz - 16f;

        _detailName = new TextNode
        {
            Position  = new Vector2(textX, pos.Y + 8f),
            Size      = new Vector2(textW, 24f),
            TextColor = CategoryGold,
            FontSize  = 16,
            IsVisible = false,
        };
        _detailName.AttachNode(this);

        _detailSub = new TextNode
        {
            Position  = new Vector2(textX, pos.Y + 34f),
            Size      = new Vector2(textW, 18f),
            TextColor = SubtitleBrown,
            FontSize  = 12,
            IsVisible = false,
        };
        _detailSub.AttachNode(this);

        _detailMarket = new TextNode
        {
            Position  = new Vector2(textX, pos.Y + 56f),
            Size      = new Vector2(textW, 16f),
            TextColor = MarketColor,
            FontSize  = 11,
            IsVisible = false,
        };
        _detailMarket.AttachNode(this);

        // Invisible hit area over the header (icon + name) so the result item shows the
        // native tooltip on hover and the native context menu on right-click, like rows do.
        _detailCollision = new CollisionNode
        {
            CollisionType = CollisionType.Hit,
            Position      = new Vector2(pos.X + 4f, pos.Y + 4f),
            Size          = new Vector2(size.X - 8f, DetailHeaderH - 8f),
            IsVisible     = false,
        };
        _detailCollision.AddEvent(AtkEventType.MouseDown, (_, _, _, _, eventData) =>
        {
            if (eventData is not null && eventData->MouseData.ButtonId == 1 && _selectedEntry is not null)
                OnRecipeRightClick(_selectedEntry);
        });
        _detailCollision.AttachNode(this);

        // Separator
        _detailSep = new HorizontalLineNode
        {
            Position  = new Vector2(pos.X + 4f, pos.Y + DetailHeaderH),
            Size      = new Vector2(size.X - 8f, 2f),
            IsVisible = false,
        };
        _detailSep.AttachNode(this);

        // Ingredients label
        var labelY = pos.Y + DetailHeaderH + 4f;
        _detailIngLabel = new TextNode
        {
            String    = CraftLoc.Get("detail.ingredients"),
            Position  = new Vector2(pos.X + 8f, labelY),
            Size      = new Vector2(size.X - 16f, 18f),
            TextColor = SubtitleBrown,
            FontSize  = 11,
            IsVisible = false,
        };
        _detailIngLabel.AttachNode(this);

        // Switch between the ingredient tree and the shopping list, right of the label.
        _shoppingButton = new TextButtonNode
        {
            String    = CraftLoc.Get("detail.shopping"),
            Position  = new Vector2(pos.X + size.X - 178f, labelY - 2f),
            Size      = new Vector2(170f, 26f),
            IsVisible = false,
        };
        _shoppingButton.OnClick = ToggleShopping;
        _shoppingButton.AttachNode(this);
        UpdateShoppingTexts();

        // Ingredient list
        var listY = labelY + 26f;
        _ingredientList = new ListNode<IngredientRow, IngredientRowNode>
        {
            Position               = new Vector2(pos.X, listY),
            Size                   = new Vector2(size.X, size.Y - (listY - pos.Y)),
            ItemSpacing            = 2f,
            AllowMultipleSelection = false,
            OptionsList            = [],
        };
        _ingredientList.OnItemSelected = ToggleIngredient; // expand/collapse craftable sub-ingredients
        _ingredientList.AttachNode(this);

        BuildCartPanel(pos, size);
    }

    /// <summary>The totals of My List, in the place of the recipe details while that view is open.</summary>
    private void BuildCartPanel(Vector2 pos, Vector2 size)
    {
        const float totalH = 24f;
        var y = pos.Y + 6f;

        _cartTitle = MakeLabel(pos.X + 8f, y, size.X - 16f, CraftLoc.Get("cart.title"), CategoryGold, 16);
        _cartTitle.IsVisible = false;
        y += 26f;

        _cartDeductCheck = new CheckboxNode
        {
            String    = CraftLoc.Get("cart.deduct"),
            IsChecked = Tweak?.Current.CartDeductOwned ?? true,
            Position  = new Vector2(pos.X + 8f, y),
            Size      = new Vector2(220f, 22f),
            IsVisible = false,
        };
        _cartDeductCheck.OnClick = isChecked =>
        {
            if (Tweak is null || Tweak.Current.CartDeductOwned == isChecked) return;
            Tweak.Current.CartDeductOwned = isChecked;
            Tweak.SaveFilters();
            RebuildCart(keepScroll: true);
        };
        _cartDeductCheck.AttachNode(this);

        // Right of the deduct box, not under the recipe list: resizing that list would dispose its rows (and a counter
        // being typed in) while the window stays open.
        _clearCartBtn = new TextButtonNode
        {
            String    = CraftLoc.Get("cart.clear"),
            Position  = new Vector2(pos.X + size.X - 168f, y - 2f),
            Size      = new Vector2(160f, 26f),
            IsVisible = false,
        };
        _clearCartBtn.OnClick = ClearCart;
        _clearCartBtn.AttachNode(this);
        y += 28f;

        // Column headers, lined up with the columns of the rows (a row is the list width minus its scroll bar).
        var rowW   = size.X - 16f;
        var qtyX   = pos.X + rowW - IngredientRowNode.QtyW - IngredientRowNode.LocW - IngredientRowNode.PriceW - 4f;
        var priceX = pos.X + rowW - IngredientRowNode.PriceW - 4f;
        _cartColItem  = MakeLabel(pos.X + IngredientRowNode.NameX0, y, qtyX - pos.X - IngredientRowNode.NameX0, CraftLoc.Get("cart.col.item"), SubtitleBrown, 11);
        _cartColQty   = MakeLabel(qtyX, y, IngredientRowNode.QtyW + IngredientRowNode.LocW, CraftLoc.Get("cart.col.qty"), SubtitleBrown, 11);
        _cartColPrice = MakeLabel(priceX, y, IngredientRowNode.PriceW, CraftLoc.Get("cart.col.price"), SubtitleBrown, 11);
        _cartColPrice.AlignmentType = AlignmentType.Right;
        _cartColItem.IsVisible = _cartColQty.IsVisible = _cartColPrice.IsVisible = false;
        y += 20f;

        _cartList = new ListNode<IngredientRow, IngredientRowNode>
        {
            Position               = new Vector2(pos.X, y),
            Size                   = new Vector2(size.X, pos.Y + size.Y - totalH - 4f - y),
            ItemSpacing            = 2f,
            AllowMultipleSelection = false,
            OptionsList            = [],
            IsVisible              = false,
        };
        _cartList.OnItemSelected = ToggleCartIngredient;
        _cartList.AttachNode(this);

        _cartTotal = new TextNode
        {
            Position      = new Vector2(pos.X + 8f, pos.Y + size.Y - totalH),
            Size          = new Vector2(size.X - 20f, totalH - 4f),
            TextColor     = MarketColor,
            FontSize      = 13,
            AlignmentType = AlignmentType.Right,
            IsVisible     = false,
        };
        _cartTotal.AttachNode(this);

        _cartEmpty = new TextNode
        {
            String    = CraftLoc.Get("cart.empty"),
            Position  = new Vector2(pos.X + 12f, pos.Y + size.Y / 2f - 8f),
            Size      = new Vector2(size.X - 24f, 18f),
            TextColor = MutedGrey,
            FontSize  = 13,
            IsVisible = false,
        };
        _cartEmpty.AttachNode(this);
    }

    // ── Sidebar helpers ───────────────────────────────────────────────────────

    private void AddStatusItem(Vector2 c, ref float y, string id, string label)
    {
        var item = new SelectableTextNode
        {
            String     = label,
            Position   = new Vector2(c.X, y),
            Size       = new Vector2(SidebarW, StatusRowH),
            IsSelected = id == _selectedView,
        };
        item.TextNode.TextColor = item.IsSelected ? CategoryGold : BodyGrey;
        item.TextNode.FontSize  = 12;

        var capturedId = id;
        item.OnClick = _ =>
        {
            var idxMap = new[] { "all", "ready", "near", "notReady", CartView };
            for (var i = 0; i < _statusItems.Count && i < idxMap.Length; i++)
            {
                _statusItems[i].IsSelected         = idxMap[i] == capturedId;
                _statusItems[i].TextNode.TextColor = _statusItems[i].IsSelected ? CategoryGold : BodyGrey;
            }
            _catListNode?.ClearSelection();
            _selectedView  = capturedId;
            _pendingFilter = true;
        };

        item.AttachNode(this);
        _statusItems.Add(item);
        y += StatusRowH;
    }

    private TextNode MakeLabel(float x, float y, float w, string text, Vector4 color, uint size = 14)
    {
        var node = new TextNode
        {
            String    = text,
            Position  = new Vector2(x, y),
            Size      = new Vector2(w, size + 6),
            TextColor = color,
            FontSize  = size,
        };
        node.AttachNode(this);
        return node;
    }

    /// <summary>The gear in the title bar, left of the window's close button: opens the settings window.</summary>
    private void BuildSettingsButton()
    {
        var gear = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = new Vector2(Size.X - 70f, 9f),
            Size        = new Vector2(24f, 24f),
            TextTooltip = CraftLoc.Get("tip.settings"),
        };
        gear.OnClick = () => Tweak?.ToggleSettings();
        gear.AttachNode(this);
    }

    /// <summary>Section header in FFXIV's condensed title font (TrumpGothic), like "CATEGORY".</summary>
    private TextNode MakeTitle(float x, float y, float w, string text)
    {
        var node = new TextNode
        {
            String    = text,
            Position  = new Vector2(x, y),
            Size      = new Vector2(w, 24f),
            TextColor = CategoryGold,
            FontType  = UiFont.Heading,
            FontSize  = UiFont.Size(20),
            CharSpacing = UiFont.Spacing,
        };
        node.AttachNode(this);
        return node;
    }
}
