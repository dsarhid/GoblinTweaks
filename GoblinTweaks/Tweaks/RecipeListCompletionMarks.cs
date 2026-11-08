using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using GoblinTweaks.UI;
using KamiToolKit.Nodes;
using CraftTypeRow = Lumina.Excel.Sheets.CraftType;
using RecipeRow = Lumina.Excel.Sheets.Recipe;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Adds the Crafting Log check mark to every row of the "Recipes List" window (RecipeProductList)
/// whose recipe was crafted at least once, and optionally a "!" mark to the ones never crafted.
/// </summary>
/// <remarks>
/// Built to survive game patches: no hooks or signatures of its own, rows are matched by text
/// against game data, and an unexpected window layout just stops the marks instead of crashing.
/// </remarks>
[Tweak(TweakCategory.Crafting)]
public sealed unsafe class RecipeListCompletionMarks : Tweak<RecipeListCompletionMarks.Options>
{
    public sealed class Options
    {
        /// <summary>Mark an item if any class crafted it, not only the class of its section.</summary>
        public bool AnyClass { get; set; }

        /// <summary>Check mark on the recipes already crafted.</summary>
        public bool MarkCrafted { get; set; } = true;

        /// <summary>"!" mark on the recipes never crafted.</summary>
        public bool MarkMissing { get; set; }

        /// <summary>Hide the rows of the recipes already crafted.</summary>
        public bool HideCrafted { get; set; }

        /// <summary>Hide the rows of the recipes that don't count for the crafting log (master, quest, delivery...).</summary>
        public bool HideNotInLog { get; set; }
    }

    private const string AddonName = "RecipeProductList";
    private const uint TreeListNodeId = 21;
    private const uint CheckNodeId = 0x4742_0001;
    private const uint MissingNodeId = 0x4742_0002;

    // The check mark used by the game's logs (GatheringNote, part #2). "Gethering" is the real file name.
    private static readonly TexturePart CheckMark = new("ui/uld/GetheringNoteBook.tex", 2, 90, 22, 22, 22, 22);
    private const short CheckX = 19; // bottom-right corner of the row icon
    private const short CheckY = 19;

    // The gold "!" diamond of the same texture, the size of the check glyph and centred where it is drawn.
    private static readonly TexturePart MissingMark = new("ui/uld/GetheringNoteBook.tex", 47, 111, 17, 17, 16, 16);
    private const short MissingX = 22;
    private const short MissingY = 21;

    // Whether a recipe was crafted is asked again after this long (crafting one while the window is open).
    private static readonly TimeSpan CraftedCacheLife = TimeSpan.FromSeconds(2);

    private readonly Dictionary<(uint CraftType, string Item), uint[]> _recipesByClass = [];
    private readonly Dictionary<string, uint[]> _recipesByItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _craftTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(nint Renderer, uint NodeId), NativeImageNode> _marks = [];
    private readonly Dictionary<(uint CraftType, string Item), bool?> _crafted = [];
    private DateTime _craftedUntil = DateTime.MinValue;
    private SimpleSettingsAddon? _settingsAddon;
    private bool _layoutWarningShown;

    protected internal override void Enable()
    {
        BuildIndex();
        _layoutWarningShown = false;
        _crafted.Clear();
        _settingsAddon = NewSettingsAddon();

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnPreDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    protected internal override void Disable()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, OnPreDraw);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);

        // The window may be open: give it back every row and take the boxes off it.
        var open = (AtkUnitBase*)Svc.GameGui.GetAddonByName(AddonName).Address;
        var list = open != null ? TreeListOf(open) : null;
        if (list != null) ShowHiddenRows(list);
        _hiddenByUs.Clear();
        DetachBoxes();

        RemoveMarks();
        _recipesByClass.Clear();
        _recipesByItem.Clear();
        _craftTypes.Clear();
        _crafted.Clear();

        _settingsAddon?.Dispose();
        _settingsAddon = null;
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

    protected override IReadOnlyList<string> CommandNames => ["/grm", "/gmarks"];

    protected override void OnCommand() => ToggleHelpWindow();

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(Loc.Get("Window.Settings"), TMain("Settings.Help"), ToggleSettings),
    ];

    private void ToggleSettings()
    {
        if (_settingsAddon is not { } window) return;
        if (window.IsOpen) window.Close(); else window.Open();
    }

    private SimpleSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkRecipeMarksSettings",
        Title           = $"{T("Name")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size            = new System.Numerics.Vector2(520f, 400f),
        RespectCloseAll = false,
        Tweak           = this,
        Heading         = T("Settings.Marks"),
        Toggles         = () =>
        [
            new(T("MarkCrafted"), T("MarkCrafted.Help"), () => Settings.MarkCrafted, on =>
            {
                Settings.MarkCrafted = on;
                SaveSettings();
            }),
            new(T("MarkMissing"), T("MarkMissing.Help"), () => Settings.MarkMissing, on =>
            {
                Settings.MarkMissing = on;
                SaveSettings();
            }),
            new(T("AnyClass"), T("AnyClass.Help"), () => Settings.AnyClass, on =>
            {
                Settings.AnyClass = on;
                SaveSettings();
            }),
            new(T("HideCrafted"), T("HideCrafted.Help"), () => Settings.HideCrafted, on =>
            {
                Settings.HideCrafted = on;
                SaveSettings();
            }),
            new(T("HideNotInLog"), T("HideNotInLog.Help"), () => Settings.HideNotInLog, on =>
            {
                Settings.HideNotInLog = on;
                SaveSettings();
            }),
        ],
    };

    protected override void OnLanguageChanged()
    {
        if (State == TweakState.Enabled) Rebuild(ref _settingsAddon, NewSettingsAddon);
    }

    protected override void OnSettingsChanged() => _crafted.Clear();

    private void BuildIndex()
    {
        _recipesByClass.Clear();
        _recipesByItem.Clear();
        _craftTypes.Clear();

        foreach (var craftType in Svc.Data.GetExcelSheet<CraftTypeRow>())
        {
            var name = craftType.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                _craftTypes.TryAdd(name, craftType.RowId);
        }

        var byClass = new Dictionary<(uint, string), List<uint>>();
        var byItem = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipe in Svc.Data.GetExcelSheet<RecipeRow>())
        {
            if (recipe.RowId == 0 || recipe.ItemResult.RowId == 0 || !recipe.ItemResult.IsValid)
                continue;

            var item = recipe.ItemResult.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(item))
                continue;

            var key = (recipe.CraftType.RowId, item);
            if (!byClass.TryGetValue(key, out var classList))
                byClass[key] = classList = [];
            classList.Add(recipe.RowId);

            if (!byItem.TryGetValue(item, out var itemList))
                byItem[item] = itemList = [];
            itemList.Add(recipe.RowId);
        }

        foreach (var (key, ids) in byClass)
            _recipesByClass[key] = [.. ids];

        foreach (var (item, ids) in byItem)
            _recipesByItem[item] = [.. ids];
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        try
        {
            DetachBoxes();
            _hiddenByUs.Clear();
            _refreshStreak = 0;
            _hidingGaveUp  = false;
            RemoveMarks();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            UpdateMarks((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>The window's list of recipes; null (with one warning in the log) when its layout is not the expected one.</summary>
    private AtkComponentTreeList* TreeListOf(AtkUnitBase* addon)
    {
        // Component nodes have type ids >= 1000; anything else means the window layout changed.
        var listNode = (AtkComponentNode*)addon->GetNodeById(TreeListNodeId);
        var treeList = listNode != null && (ushort)listNode->AtkResNode.Type >= 1000
            ? (AtkComponentTreeList*)listNode->Component
            : null;
        if (treeList == null && !_layoutWarningShown)
        {
            Svc.Log.Warning("{addon} layout changed: node {id} is not a list. Marks disabled until the next update.", AddonName, TreeListNodeId);
            _layoutWarningShown = true;
        }
        return treeList;
    }

    private void UpdateMarks(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsVisible)
            return;

        var treeList = TreeListOf(addon);
        if (treeList == null)
            return;

        AttachBoxes(addon);
        SyncBoxes();

        if (DateTime.UtcNow >= _craftedUntil)
        {
            _crafted.Clear();
            _craftedUntil = DateTime.UtcNow + CraftedCacheLife;
        }

        HideRows(treeList);

        // Rows are reused while scrolling: hide every mark, then show the ones that apply now.
        foreach (var mark in _marks.Values)
            mark.SetVisible(false);

        if (!Settings.MarkCrafted && !Settings.MarkMissing)
            return;

        uint sectionCraftType = 0;

        foreach (var entry in treeList->Items.AsSpan())
        {
            var item = entry.Value;
            if (item == null)
                continue;

            // Section headers ("Smithing", "Cooking"...) set the class of the rows below them. Any other
            // header (a level range...) leaves the class as it is.
            if (IsHeader(item))
            {
                if (_craftTypes.TryGetValue(ReadText(item), out var craftType))
                    sectionCraftType = craftType;
                continue;
            }

            // Only the rows on screen: their text is read every frame.
            if (item->Renderer == null || item->IsHidden)
                continue;

            var crafted = WasCrafted(sectionCraftType, ReadText(item));
            if (crafted == true && Settings.MarkCrafted)
                GetOrCreateMark(item->Renderer, CheckNodeId, CheckMark, CheckX, CheckY)?.SetVisible(true);
            else if (crafted == false && Settings.MarkMissing)
                GetOrCreateMark(item->Renderer, MissingNodeId, MissingMark, MissingX, MissingY)?.SetVisible(true);
        }
    }

    private static bool IsHeader(AtkComponentTreeListItem* item)
        => (item->Type & (TreeListItemType.Group | TreeListItemType.SectionHeader)) != 0;

    // ── Hiding rows ───────────────────────────────────────────────────────────

    // Rows this tweak hid (the game hides rows of folded groups too: those are left to it).
    private readonly HashSet<nint> _hiddenByUs = [];

    // Frames in a row in which the list had to be laid out again. If the game keeps showing the rows we hide, hiding
    // stops for this window instead of laying the list out every frame.
    private const int MaxRefreshStreak = 30;
    private int  _refreshStreak;
    private bool _hidingGaveUp;

    /// <summary>
    /// Hides, through the list's own "hidden" flag (the one folded groups use), the rows the options leave out, and
    /// the class headers left with no row. Asks the list to lay itself out again only when something changed.
    /// </summary>
    private void HideRows(AtkComponentTreeList* treeList)
    {
        var hiding = (Settings.HideCrafted || Settings.HideNotInLog) && !_hidingGaveUp;
        if (!hiding)
        {
            if (_hiddenByUs.Count > 0) ShowHiddenRows(treeList);
            return;
        }

        // What each row should be: rows under their header, the header hidden when every row under it is.
        var items = treeList->Items.AsSpan();
        var hide = new bool[items.Length];
        uint sectionCraftType = 0;
        var header = -1;
        var headerRows = 0;
        var headerHidden = 0;

        void CloseHeader()
        {
            if (header >= 0 && headerRows > 0 && headerHidden == headerRows) hide[header] = true;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i].Value;
            if (item == null) continue;

            if (IsHeader(item))
            {
                CloseHeader();
                header = i;
                headerRows = headerHidden = 0;
                if (_craftTypes.TryGetValue(ReadText(item), out var craftType))
                    sectionCraftType = craftType;
                continue;
            }

            headerRows++;
            if (ShouldHide(sectionCraftType, ReadText(item)))
            {
                hide[i] = true;
                headerHidden++;
            }
        }
        CloseHeader();

        var changed = false;
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i].Value;
            if (item == null) continue;

            if (hide[i] && !item->IsHidden)
            {
                item->IsHidden = true;
                _hiddenByUs.Add((nint)item);
                changed = true;
            }
            else if (!hide[i] && _hiddenByUs.Remove((nint)item))
            {
                item->IsHidden = false;
                changed = true;
            }
        }

        if (!changed)
        {
            _refreshStreak = 0;
            return;
        }

        treeList->LayoutRefreshPending = true;
        if (++_refreshStreak > MaxRefreshStreak)
        {
            Svc.Log.Warning("{addon}: the list keeps showing the rows hidden by {tweak}; hiding stops until the window opens again.", AddonName, Name);
            _hidingGaveUp = true;
            ShowHiddenRows(treeList);
        }
    }

    /// <summary>Gives back the rows this tweak hid (only rows still in the list: the pointers are never followed).</summary>
    private void ShowHiddenRows(AtkComponentTreeList* treeList)
    {
        var changed = false;
        foreach (var entry in treeList->Items.AsSpan())
        {
            var item = entry.Value;
            if (item == null || !_hiddenByUs.Contains((nint)item)) continue;
            item->IsHidden = false;
            changed = true;
        }

        _hiddenByUs.Clear();
        if (changed) treeList->LayoutRefreshPending = true;
    }

    /// <summary>Whether the options hide a row: crafted, or not counting for the crafting log. Unknown rows stay.</summary>
    private bool ShouldHide(uint craftType, string itemName)
    {
        if (itemName.Length == 0) return false;
        if (Settings.HideCrafted && WasCrafted(craftType, itemName) == true) return true;
        if (Settings.HideNotInLog && RecipesOf(craftType, itemName) is { } recipes && !recipes.Any(CraftingLogRecipes.Ids.Contains))
            return true;
        return false;
    }

    /// <summary>The recipes of a row: those of its class when the class is known, else every recipe of the item.</summary>
    private uint[]? RecipesOf(uint craftType, string itemName)
    {
        if (craftType != 0 && _recipesByClass.TryGetValue((craftType, itemName), out var classRecipes)) return classRecipes;
        return _recipesByItem.TryGetValue(itemName, out var recipes) ? recipes : null;
    }

    // ── Boxes on the window ───────────────────────────────────────────────────

    // Where the boxes go: right of the class buttons under the item, right-aligned (unscaled window coordinates).
    private const float BoxesY      = 98f;
    private const float BoxesRight  = 16f;
    private const float BoxesHeight = 20f;
    private const float BoxesGap    = 12f;

    private CheckboxNode? _hideCraftedBox;
    private CheckboxNode? _hideNotInLogBox;
    private nint   _boxWindow;
    private ushort _boxWindowId;
    private bool   _syncingBoxes;

    /// <summary>Puts the two boxes on the window, once per window.</summary>
    private void AttachBoxes(AtkUnitBase* addon)
    {
        if (_boxWindow == (nint)addon && _hideCraftedBox is not null) return;
        DetachBoxes();

        _hideNotInLogBox = NewBox(T("Box.HideNotInLog"), T("HideNotInLog.Help"), Settings.HideNotInLog, on => Settings.HideNotInLog = on);
        _hideCraftedBox  = NewBox(T("Box.HideCrafted"), T("HideCrafted.Help"), Settings.HideCrafted, on => Settings.HideCrafted = on);

        // Right-aligned, the "not in the log" box last; each is as wide as its text.
        var width = addon->GetScaledWidth(true) / Math.Max(addon->Scale, 0.01f);
        var right = width - BoxesRight;
        _hideNotInLogBox.Position = new System.Numerics.Vector2(right - _hideNotInLogBox.Width, BoxesY);
        right -= _hideNotInLogBox.Width + BoxesGap;
        _hideCraftedBox.Position = new System.Numerics.Vector2(right - _hideCraftedBox.Width, BoxesY);

        _hideCraftedBox.AttachNode(addon);
        _hideNotInLogBox.AttachNode(addon);
        _boxWindow   = (nint)addon;
        _boxWindowId = addon->Id;
    }

    private CheckboxNode NewBox(string label, string tooltip, bool isChecked, Action<bool> set)
    {
        var box = new CheckboxNode
        {
            String      = label,
            Height      = BoxesHeight,
            TextTooltip = tooltip,
        };
        box.String = label; // the width follows the text and the height
        _syncingBoxes = true;
        box.IsChecked = isChecked;
        _syncingBoxes = false;
        box.OnClick = on =>
        {
            if (_syncingBoxes) return;
            set(on);
            SaveSettings();
        };
        return box;
    }

    /// <summary>Keeps the boxes in line with the options when they change in the settings window.</summary>
    private void SyncBoxes()
    {
        _syncingBoxes = true;
        try
        {
            if (_hideCraftedBox is { } crafted && crafted.IsChecked != Settings.HideCrafted) crafted.IsChecked = Settings.HideCrafted;
            if (_hideNotInLogBox is { } log && log.IsChecked != Settings.HideNotInLog) log.IsChecked = Settings.HideNotInLog;
        }
        finally
        {
            _syncingBoxes = false;
        }
    }

    private void DetachBoxes()
    {
        foreach (var box in new[] { _hideCraftedBox, _hideNotInLogBox })
        {
            if (box is null) continue;
            NativeFocus.ReleaseNode(_boxWindow, _boxWindowId, box);
            box.Dispose();
        }

        _hideCraftedBox = _hideNotInLogBox = null;
        _boxWindow = 0;
    }

    private static string ReadText(AtkComponentTreeListItem* item)
    {
        var strings = item->StringValues.AsSpan();
        if (strings.Length == 0 || !strings[0].HasValue)
            return string.Empty;

        return SeString.Parse(strings[0].AsSpan()).TextValue.Trim();
    }

    /// <summary>
    /// Whether the recipe of a row was crafted, from the same data the Crafting Log uses for its own check mark;
    /// null when the row is not a recipe this tweak knows. Kept for a moment, since it is asked every frame.
    /// </summary>
    private bool? WasCrafted(uint craftType, string itemName)
    {
        if (itemName.Length == 0)
            return null;

        var key = (Settings.AnyClass ? 0u : craftType, itemName);
        if (_crafted.TryGetValue(key, out var known))
            return known;

        bool? crafted;
        if (!Settings.AnyClass && craftType != 0 && _recipesByClass.TryGetValue((craftType, itemName), out var classRecipes))
            crafted = classRecipes.Any(QuestManager.IsRecipeComplete);
        else if (_recipesByItem.TryGetValue(itemName, out var recipes))
            crafted = recipes.Any(QuestManager.IsRecipeComplete);
        else
            crafted = null;

        _crafted[key] = crafted;
        return crafted;
    }

    private NativeImageNode? GetOrCreateMark(AtkComponentListItemRenderer* renderer, uint nodeId, TexturePart part, short x, short y)
    {
        if (_marks.TryGetValue(((nint)renderer, nodeId), out var existing))
            return existing;

        var mark = NativeImageNode.Create((AtkComponentBase*)renderer, nodeId, part, x, y);
        if (mark != null)
            _marks[((nint)renderer, nodeId)] = mark;

        return mark;
    }

    private void RemoveMarks()
    {
        foreach (var mark in _marks.Values)
            mark.Dispose();

        _marks.Clear();
    }
}
