using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using GoblinTweaks.UI;
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

    private void OnPreFinalize(AddonEvent type, AddonArgs args) => RemoveMarks();

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

    private void UpdateMarks(AtkUnitBase* addon)
    {
        if (addon == null || !addon->IsVisible)
            return;

        // Component nodes have type ids >= 1000; anything else means the window layout changed.
        var listNode = (AtkComponentNode*)addon->GetNodeById(TreeListNodeId);
        var treeList = listNode != null && (ushort)listNode->AtkResNode.Type >= 1000
            ? (AtkComponentTreeList*)listNode->Component
            : null;
        if (treeList == null)
        {
            if (!_layoutWarningShown)
            {
                Svc.Log.Warning("{addon} layout changed: node {id} is not a list. Marks disabled until the next update.", AddonName, TreeListNodeId);
                _layoutWarningShown = true;
            }
            return;
        }

        // Rows are reused while scrolling: hide every mark, then show the ones that apply now.
        foreach (var mark in _marks.Values)
            mark.SetVisible(false);

        if (!Settings.MarkCrafted && !Settings.MarkMissing)
            return;

        if (DateTime.UtcNow >= _craftedUntil)
        {
            _crafted.Clear();
            _craftedUntil = DateTime.UtcNow + CraftedCacheLife;
        }

        uint sectionCraftType = 0;

        foreach (var entry in treeList->Items.AsSpan())
        {
            var item = entry.Value;
            if (item == null)
                continue;

            // Section headers ("Smithing", "Cooking"...) set the class of the rows below them. Any other
            // header (a level range...) leaves the class as it is.
            if ((item->Type & (TreeListItemType.Group | TreeListItemType.SectionHeader)) != 0)
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
