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
/// whose recipe was crafted at least once.
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
    }

    private const string AddonName = "RecipeProductList";
    private const uint TreeListNodeId = 21;
    private const uint CheckNodeId = 0x4742_0001;

    // The check mark used by the game's logs (GatheringNote, part #2). "Gethering" is the real file name.
    private static readonly TexturePart CheckMark = new("ui/uld/GetheringNoteBook.tex", 2, 90, 22, 22, 22, 22);
    private const short CheckX = 19; // bottom-right corner of the row icon
    private const short CheckY = 19;

    private readonly Dictionary<(uint CraftType, string Item), uint[]> _recipesByClass = [];
    private readonly Dictionary<string, uint[]> _recipesByItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _craftTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, NativeImageNode> _marks = [];
    private bool _layoutWarningShown;

    protected internal override void Enable()
    {
        BuildIndex();
        _layoutWarningShown = false;

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
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

    protected override IReadOnlyList<string> CommandNames => ["/grm", "/gmarks"];

    protected override void OnCommand() => ToggleHelpWindow();

    public override IReadOnlyList<TweakToggle> Toggles =>
    [
        new(T("AnyClass"), T("AnyClass.Help"), () => Settings.AnyClass, on =>
        {
            Settings.AnyClass = on;
            SaveSettings();
        }),
    ];

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

        uint sectionCraftType = 0;

        foreach (var entry in treeList->Items.AsSpan())
        {
            var item = entry.Value;
            if (item == null)
                continue;

            var text = ReadText(item);

            // Section headers ("Smithing", "Cooking"...) set the class of the rows below them.
            if ((item->Type & (TreeListItemType.Group | TreeListItemType.SectionHeader)) != 0)
            {
                _craftTypes.TryGetValue(text, out sectionCraftType);
                continue;
            }

            if (item->Renderer == null || item->IsHidden || !WasCrafted(sectionCraftType, text))
                continue;

            GetOrCreateMark(item->Renderer)?.SetVisible(true);
        }
    }

    private static string ReadText(AtkComponentTreeListItem* item)
    {
        var strings = item->StringValues.AsSpan();
        if (strings.Length == 0 || !strings[0].HasValue)
            return string.Empty;

        return SeString.Parse(strings[0].AsSpan()).TextValue.Trim();
    }

    // Same data the Crafting Log uses for its own check mark.
    private bool WasCrafted(uint craftType, string itemName)
    {
        if (itemName.Length == 0)
            return false;

        if (!Settings.AnyClass && craftType != 0 && _recipesByClass.TryGetValue((craftType, itemName), out var classRecipes))
            return classRecipes.Any(QuestManager.IsRecipeComplete);

        return _recipesByItem.TryGetValue(itemName, out var recipes) && recipes.Any(QuestManager.IsRecipeComplete);
    }

    private NativeImageNode? GetOrCreateMark(AtkComponentListItemRenderer* renderer)
    {
        if (_marks.TryGetValue((nint)renderer, out var existing))
            return existing;

        var mark = NativeImageNode.Create((AtkComponentBase*)renderer, CheckNodeId, CheckMark, CheckX, CheckY);
        if (mark != null)
            _marks[(nint)renderer] = mark;

        return mark;
    }

    private void RemoveMarks()
    {
        foreach (var mark in _marks.Values)
            mark.Dispose();

        _marks.Clear();
    }
}
