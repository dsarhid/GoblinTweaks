# Crafting Materials "Por origen" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Tree / By source selector to the My List "Total materials" panel; "By source" lists every base material once, grouped by gathering zone, then vendor, other, crafts first, crystals and already-owned.

**Architecture:** Pure logic in `GoblinTweaks/Crafting/` (tested by `GoblinTweaks.Tests`): `CraftMath.Breakdown` (base materials + intermediate crafts) and `SourcePlan.Build` (grouping, zone cover, order). A Dalamud-side index `Core/GatheringSources` reads gathering/fishing/vendor data from Lumina. `UI/CraftingMaterialsAddon` turns the plan into rows of the existing `ListNode<IngredientRow, IngredientRowNode>`, with header rows that fold.

**Tech Stack:** C# / .NET 10, Dalamud.NET.Sdk 15, KamiToolKit native nodes, Lumina sheets, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-15-gcm-totals-by-source-design.md`

## Global Constraints

- UI is native KamiToolKit only, never ImGui.
- KTK dropdown: assign `Options` only after `AttachNode` (closing the window crashes otherwise).
- Every new text goes in `UI/CraftLoc.cs`, English and Spanish (voseo).
- Files in `GoblinTweaks/Crafting/` use no Dalamud types (`GoblinTweaks.Tests` links them).
- Data only from game sheets: no network, no external services.
- Default view: Tree (`"tree"`). Collapsed groups live only while the window is open.
- Group order: zones → vendor → other → crafts first → crystals → already have everything.
- Group priority per item: crystal (`ItemUICategory` 59) → owned ≥ needed → gatherable zone → `GilShopItem` vendor → other.
- Commit steps run only if the user has asked for commits in this session; otherwise skip them.

## Review Focus

- A gathering point with no zone or an empty zone name (Diadem rows, removed content): the item must fall to Other, never crash. → Task 3 skips such spots; Task 2 test `NoSpotsGoesToOther`.
- Every material already owned, or an empty list: no zone groups, only Done/Crystals, no empty headers. → Task 2 test `EverythingOwnedHasOnlyDoneAndCrystals`, `EmptyInputHasNoGroups`.
- "Deduct what I own" off: amounts are totals but Done still compares against what is owned; the By source quantities must equal the ones the gil total uses. → Task 1 test `BreakdownBaseEqualsBaseMaterials` (both deduct values).
- Changing the plugin language while By source is open: the dropdown labels and zone/place names follow the new language. → Task 3 caches per language; Task 4 step re-sets dropdown options with the sort dropdown.
- Folding a zone, then changing an amount (recount): the zone stays folded. → Task 4 keys folded groups by `SourceGroup.Key` (zone id based), verified in-game step.

---

### Task 1: Intermediate crafts in the breakdown

**Files:**
- Modify: `GoblinTweaks/Crafting/CraftMath.cs`
- Test: `GoblinTweaks.Tests/CraftMathTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct Craft(uint ItemId, string Name, uint IconId, int Crafts, int Depth);` (in `CraftMath.cs`)
  - `public static (List<Need> Base, List<Craft> Crafts) CraftMath.Breakdown(IEnumerable<CartItem> items, ICraftData data, bool deductOwned)`
  - `BaseMaterials(...)` keeps its signature and becomes `=> Breakdown(items, data, deductOwned).Base`.

- [ ] **Step 1: Write the failing tests** in `CraftMathTests.cs`, reusing the file's existing fake `ICraftData` helper:
  - `BreakdownBaseEqualsBaseMaterials`: for one cart with a two-level recipe, `Breakdown(...).Base` equals `BaseMaterials(...)` for `deductOwned` true and false.
  - `BreakdownCountsCraftsOfIntermediates`: recipe X needs 2× Whetstone (makes 1, needs 1 Annite + 1 Wind Crystal); `Crafts` = `[Craft(Whetstone, …, Crafts: 2, Depth: 1)]`.
  - `BreakdownSumsCraftsAcrossRecipes`: two listed recipes each needing 1 Whetstone → one entry with `Crafts == 2`.
  - `BreakdownUsesLeftoversOfACraft`: intermediate makes 3 per craft, two recipes need 2 each → `Crafts == 2` (4 needed, 6 made).
  - `BreakdownDeductsOwnedIntermediates`: owning 2 Whetstones with deduct on → no Whetstone in `Crafts`; deduct off → `Crafts == 2`.
  - `BreakdownOrdersDeepestFirst`: A needs B, B needs C (all craftable) → order `[C, B]` (Depth 2 before Depth 1).

- [ ] **Step 2: Run** `dotnet test GoblinTweaks.Tests --filter CraftMathTests` — Expected: compile FAIL (`Breakdown` not defined).

- [ ] **Step 3: Implement `Breakdown`** by moving the body of `BaseMaterials` into it and, where a sub-recipe is followed (`crafts` computed), adding `crafts` to a per-item sum that keeps the max depth seen. Return crafts ordered by `Depth` descending, then first appearance.

- [ ] **Step 4: Run** `dotnet test GoblinTweaks.Tests` — Expected: all PASS (existing `BaseMaterials` tests included).

- [ ] **Step 5: Commit** `git add GoblinTweaks/Crafting/CraftMath.cs GoblinTweaks.Tests/CraftMathTests.cs && git commit -m "Crafting: Breakdown returns the crafts of intermediates"`

---

### Task 2: Grouping by source

**Files:**
- Create: `GoblinTweaks/Crafting/SourcePlan.cs`
- Test: `GoblinTweaks.Tests/SourcePlanTests.cs`

**Interfaces:**
- Consumes: `Need` (CraftMath.cs).
- Produces (all in namespace `GoblinTweaks.Crafting`):
  ```csharp
  public enum GatherClass { Miner, Botanist, Fisher }
  public enum SourceKind { Zone, Vendor, Other, Crystals, Done }
  public sealed record GatherSpot(uint ZoneId, string ZoneName, string PlaceName, GatherClass Class, int Level, bool Timed);
  public sealed record ItemSources(IReadOnlyList<GatherSpot> Spots, bool Vendor, bool Crystal)
  { public static readonly ItemSources None = new([], false, false); }
  public sealed record SourceLine(Need Need, int Owned, GatherSpot? Spot, IReadOnlyList<string> AlsoIn, bool AlsoVendor)
  { public bool Missing => Owned < Need.Amount; }
  public sealed record SourceGroup(SourceKind Kind, string Key, uint ZoneId, string ZoneName,
      IReadOnlyList<GatherClass> Classes, IReadOnlyList<SourceLine> Lines)
  { public int Missing => Lines.Count(l => l.Missing); }
  public static class SourcePlan
  { public static List<SourceGroup> Build(IReadOnlyList<Need> bases, Func<uint, int> owned, Func<uint, ItemSources> sources); }
  ```
  `Key`: `"zone:<ZoneId>"`, `"vendor"`, `"other"`, `"crystals"`, `"done"`. Empty groups are not returned.

- [ ] **Step 1: Write the failing tests** (`SourcePlanTests.cs`; helper `Spot(zone, name, place, cls = Miner, level = 90, timed = false)`; needs built with `new Need(id, name, 0, amount)`):
  - `CrystalsGoToCrystalsEvenWhenMissing`: crystal with owned 0 → only group `Crystals`.
  - `OwnedItemsGoToDone`: gatherable item with owned ≥ amount → group `Done`, no zone group.
  - `GatherableGoesToItsZone`: one spot in zone 10 "Yak T'el" → group `Zone`, `Key == "zone:10"`, line `Spot.PlaceName == "Xmun Hojaw"`.
  - `GatherBeatsVendor`: gatherable and vendor → zone group, `AlsoVendor == true`.
  - `VendorOnlyGoesToVendor`, `NoSpotsGoesToOther` (`ItemSources.None` → `Other`).
  - `MultiZoneItemJoinsTheZoneWithMostItems`: A only in zone 1, B only in zone 1, C in zones 1 and 2 → all three in zone 1; C's `AlsoIn == ["Zone 2"]`.
  - `ThreeZoneItemPicksTheCoveringZone`: D in zones 1, 2, 3; E only in 3 → D and E in zone 3; D's `AlsoIn` = zones 1 and 2 by name.
  - `ZoneTieBreaksBySingleZoneItemsThenName`: F in zones "B" and "A", nothing else → zone "A" (name); G in zones X, Y plus H only in Y → G joins Y.
  - `SpotPrefersNonTimedThenLowestLevel` within the chosen zone.
  - `GroupsAreOrdered`: zones by `Missing` desc then name, then Vendor, Other, Crystals, Done.
  - `LinesMissingFirstThenName` inside a group.
  - `ZoneClassesAreDistinctInEnumOrder`: Botanist + Miner lines → `Classes == [Miner, Botanist]`.
  - `EverythingOwnedHasOnlyDoneAndCrystals`, `EmptyInputHasNoGroups`.

- [ ] **Step 2: Run** `dotnet test GoblinTweaks.Tests --filter SourcePlanTests` — Expected: compile FAIL.

- [ ] **Step 3: Implement `SourcePlan.Build`.** Assign by priority (crystal → done → zone → vendor → other). Zone cover, over the missing gatherable non-crystal items:
  ```text
  unassigned = all such items
  while unassigned not empty:
      for each zone z: cover(z) = |{i in unassigned : i has a spot in z}|
      pick max cover; tie → max |{i in all such items : i's only zone is z}|; tie → ZoneName (ordinal)
      assign every unassigned item with a spot in z to z
  ```
  Line spot = that item's spots in the zone, ordered by `Timed` (false first) then `Level`. `AlsoIn` = the item's other zone names, distinct, ordinal-sorted.

- [ ] **Step 4: Run** `dotnet test GoblinTweaks.Tests` — Expected: all PASS.

- [ ] **Step 5: Commit** `git add GoblinTweaks/Crafting/SourcePlan.cs GoblinTweaks.Tests/SourcePlanTests.cs && git commit -m "Crafting: group materials by where they come from"`

---

### Task 3: Gathering sources from the game data

**Files:**
- Create: `GoblinTweaks/Core/GatheringSources.cs`

**Interfaces:**
- Consumes: `GatherSpot`, `GatherClass`, `ItemSources` (Task 2).
- Produces:
  ```csharp
  internal sealed class GatheringSources
  {
      /// Built once per language, lazily, on first use; cached in a static keyed by language.
      public static GatheringSources For(Dalamud.Game.ClientLanguage lang);
      public ItemSources Get(uint itemId); // ItemSources.None when nothing is known
  }
  ```

- [ ] **Step 1: Implement `For`/`Get`**, reading sheets with `Svc.Data.GetExcelSheet<T>(lang)`:
  - Gathering: `GatheringItem` (`Item`, `GatheringItemLevel` → `GatheringItemLevelConvertTable.GatheringItemLevel`) → `GetSubrowExcelSheet<GatheringItemPoint>` (row id = GatheringItem id; subrow `GatheringPoint`) → `GatheringPoint` (`TerritoryType`, `PlaceName`, `GatheringPointBase.Value.GatheringType`). Class: GatheringType 0–1 Miner, 2–3 Botanist, 4–5 Fisher.
  - Timed: `GatheringPointTransient` row with the GatheringPoint id: `GatheringRarePopTimeTable.RowId != 0` or (`EphemeralStartTime != 65535` and `EphemeralStartTime != EphemeralEndTime`). Check one known timed and one normal point in game data while implementing and adjust the ephemeral test if 65535 isn't the "none" value.
  - Fishing: `FishingSpot` (`Item[]`, `TerritoryType`, `PlaceName`, `GatheringLevel`) → Fisher, not timed. Spearfishing: `SpearfishingItem` (`Item`, `TerritoryType`, `GatheringItemLevel` → convert table), place name = zone name.
  - Zone name = `TerritoryType.PlaceName.Name`. Skip spots with territory 0, invalid refs or empty zone name. One spot per (item, zone, place, class).
  - Vendor: set of `GilShopItem` (subrow sheet) `Item.RowId`. Crystal: `Item.ItemUICategory.RowId == 59`.
  - Log once: `Svc.Log.Info($"GatheringSources: {n} gatherable items, {v} vendor items ({lang})")`.

- [ ] **Step 2: Run** `dotnet build GoblinTweaks/GoblinTweaks.csproj -c Release` — Expected: `0 Advertencia(s)` / `0 Warning(s)`, build succeeds.

- [ ] **Step 3: Commit** `git add GoblinTweaks/Core/GatheringSources.cs && git commit -m "Core: index of where items are gathered or sold"`

---

### Task 4: The By source view in the window

**Files:**
- Modify: `GoblinTweaks/Tweaks/CraftingMaterials.cs:300-303` (filter settings)
- Modify: `GoblinTweaks/UI/Nodes/IngredientRowNode.cs`
- Modify: `GoblinTweaks/UI/CraftingMaterialsAddon.cs` (`BuildCartPanel` ~1725, `ShowCartPanel` ~671, `RebuildCart` ~700, `ToggleCartIngredient` ~742, `RebuildCartList` ~752, where `_sortDropdown.Options` is re-set on language change)
- Modify: `GoblinTweaks/UI/CraftLoc.cs`, `GoblinTweaks/UI/CraftingHelpAddon.cs`

**Interfaces:**
- Consumes: `CraftMath.Breakdown`, `Craft` (Task 1); `SourcePlan.Build`, `SourceGroup`, `SourceLine`, `SourceKind`, `GatherClass` (Task 2); `GatheringSources.For(CraftingMaterials.DataLanguage).Get` (Task 3).
- Produces:
  - Setting `public string CartMode { get; set; } = "tree";` (`"tree"` | `"source"`) next to `CartDeductOwned`.
  - `IngredientRow` gains optional fields: `string? Header = null, string? HeaderRight = null, string? QtyText = null, string? Place = null, string? ExtraTip = null`.

- [ ] **Step 1: Row node.** In `IngredientRowNode`:
  - `Header != null`: header row — icon, qty, location and price hidden; name shows `Header` in gold (`ColorCaret`), `HeaderRight` right-aligned in the qty+location area; caret visible, `"-"` when `Expanded` else `"+"`; no tooltips; right-click does nothing (guard `Check.ItemId != 0` for every row).
  - `QtyText` replaces `"{Available}/{Required}"` (crafts rows).
  - `Place`: when the item is missing and `PrimaryLocation` is empty, the location column shows `Place` instead of `loc.notfound`.
  - `ExtraTip`: like `PriceTip` — `TextTooltip` = location lines (if any) + `ExtraTip` (+ `PriceTip`), `ItemTooltip = 0`.

- [ ] **Step 2: Texts** in `CraftLoc` (en / es):
  - `cart.view.tree` "Tree" / "Árbol"; `cart.view.source` "By source" / "Por origen"
  - `src.zone` "{0} · {1}" (zone, classes); `src.vendor` "Buy from a vendor" / "Comprar a vendedor"; `src.other` "Other (drops, exchanges, Market Board)" / "Otros (drops, canjes, Market Board)"; `src.crafts` "Craft first" / "Craftear antes"; `src.crystals` "Crystals" / "Cristales"; `src.done` "You have everything" / "Ya tenés todo"
  - `src.missing` "{0} to get" / "{0} por conseguir"; `src.recipes` "{0} recipes" / "{0} recetas"; `src.crafts.qty` "×{0} crafts" / "×{0} crafts"
  - `class.miner` "Miner", `class.botanist` "Botanist", `class.fisher` "Fisher" (same in es)
  - `src.tip.gather` "Gather: {0} ({1}, Lv {2})" / "Recolectar: {0} ({1}, nivel {2})"; `src.tip.timed` "Timed node" / "Punto con horario"; `src.tip.also` "Also in: {0}" / "También en: {0}"; `src.tip.vendor` "Also sold by a vendor" / "También se compra a vendedor"
  - `help.tips.t10` (en): "My List › Total materials: Tree shows each material inside what it is made for; By source lists every material once, grouped by the zone where you gather it, then vendor, other, crafts to do first, crystals and what you already have. Click a group title to fold it." — es in voseo with the same content. Add `new Line("", White, "help.tips.t10", 72f)` to the Tips page.

- [ ] **Step 3: Selector.** In `BuildCartPanel`, a `StringDropDownNode` `_cartModeDropdown` at `(pos.X + 236f, y - 2f)`, size `(130f, 24f)`, `MaxListOptions = 2`, `GetLabelFunction = k => CraftLoc.Get("cart.view." + k)`, hidden like the other cart nodes and listed in `ShowCartPanel`'s node array. After `AttachNode`: `Options = ["tree", "source"]`, `SelectedOption = Tweak.Current.CartMode`. `OnOptionSelected`: save to `CartMode`, `Tweak.SaveFilters()`, `RebuildCart(keepScroll: false)`. Re-set its `Options`/`SelectedOption` wherever `_sortDropdown.Options` is re-set; null it where `_sortDropdown` is nulled.

- [ ] **Step 4: Rows.** In `RebuildCart`, when `CartMode == "source"`: `var (bases, crafts) = CraftMath.Breakdown(listed, data, deduct)`; `_cartBase = bases`; `groups = SourcePlan.Build(bases, _snapshot.GetTotal, GatheringSources.For(CraftingMaterials.DataLanguage).Get)`; make one `IngTreeNode` per line with `MakeIngNode(CraftingMaterials.Check(line.Need, _snapshot), 0, _cartById)` and `Craftable = false` (so price polling and highlights keep reading `_cartById`). Keep the groups and crafts in fields for `RebuildCartList`. Tree mode stays exactly as today.
  In `RebuildCartList` (source mode), for each group in order — inserting the crafts group (`Key "crafts"`, `HeaderRight = src.recipes`) before Crystals when `crafts` is not empty — add a header row (`NodeId` negative, mapped to the group `Key` in a `Dictionary<int, string>`; `Header` = `src.zone` with zone name and localized classes joined by ", ", or the group's text; `HeaderRight` = `src.missing` when `Missing > 0`; `Expanded` = key not in `_sourceCollapsed`), then, unless folded, its lines: price as today, `Place = line.Spot?.PlaceName`, `ExtraTip` from `src.tip.gather` / `src.tip.timed` / `src.tip.also` / `src.tip.vendor` as applicable. Crafts lines: icon and name from `Craft`, `QtyText = src.crafts.qty`, no price.
  `_sourceCollapsed` is a `HashSet<string>` field, cleared in the window's close/dispose path (not saved).

- [ ] **Step 5: Clicks.** `ToggleCartIngredient`: a header `NodeId` toggles its key in `_sourceCollapsed` and calls `RebuildCartList(keepScroll: true)`; other rows in source mode do nothing; tree mode unchanged.

- [ ] **Step 6: Run** `dotnet build GoblinTweaks/GoblinTweaks.csproj -c Release` and `dotnet test GoblinTweaks.Tests` — Expected: build with 0 warnings, all tests PASS.

- [ ] **Step 7: Verify in game** (the user runs it): My List with materials from two zones, one multi-zone material, one vendor item, one drop, one intermediate and crystals.
  - Switch Tree ↔ By source: tree identical to before; By source shows zones first, then Vendor/Other/Craft first/Crystals/You have everything.
  - The gil total is the same in both views; toggling "Deduct what I own" updates both.
  - Fold a zone, change a recipe count: the zone stays folded. Close and reopen the window: all groups open, the chosen view kept.
  - Change the plugin language: selector labels and zone names change.

- [ ] **Step 8: Commit** `git add GoblinTweaks/Tweaks/CraftingMaterials.cs GoblinTweaks/UI && git commit -m "Crafting Materials: By source view of My List totals"`
