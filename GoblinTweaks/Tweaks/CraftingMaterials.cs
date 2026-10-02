using System.Text;
using Dalamud.Game;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
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

    public readonly Dictionary<uint, int>                              Player          = new();
    public readonly Dictionary<uint, int>                              Saddlebag       = new();
    public readonly Dictionary<uint, int>                              Retainer        = new(); // aggregate
    public readonly Dictionary<uint, int>                              FCChest         = new();
    public readonly List<(string Name, Dictionary<uint, int> Items)>  RetainerDetails = [];

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

    /// <summary>Returns human-readable location strings for tooltip display, including retainer names.</summary>
    public IEnumerable<(string Location, int Qty)> Locations(uint itemId)
    {
        var q = Player.GetValueOrDefault(itemId);
        if (q > 0)
            yield return (string.IsNullOrEmpty(PlayerName) ? CraftLoc.Get("loc.yourbags") : CraftLoc.Fmt("loc.bagsof", PlayerName), q);

        q = Saddlebag.GetValueOrDefault(itemId);
        if (q > 0) yield return (CraftLoc.Get("loc.saddlebag"), q);

        if (RetainerDetails.Count > 0)
        {
            foreach (var (name, items) in RetainerDetails)
            {
                q = items.GetValueOrDefault(itemId);
                if (q > 0) yield return (CraftLoc.Fmt("loc.bagsof", name), q);
            }
        }
        else
        {
            q = Retainer.GetValueOrDefault(itemId);
            if (q > 0) yield return (CraftLoc.Get("loc.retainers"), q);
        }

        q = FCChest.GetValueOrDefault(itemId);
        if (q > 0) yield return (CraftLoc.Get("loc.fcchest"), q);
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
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("ja", "日本語"),
    ];

    private CraftingMaterialsAddon? _addon;
    private UniversalisService?     _universalis;
    private bool                    _showSettings;

    public UniversalisService Universalis => _universalis!;
    public uint WorldId => Svc.PlayerState.IsLoaded ? Svc.PlayerState.HomeWorld.RowId : 0u;

    /// <summary>Language code for the window's own UI strings ("" = follow GoblinTweaks).</summary>
    public string WindowUiLang => string.IsNullOrEmpty(Settings.DataLanguage) ? Loc.CurrentLanguage : Settings.DataLanguage;

    /// <summary>Game-data language used for item/recipe names in the window.</summary>
    public ClientLanguage DataLanguage => WindowUiLang switch
    {
        "de" => ClientLanguage.German,
        "fr" => ClientLanguage.French,
        "ja" => ClientLanguage.Japanese,
        _    => ClientLanguage.English, // "en", "es" and anything without game data
    };

    protected internal override void Enable()
    {
        _universalis = new UniversalisService();
        CraftLoc.Lang = WindowUiLang;
        _addon = new CraftingMaterialsAddon
        {
            InternalName = "GtkCraftingMaterials",
            Title        = CraftLoc.Get("title"),
            Size         = new System.Numerics.Vector2(1180f, 740f),
            Tweak        = this,
        };
    }

    protected internal override void Disable()
    {
        _addon?.Close();
        _addon = null;
        _universalis?.Dispose();
        _universalis = null;
    }

    public override bool HasSettings => true;

    // Any settings change (language or inventory sources) re-scans the open window.
    protected override void OnSettingsChanged() => _addon?.RequestRefresh();

    public override void DrawSettings()
    {
        // Open the native window (icon hints that a separate window opens).
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ExternalLinkAlt, T("Open")))
            _addon?.Open();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("Open.Help"));

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Cog, T("Settings")))
            _showSettings = !_showSettings;

        if (!_showSettings)
            return;

        ImGui.Spacing();
        DrawLanguagePicker();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(Palette.Accent, T("Sources"));
        ImGui.Spacing();

        var saddlebag = Settings.IncludeSaddlebag;
        if (Widgets.SettingToggle(T("IncludeSaddlebag"), null, ref saddlebag))
        { Settings.IncludeSaddlebag = saddlebag; SaveSettings(); }

        var retainers = Settings.IncludeRetainers;
        if (Widgets.SettingToggle(T("IncludeRetainers"), T("IncludeRetainers.Help"), ref retainers))
        { Settings.IncludeRetainers = retainers; SaveSettings(); }

        var fc = Settings.IncludeFCChest;
        if (Widgets.SettingToggle(T("IncludeFCChest"), T("IncludeFCChest.Help"), ref fc))
        { Settings.IncludeFCChest = fc; SaveSettings(); }
    }

    private void DrawLanguagePicker()
    {
        ImGui.TextColored(Palette.Accent, T("Language"));
        ImGui.Spacing();

        string Label(string code) => code.Length == 0
            ? T("Language.Same")
            : DataLanguages.First(l => l.Code == code).Label;

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##cmlang", Label(Settings.DataLanguage)))
        {
            if (combo)
            {
                foreach (var (code, _) in DataLanguages)
                {
                    if (ImGui.Selectable(Label(code), code == Settings.DataLanguage) && code != Settings.DataLanguage)
                    {
                        Settings.DataLanguage = code;
                        SaveSettings();
                    }
                }
            }
        }

        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Palette.Muted, T("Language.Help"));
        ImGui.PopTextWrapPos();
    }

    // ── Inventory scanning ────────────────────────────────────────────────────

    public unsafe InventorySnapshot ScanInventories()
    {
        var snap = new InventorySnapshot();
        var mgr  = InventoryManager.Instance();
        if (mgr == null) return snap;

        snap.PlayerName = Svc.PlayerState.IsLoaded ? Svc.PlayerState.CharacterName : string.Empty;

        ScanContainers(snap.Player, mgr,
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
            InventoryType.Crystals);

        if (Settings.IncludeSaddlebag)
            ScanContainers(snap.Saddlebag, mgr,
                InventoryType.SaddleBag1, InventoryType.SaddleBag2,
                InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2);

        if (Settings.IncludeRetainers)
            ScanRetainers(snap.Retainer, snap.RetainerDetails, mgr);

        if (Settings.IncludeFCChest)
            ScanContainers(snap.FCChest, mgr,
                InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2,
                InventoryType.FreeCompanyPage3, InventoryType.FreeCompanyPage4,
                InventoryType.FreeCompanyPage5);

        return snap;
    }

    private static unsafe void ScanContainers(
        Dictionary<uint, int> target, InventoryManager* mgr, params InventoryType[] types)
    {
        foreach (var type in types)
        {
            var container = mgr->GetInventoryContainer(type);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;

                var id = slot->ItemId;
                target[id] = target.GetValueOrDefault(id) + (int)slot->Quantity;
            }
        }
    }

    // Retainer inventories are stored client-side after visiting the retainer bell.
    // Only the currently active retainer window exposes real-time data; cached data
    // from previous visits is available in RetainerPage1-7 of each retainer's slot.
    private static unsafe void ScanRetainers(
        Dictionary<uint, int> aggregate,
        List<(string Name, Dictionary<uint, int> Items)> details,
        InventoryManager* mgr)
    {
        var retMgr = RetainerManager.Instance();
        if (retMgr == null) return;

        var count = retMgr->GetRetainerCount();
        for (uint ri = 0; ri < count && ri < 10; ri++)
        {
            var retainer = retMgr->GetRetainerBySortedIndex(ri);
            if (retainer == null || !retainer->Available) continue;

            // Name is a Span<byte> with a null terminator — decode as UTF-8.
            var nameSpan    = retainer->Name;
            var nullIdx     = nameSpan.IndexOf((byte)0);
            var retainerName = Encoding.UTF8.GetString(nullIdx >= 0 ? nameSpan[..nullIdx] : nameSpan);
            if (string.IsNullOrWhiteSpace(retainerName))
                retainerName = $"Retainer {ri + 1}";

            var retainerItems = new Dictionary<uint, int>();

            for (var page = 0; page < 7; page++)
            {
                // Each retainer occupies 7 consecutive InventoryType slots starting at RetainerPage1
                var type = (InventoryType)((uint)InventoryType.RetainerPage1 + ri * 7 + (uint)page);
                var container = mgr->GetInventoryContainer(type);
                if (container == null) continue;

                for (var s = 0; s < container->Size; s++)
                {
                    var slot = container->GetInventorySlot(s);
                    if (slot == null || slot->ItemId == 0) continue;

                    var id = slot->ItemId;
                    retainerItems[id] = retainerItems.GetValueOrDefault(id) + (int)slot->Quantity;
                    aggregate[id]     = aggregate    .GetValueOrDefault(id) + (int)slot->Quantity;
                }
            }

            if (retainerItems.Count > 0)
                details.Add((retainerName, retainerItems));
        }
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
                ItemLevel    = (int)resultItem.LevelItem.RowId,
                CraftLevel   = recipe.RecipeLevelTable.IsValid ? recipe.RecipeLevelTable.Value.ClassJobLevel : 0,
                Ingredients  = checks,
            });
        }

        return results;
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
