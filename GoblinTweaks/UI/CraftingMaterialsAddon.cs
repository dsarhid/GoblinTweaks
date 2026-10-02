using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using GoblinTweaks.Localization;
using GoblinTweaks.Tweaks;
using GoblinTweaks.UI.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// FFXIV-native Crafting Materials window (native AtkUnitBase addon).
/// Three columns: sidebar (search + status + category) | recipe list | ingredient detail panel.
/// Phase 4 adds: native item tooltips, search, refresh button, Universalis market data.
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
        public int                      Depth;
        public bool                     Craftable;
        public bool                     Expanded;
        public List<IngTreeNode>?       Children;
        public int                      Id;
    }
    private readonly List<IngTreeNode>            _ingRoots   = [];
    private readonly Dictionary<int, IngTreeNode> _ingById   = new();
    private int                                   _ingIdSeq;

    private bool   _pendingRefresh = true;
    private bool   _pendingFilter  = false;
    private int    _collisionRebuildFrames;
    private uint   _pendingOpenRecipeId; // deferred crafting-log open (0 = none)
    private string _selectedView   = "ready";

    // Filter / sort state (driven by the top bar)
    private bool   _hideCrafted;
    private string _sortMode       = SortName;
    private string _classFilter    = AllClasses;
    private bool   _sortDescending;

    private const string SortName    = "Name";
    private const string SortStatus  = "Craft status";
    private const string SortLevel   = "Item level";
    private const string SortMissing = "Fewest missing";
    private const string AllClasses  = "All classes";

    // ── Sidebar node refs ─────────────────────────────────────────────────────
    private TextInputNode?     _searchInput;
    private CircleButtonNode?  _refreshBtn;
    private readonly List<SelectableTextNode> _statusItems = [];
    private ListNode<CraftableEntry,  RecipeRowNode>?   _recipeList;
    private ListNode<CategoryEntry,   CategoryRowNode>? _catListNode;

    // ── Filter bar + bottom bar node refs ──────────────────────────────────────
    private CheckboxNode?      _hideCraftedCheck;
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
    private CraftingHelpAddon? _helpAddon;

    // ── Detail panel node refs ────────────────────────────────────────────────
    private TextNode?           _detailPlaceholder;
    private IconImageNode?      _detailIcon;
    private TextNode?           _detailName;
    private TextNode?           _detailSub;
    private TextNode?           _detailMarket;
    private HorizontalLineNode? _detailSep;
    private TextNode?           _detailIngLabel;
    private CollisionNode?      _detailCollision;
    private ListNode<IngredientRow, IngredientRowNode>? _ingredientList;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);
        _selectedEntry  = null;
        _pendingRefresh = true;
        BuildLayout();

        // Route row right-clicks: ingredients → native item menu; recipes → item menu if
        // the result is in your bags, otherwise the native Crafting Log for that recipe.
        RecipeRowNode.OnItemRightClick     = OnRecipeRightClick;
        IngredientRowNode.OnItemRightClick = id => OpenItemContextMenu(id);
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        if (_pendingRefresh) LoadData();
        if (_pendingFilter)  ApplyFilter();

        _recipeList?.Update();
        _catListNode?.Update();
        _ingredientList?.Update();

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

        // Refresh market data display when Universalis responds
        PollMarketData();
    }

    /// <summary>Queues a native collision-node-list rebuild for the next few frames.</summary>
    private void RequestCollisionRebuild() => _collisionRebuildFrames = 3;

    /// <summary>Re-scans inventories and re-analyses recipes next frame (e.g. after a settings change).</summary>
    public void RequestRefresh() => _pendingRefresh = true;

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _statusItems.Clear();
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

        _helpAddon?.Close();
        _helpAddon = null;

        RecipeRowNode.OnItemRightClick     = null;
        IngredientRowNode.OnItemRightClick = null;

        base.OnFinalize(addon);
    }

    // ── Native item context menu ────────────────────────────────────────────────

    /// <summary>Opens the game's native inventory item menu. Returns false if not in bags.</summary>
    private bool OpenItemContextMenu(uint itemId)
    {
        if (itemId == 0) return false;

        var (type, slot) = FindPlayerItemSlot(itemId);
        if (slot < 0) return false; // the native inventory menu needs a real slot

        var agent = AgentInventoryContext.Instance();
        if (agent is null) return false;

        agent->OpenForItemSlot(type, slot, 0, (uint)AddonId);
        return true;
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

    private static (InventoryType Type, int Slot) FindPlayerItemSlot(uint itemId)
    {
        var mgr = InventoryManager.Instance();
        if (mgr is null) return (InventoryType.Inventory1, -1);

        ReadOnlySpan<InventoryType> containers =
        [
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.Crystals,
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
        CraftLoc.Lang = Tweak.WindowUiLang;
        Title         = CraftLoc.Get("title");
        ApplyLanguage();

        try
        {
            _snapshot = Tweak.ScanInventories();
            _entries  = Tweak.Analyze(_snapshot);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "CraftingMaterialsAddon: LoadData failed");
            _entries = [];
        }

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
        {
            _classFilter = AllClasses;
            _classDropdown.SelectedOption = AllClasses;
        }
    }

    private void UpdateStatusCounts()
    {
        var ready    = _entries.Count(e => e.Status is CraftStatus.Ready or CraftStatus.ReadyElsewhere);
        var near     = _entries.Count(e => e.Status == CraftStatus.NearComplete);
        var notReady = _entries.Count(e => e.Status == CraftStatus.NotReady);

        if (_statusItems.Count >= 3)
        {
            _statusItems[0].String = $"{CraftLoc.Get("status.ready")} ({ready})";
            _statusItems[1].String = $"{CraftLoc.Get("status.almost")} ({near})";
            _statusItems[2].String = $"{CraftLoc.Get("status.cant")} ({notReady})";
        }
    }

    private void UpdateCategoryList()
    {
        var cats = _entries
            .Where(e => !string.IsNullOrEmpty(e.CategoryName))
            .GroupBy(e => e.CategoryName)
            .Select(g => new CategoryEntry(g.Key, g.Count(e => e.IsCrafted), g.Count()))
            .OrderBy(c => c.Name)
            .ToList();

        if (_catListNode is not null)
            _catListNode.OptionsList = cats;

        RequestCollisionRebuild();
    }

    private void ApplyFilter()
    {
        _pendingFilter = false;
        _selectedEntry = null;
        ClearDetailPanel();

        _filtered = _selectedView switch
        {
            "ready"    => _entries.Where(e => e.Status is CraftStatus.Ready or CraftStatus.ReadyElsewhere).ToList(),
            "near"     => _entries.Where(e => e.Status == CraftStatus.NearComplete).ToList(),
            "notReady" => _entries.Where(e => e.Status == CraftStatus.NotReady).ToList(),
            var v when v.StartsWith("cat:")
                       => _entries.Where(e => e.CategoryName == v[4..]).ToList(),
            _          => _entries,
        };

        // Search text filter
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var term = _searchText.Trim();
            _filtered = _filtered.Where(e =>
                e.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.CraftClass.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Ingredients.Any(i => i.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        // Top-bar filters
        if (_hideCrafted)
            _filtered = _filtered.Where(e => !e.IsCrafted).ToList();

        if (_classFilter != AllClasses)
            _filtered = _filtered.Where(e => e.CraftClass == _classFilter).ToList();

        // Sort (base ascending order, then reversed if the direction toggle is set)
        var ordered = _sortMode switch
        {
            SortStatus  => _filtered.OrderBy(e => (int)e.Status).ThenBy(e => e.Name),
            SortLevel   => _filtered.OrderBy(e => e.ItemLevel).ThenBy(e => e.Name),
            SortMissing => _filtered.OrderBy(e => e.MissingCount).ThenBy(e => e.Name),
            _           => _filtered.OrderBy(e => e.Name),
        };
        _filtered = ordered.ToList();
        if (_sortDescending)
            _filtered.Reverse();

        if (_recipeList is not null)
            _recipeList.OptionsList = _filtered;

        UpdateBottomBar();
        RequestCollisionRebuild();
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
        if (_detailSub is not null)  { _detailSub.String = sub; _detailSub.IsVisible = !string.IsNullOrEmpty(sub); }

        // Request Universalis and show "loading…" immediately
        if (Tweak?.Universalis is { } uni)
        {
            uni.RequestIfNeeded(entry.ItemId, Tweak.WorldId);
            if (_detailMarket is not null)
            {
                _detailMarket.String    = uni.IsPending(entry.ItemId) ? CraftLoc.Get("market.loading") : string.Empty;
                _detailMarket.IsVisible = true;
            }
        }

        if (_detailSep is not null)      _detailSep.IsVisible      = true;
        if (_detailIngLabel is not null)  _detailIngLabel.IsVisible = true;

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

    private IngTreeNode MakeIngNode(IngredientCheck ing, int depth)
    {
        var locs = _snapshot.Locations(ing.ItemId).ToList();
        var node = new IngTreeNode
        {
            Check           = ing,
            PrimaryLocation = locs.Count > 0 ? locs[0].Location : string.Empty,
            Depth           = depth,
            Craftable       = depth < MaxIngDepth && (Tweak?.IsCraftable(ing.ItemId) ?? false),
            Id              = ++_ingIdSeq,
        };
        _ingById[node.Id] = node;
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

        void Add(IngTreeNode n)
        {
            rows.Add(new IngredientRow(n.Check, n.PrimaryLocation, n.Depth, n.Craftable, n.Expanded, n.Id));
            if (n.Expanded && n.Children is not null)
                foreach (var child in n.Children)
                    Add(child);
        }

        foreach (var root in _ingRoots)
            Add(root);

        _ingredientList.OptionsList = rows;
        RequestCollisionRebuild();
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
        if (_detailCollision is not null)   _detailCollision.IsVisible   = false;
        if (_ingredientList is not null)    _ingredientList.OptionsList  = [];

        _ingRoots.Clear();
        _ingById.Clear();

        RequestCollisionRebuild();
    }

    private void PollMarketData()
    {
        if (_selectedEntry is null || Tweak?.Universalis is not { } uni) return;
        if (_detailMarket is null || !_detailMarket.IsVisible) return;

        var itemId = _selectedEntry.ItemId;

        if (uni.IsPending(itemId))
        {
            _detailMarket.String = CraftLoc.Get("market.loading");
            return;
        }

        if (!uni.HasData(itemId)) return;

        var data = uni.Get(itemId);
        _detailMarket.String    = data is { AveragePrice: > 0 } or { MinPrice: > 0 }
            ? CraftLoc.Fmt("market.price", data.AveragePrice.ToString("N0"), data.MinPrice.ToString("N0"))
            : CraftLoc.Get("market.none");
        _detailMarket.TextColor = data is { AveragePrice: > 0 } ? MarketColor : MutedGrey;
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
        _recipeList.OnItemSelected = entry => { if (entry is not null) ShowDetailPanel(entry); };
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

        _lblSort = MakeLabel(pos.X + 170f, cy + 3f, 40f, CraftLoc.Get("filter.sort"), SubtitleBrown, 12);
        _sortDropdown = new StringDropDownNode
        {
            Position         = new Vector2(pos.X + 212f, cy),
            Size             = new Vector2(150f, 24f),
            MaxListOptions   = 6,
            Options          = [SortName, SortStatus, SortLevel, SortMissing],
            SelectedOption   = _sortMode,
            GetLabelFunction = key => SortLabel(key),
        };
        _sortDropdown.OnOptionSelected = s => { _sortMode = s; _pendingFilter = true; };
        _sortDropdown.AttachNode(this);

        // Sort-direction toggle (native up/down arrow button)
        _sortDirBtn = new CircleButtonNode
        {
            Icon        = _sortDescending ? CircleButtonIcon.ArrowDown : CircleButtonIcon.UpArrow,
            Position    = new Vector2(pos.X + 366f, cy),
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

        _lblClass = MakeLabel(pos.X + 402f, cy + 3f, 46f, CraftLoc.Get("filter.class"), SubtitleBrown, 12);
        _classDropdown = new StringDropDownNode
        {
            Position         = new Vector2(pos.X + 450f, cy),
            Size             = new Vector2(170f, 24f),
            MaxListOptions   = 10,
            Options          = [AllClasses],
            SelectedOption   = _classFilter,
            GetLabelFunction = key => key == AllClasses ? CraftLoc.Get("class.all") : key,
        };
        _classDropdown.OnOptionSelected = s => { _classFilter = s; _pendingFilter = true; };
        _classDropdown.AttachNode(this);
    }

    private static string SortLabel(string key) => key switch
    {
        SortStatus  => CraftLoc.Get("sort.status"),
        SortLevel   => CraftLoc.Get("sort.level"),
        SortMissing => CraftLoc.Get("sort.missing"),
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
        if (_detailIngLabel is not null)   _detailIngLabel.String  = CraftLoc.Get("detail.ingredients");
        if (_detailPlaceholder is not null) _detailPlaceholder.String = CraftLoc.Get("detail.placeholder");

        // Force the sort dropdown to re-render its localized labels.
        if (_sortDropdown is not null)
        {
            _sortDropdown.Options        = [SortName, SortStatus, SortLevel, SortMissing];
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
        _helpButton.OnClick = OpenHelp;
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

    private void OpenHelp()
    {
        _helpAddon ??= new CraftingHelpAddon
        {
            InternalName = "GtkCraftMatHelp",
            Title        = CraftLoc.Get("help.title"),
            Size         = new Vector2(780f, 560f),
        };
        _helpAddon.Open();
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
        _refreshBtn.OnClick = () => _pendingRefresh = true;
        _refreshBtn.AttachNode(this);

        y += SearchBarH + gap + 2f;

        // ── STATUS section ────────────────────────────────────────────────────
        _hdrStatus = MakeTitle(c.X + 6f, y, SidebarW - 12f, CraftLoc.Get("hdr.status"));
        y += 26f;

        AddStatusItem(c, ref y, "ready",    CraftLoc.Get("status.ready"));
        AddStatusItem(c, ref y, "near",     CraftLoc.Get("status.almost"));
        AddStatusItem(c, ref y, "notReady", CraftLoc.Get("status.cant"));

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
            _selectedView  = $"cat:{cat.Name}";
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

        // Ingredient list
        var listY = labelY + 20f;
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
            var idxMap = new[] { "ready", "near", "notReady" };
            for (var i = 0; i < _statusItems.Count && i < 3; i++)
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

    /// <summary>Section header in FFXIV's condensed title font (TrumpGothic), like "CATEGORY".</summary>
    private TextNode MakeTitle(float x, float y, float w, string text)
    {
        var node = new TextNode
        {
            String    = text,
            Position  = new Vector2(x, y),
            Size      = new Vector2(w, 24f),
            TextColor = CategoryGold,
            FontType  = FontType.TrumpGothic,
            FontSize  = 20,
        };
        node.AttachNode(this);
        return node;
    }
}
