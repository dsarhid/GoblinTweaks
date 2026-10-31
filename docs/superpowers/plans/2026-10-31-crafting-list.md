# Crafting Materials "My List" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the player tick several recipes in the Crafting Materials window, set how many times to craft each, and see in a "My List" view the summed materials (expandable tree, owned-stock deduction toggle, estimated price per row and a gil total).

**Architecture:** The math (aggregating ingredients, crafts needed, base materials, cost) and the list operations go in two new pure C# files with no Dalamud types, so a small xUnit project can compile them by link and test them. `CraftingMaterials` adapts its recipe data and inventory snapshot to that pure code (and its existing `ShoppingList` is rewritten on top of it). `CraftingMaterialsAddon` gets a fifth sidebar view, a checkbox and a counter in `RecipeRowNode`, and a totals panel that reuses `IngredientRowNode` with an optional price column.

**Tech Stack:** C# / .NET 10, Dalamud.NET.Sdk 15, KamiToolKit 2.2.38 (native UI only), xUnit for the new pure-code tests.

**Spec:** `docs/superpowers/specs/2026-10-31-crafting-list-design.md`

## Global Constraints

- Native UI only (KamiToolKit). Never ImGui.
- Every new text lives in `GoblinTweaks/UI/CraftLoc.cs`, in both `en` and `es` tables; Spanish in voseo.
- No emoji in game text (the game font has none): the sidebar entry reads "My List (N)" / "Mi lista (N)".
- Counter range: 1–999. Adding a recipe sets its count to 1.
- `CartDeductOwned` defaults to `true`.
- Sub-recipe depth limit: `MaxRecipeDepth` = 6, loops are never followed (same as `ShoppingList` today).
- "Have" = `IngredientCheck.Available` (bags + the sources enabled in the tweak options), exactly as today.
- Unit price = existing `UnitPrice` rule: Universalis average, else cheapest listing.
- The gil total sums base materials only, never intermediates.
- Rows in a recycled `ListNode` take all state from their data (`SetNodeData` runs before `ItemData` updates).
- After repopulating any `ListNode`, call `RequestCollisionRebuild()`.
- CI builds only `GoblinTweaks/GoblinTweaks.csproj`; the test project must not be referenced by it.
- No release work (changelog, version bump) in this plan.

## Review Focus

- A saved list holding a recipe the game data no longer has (patch, corrupted config) → it is dropped silently on load, the rest stays. Test: `Sanitize_DropsUnknownAndDuplicates` (Task 1).
- The same intermediate needed by several listed recipes, with `ResultAmount > 1` → crafts are computed once on the summed need, not per recipe (5 needed, makes 3 → 2 crafts, not 1+1+…). Test: `Children_UsesSummedNeed` (Task 2).
- Recipe loops or very deep chains → no infinite recursion, stops at depth 6. Test: `BaseMaterials_StopsOnLoop` (Task 2).
- Counter typed as 0, empty, negative or 5000 → stored as 1 or 999. Test: `SetCount_Clamps` (Task 1).
- Closing the window or changing the window language while the "My List" view is open and a counter has focus → no crash, no leftover nodes. In-game check in Task 6.

---

### Task 1: Pure list model + test project

**Files:**
- Create: `GoblinTweaks/Crafting/CraftCart.cs`
- Create: `GoblinTweaks.Tests/GoblinTweaks.Tests.csproj`
- Create: `GoblinTweaks.Tests/CraftCartTests.cs`
- Modify: `GoblinTweaks.sln` (add the test project)
- Modify: `.gitignore` (add `.superpowers/`)

**Interfaces:**
- Produces (namespace `GoblinTweaks.Crafting`):
  - `public sealed class CartItem { public uint RecipeId { get; set; } public int Count { get; set; } = 1; }`
  - `public static class CraftCart` with
    `const int MinCount = 1`, `const int MaxCount = 999`,
    `bool Contains(List<CartItem> cart, uint recipeId)`,
    `void Toggle(List<CartItem> cart, uint recipeId)` (absent → append with Count 1; present → remove),
    `void SetCount(List<CartItem> cart, uint recipeId, int count)` (clamped; no-op if absent),
    `void Sanitize(List<CartItem> cart, Func<uint, bool> recipeExists)` (drop unknown ids, keep first of duplicates, clamp counts).

- [ ] **Step 0: Branch.** `git checkout -b feature/crafting-list`

- [ ] **Step 1: Test project.** `GoblinTweaks.Tests.csproj`: `net10.0`, `Nullable` and `ImplicitUsings` enabled, packages `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`; compile the pure files by link, no project reference:

```xml
<Compile Include="..\GoblinTweaks\Crafting\*.cs" Link="Crafting\%(Filename)%(Extension)" />
```

Add it to `GoblinTweaks.sln` (`dotnet sln add GoblinTweaks.Tests`). Add `.superpowers/` to `.gitignore`.

- [ ] **Step 2: Write the failing tests** in `CraftCartTests.cs`:

```csharp
[Fact] public void Toggle_AddsWithCountOneThenRemoves()
{ var c = new List<CartItem>(); CraftCart.Toggle(c, 7); Assert.Single(c); Assert.Equal(1, c[0].Count);
  CraftCart.Toggle(c, 7); Assert.Empty(c); }

[Fact] public void Toggle_KeepsOrderOfAdding()
{ var c = new List<CartItem>(); CraftCart.Toggle(c, 3); CraftCart.Toggle(c, 1); CraftCart.Toggle(c, 2);
  Assert.Equal(new uint[] { 3, 1, 2 }, c.Select(i => i.RecipeId)); }

[Theory] [InlineData(0, 1)] [InlineData(-5, 1)] [InlineData(5000, 999)] [InlineData(42, 42)]
public void SetCount_Clamps(int input, int stored)
{ var c = new List<CartItem> { new() { RecipeId = 9 } }; CraftCart.SetCount(c, 9, input); Assert.Equal(stored, c[0].Count); }

[Fact] public void Sanitize_DropsUnknownAndDuplicates()
{ var c = new List<CartItem> { new() { RecipeId = 1, Count = 2 }, new() { RecipeId = 99 }, new() { RecipeId = 1, Count = 5 }, new() { RecipeId = 2, Count = 0 } };
  CraftCart.Sanitize(c, id => id != 99);
  Assert.Equal(new uint[] { 1, 2 }, c.Select(i => i.RecipeId)); Assert.Equal(2, c[0].Count); Assert.Equal(1, c[1].Count); }
```

- [ ] **Step 3: Run** `dotnet test GoblinTweaks.Tests` → FAIL (types not defined).
- [ ] **Step 4: Implement `CraftCart.cs`** (no `using Dalamud…`, no `GoblinTweaks.Tweaks` types).
- [ ] **Step 5: Run** `dotnet test GoblinTweaks.Tests` → all PASS; `dotnet build GoblinTweaks/GoblinTweaks.csproj -c Release` → succeeds.
- [ ] **Step 6: Commit** `git add -A && git commit -m "Crafting list: list model and test project"`

---

### Task 2: Pure crafting math

**Files:**
- Create: `GoblinTweaks/Crafting/CraftMath.cs`
- Create: `GoblinTweaks.Tests/CraftMathTests.cs`

**Interfaces:**
- Consumes: `CartItem` (Task 1).
- Produces (namespace `GoblinTweaks.Crafting`):
  - `public readonly record struct CraftPart(uint ItemId, string Name, uint IconId, int Amount);`
  - `public sealed record CraftRecipe(int ResultAmount, IReadOnlyList<CraftPart> Ingredients);`
  - `public interface ICraftData { CraftRecipe? Recipe(uint recipeId); uint RecipeFor(uint itemId); int Owned(uint itemId); }` (`RecipeFor` returns 0 when not craftable)
  - `public readonly record struct Need(uint ItemId, string Name, uint IconId, int Amount);`
  - `public readonly record struct CostResult(long Gil, bool Loading, bool Partial);`
  - `public static class CraftMath` with
    `const int MaxDepth = 6`,
    `List<Need> Direct(IEnumerable<CartItem> items, ICraftData data)` — ingredients of every listed recipe × Count, summed by item, in order of first appearance; unknown recipes skipped;
    `int Crafts(int need, int owned, int resultAmount, bool deductOwned)` — `ceil(max(0, need − (deductOwned ? owned : 0)) / max(1, resultAmount))`;
    `List<Need> Children(uint itemId, int need, ICraftData data, bool deductOwned)` — the ingredients of `RecipeFor(itemId)` × `Crafts(...)`; empty when not craftable or 0 crafts;
    `List<Need> BaseMaterials(IEnumerable<CartItem> items, ICraftData data, bool deductOwned)` — today's `ShoppingList` algorithm generalized to several roots with counts (roots enter at depth 1, intermediates only followed while `depth < MaxDepth` and not on the current path; with `deductOwned` intermediates are taken from a running stock seeded from `Owned`, leftovers of a craft go back to the stock; without it nothing is taken). Returns base materials with their total need, order of first appearance;
    `CostResult Cost(IEnumerable<(uint ItemId, int Qty)> buys, Func<uint, long?> unitPrice)` — `null` price = loading (skip, set `Loading`), price ≤ 0 = no price (skip, set `Partial`), Qty ≤ 0 skipped.

- [ ] **Step 1: Write the failing tests** in `CraftMathTests.cs`, with a small in-memory `FakeData : ICraftData` (dictionaries of recipes, result-item→recipe, owned). Items: `Ore`(1), `Ingot`(2, recipe 20: 3 Ore → makes 1), `Lumber`(3, recipe 30: 2 Log → makes 3), `Log`(4), `Spear`(recipe 100: 2 Ingot + 1 Lumber), `Lance`(recipe 101: 1 Ingot + 4 Lumber).

```csharp
[Fact] public void Direct_SumsAcrossRecipesTimesCount()
// cart: Spear×2, Lance×1 → Ingot 5, Lumber 6 (order: Ingot, Lumber)

[Fact] public void Crafts_RoundsUpAndDeducts()
// Crafts(5, 0, 3, true) == 2; Crafts(5, 4, 3, true) == 1; Crafts(5, 9, 3, true) == 0; Crafts(5, 9, 3, false) == 2

[Fact] public void Children_UsesSummedNeed()
// Children(Lumber, 5, data, true) with 0 owned → Log 4 (2 crafts × 2), not per-recipe rounding

[Fact] public void BaseMaterials_DeductsOwnedIntermediates()
// cart Spear×2, owned Ingot 1 → Ore 9 (3 missing ingots × 3), Log 2 (1 lumber craft)

[Fact] public void BaseMaterials_NoDeductIgnoresStock()
// same cart, owned Ingot 1, deductOwned false → Ore 12, Log 2

[Fact] public void BaseMaterials_LeftoversServeNextRecipe()
// cart Spear×1 + Lance×1: Lumber need 1 then 4 → 2 crafts total (makes 3 each), Log 4

[Fact] public void BaseMaterials_StopsOnLoop()
// recipe A needs B, B needs A → returns without stack overflow; A or B appears as a base material

[Fact] public void Cost_SumsAndFlags()
// Cost([(1,3),(2,2),(3,1),(4,0)], id => id switch { 1 => 10, 2 => null, 3 => 0, _ => 500 })
// == new CostResult(30, Loading: true, Partial: true)
```

Write each body with these exact values as asserts.

- [ ] **Step 2: Run** `dotnet test GoblinTweaks.Tests` → FAIL (types not defined).
- [ ] **Step 3: Implement `CraftMath.cs`.** Copy the recursion of `CraftingMaterials.ShoppingList` (`GoblinTweaks/Tweaks/CraftingMaterials.cs:1258-1306`) for `BaseMaterials`, reading through `ICraftData`.
- [ ] **Step 4: Run** `dotnet test GoblinTweaks.Tests` → all PASS.
- [ ] **Step 5: Commit** `git commit -am "Crafting list: pure crafting math"` (add new files first).

---

### Task 3: Tweak side — options, data adapter, ShoppingList on the new math

**Files:**
- Modify: `GoblinTweaks/Tweaks/CraftingMaterials.cs` (`Options` at ~260, `ShoppingList` at ~1258)

**Interfaces:**
- Consumes: `CartItem`, `CraftCart.Sanitize`, `ICraftData`, `CraftMath.BaseMaterials` (Tasks 1–2).
- Produces:
  - `Options.Cart` : `List<CartItem>` = `[]`; `Options.CartDeductOwned` : `bool` = `true`.
  - `internal ICraftData CraftData(InventorySnapshot snap)` — adapter: `Recipe` from `RecipeParts`, `RecipeFor` from `RecipeByResult()`, `Owned` = `snap.GetTotal(id)`.
  - `internal IngredientCheck Check(Need need, InventorySnapshot snap)` — same `InPlayer`/`InOther` split as `ShoppingList` builds today.
  - `internal void SanitizeCart()` — runs `CraftCart.Sanitize(Current.Cart, id => RecipeParts(id) is not null)` and saves if anything changed; called once from `Enable` after settings load.
  - `ShoppingList(uint recipeId, InventorySnapshot snap)` keeps its signature and output (same items, same order: missing first, then by name), now built from `CraftMath.BaseMaterials([new CartItem { RecipeId = recipeId }], CraftData(snap), true)`.

- [ ] **Step 1: Implement the above.** Saving uses the existing `SaveFilters()` (no notification).
- [ ] **Step 2: Build** `dotnet build -c Release` → succeeds, no new warnings.
- [ ] **Step 3: In-game regression check:** open Crafting Materials, pick a recipe with a craftable ingredient you partly own, press the shopping-list button. The list matches what v1.3.4 shows for the same recipe (same items, amounts, order).
- [ ] **Step 4: Commit** `git commit -am "Crafting list: options, data adapter, ShoppingList on shared math"`

---

### Task 4: "My List" view and the checkbox in recipe rows

**Files:**
- Modify: `GoblinTweaks/UI/Nodes/RecipeRowNode.cs`
- Modify: `GoblinTweaks/UI/CraftingMaterialsAddon.cs` (`AddStatusItem` ~1346, `BuildSidebar` ~1188, `UpdateStatusCounts` ~492, `ApplyFilter` ~533, `OnSetup` ~185)
- Modify: `GoblinTweaks/UI/CraftLoc.cs`

**Interfaces:**
- Consumes: `CraftCart.Contains/Toggle`, `Options.Cart` (Tasks 1, 3).
- Produces:
  - Addon const `CartView = "cart"`; `private bool InCartView => _selectedView == CartView;`
  - `RecipeRowNode` statics set by the addon in `OnSetup` (same pattern as `OnItemRightClick`): `internal static Func<uint, bool>? IsInCart;` `internal static Action<uint>? OnToggleCart;`
  - Addon `private void ToggleCart(uint recipeId)` — toggles, saves (`Tweak.SaveFilters()`), updates the sidebar count, re-assigns the recipe list keeping scroll (`AutoResetScroll = false` like `ApplyFilter`), and in cart view rebuilds the totals (Task 6).
  - CraftLoc keys: `status.cart` = "My List" / "Mi lista"; `tip.cart` = "Add to or remove from My List" / "Agregar a Mi lista o quitarla".

- [ ] **Step 1: Row checkbox.** A `CheckboxNode` (no label, ~18×18) left of the icon; icon and text shift right by its width. `SetNodeData` sets `IsChecked = IsInCart?.Invoke(data.RecipeId) == true`; `OnClick` calls `OnToggleCart?.Invoke(ItemData.RecipeId)`. Tooltip `tip.cart`. The click must not select the row: verify in-game; if KTK propagates it, ignore the `OnItemSelected` that follows a toggle in the same frame.
- [ ] **Step 2: Sidebar entry.** Fifth `AddStatusItem(c, ref y, CartView, …)` after `notReady`; the click handler's id map gets `CartView` as fifth entry. `UpdateStatusCounts` writes `$"{CraftLoc.Get("status.cart")} ({cart.Count})"` into `_statusItems[4]`. The category list start moves down one row.
- [ ] **Step 3: Filtering.** In `ApplyFilter`, when `InCartView`: `_filtered` = the cart's recipes in cart order, looked up in `_entries` by `RecipeId` (missing ones skipped), with no search/filter/sort applied; the right panel is not cleared to the placeholder (Task 6 fills it). In cart view `_recipeList.OnItemSelected` does not call `ShowDetailPanel`: the totals stay on the right.
- [ ] **Step 4: Build** `dotnet build -c Release` → succeeds.
- [ ] **Step 5: In-game check:** tick 3 recipes in "All Recipes" → sidebar shows "My List (3)", the detail panel did not change on ticking; tick one off → "(2)"; open "My List" with "Hide crafted" on and a crafted recipe in the list → it is still shown; `/xlplugins` reload → list kept.
- [ ] **Step 6: Commit** `git commit -am "Crafting list: My List view and recipe checkboxes"`

---

### Task 5: Counter and "Clear list" in the My List column

**Files:**
- Modify: `GoblinTweaks/UI/Nodes/RecipeRowNode.cs`
- Modify: `GoblinTweaks/UI/CraftingMaterialsAddon.cs` (`BuildLayout` ~914)
- Modify: `GoblinTweaks/UI/CraftLoc.cs`

**Interfaces:**
- Consumes: `CraftCart.SetCount`, `ToggleCart`, `InCartView` (Tasks 1, 4).
- Produces:
  - `RecipeRowNode` statics: `internal static bool ShowCounter;` `internal static Func<uint, int>? CountOf;` `internal static Action<uint, int>? OnCountChanged;`
  - Addon `private void SetCartCount(uint recipeId, int count)` — `CraftCart.SetCount`, save, rebuild totals.
  - Addon `private void ClearCart()` — empties the list, saves, updates count, re-runs `ApplyFilter`.
  - CraftLoc keys: `cart.clear` = "Clear list" / "Vaciar lista"; `tip.count` = "How many times you'll craft it" / "Cuántas veces la vas a craftear".

- [ ] **Step 1: Counter.** A `NumericInputNode` (`Min` 1, `Max` 999, `Step` 1, ~80×28) at the right of the row, visible only when `ShowCounter`; the name and subtitle width shrink while it is visible. `SetNodeData` sets `Value = CountOf(recipeId)` behind a `syncing` guard so it does not echo back (pattern: `BattleTextAddon.cs:401-420`); value changes call `OnCountChanged`.
- [ ] **Step 2: Clear button.** A `TextButtonNode` `cart.clear` under the middle column, visible only in cart view with a non-empty list; in cart view the recipe list height shrinks by the button row (restore on leaving). If resizing the `ListNode` at runtime misbehaves, put the button in the right panel's header row instead and tell the user.
- [ ] **Step 3: Build** → succeeds.
- [ ] **Step 4: In-game check:** in My List, set a count with the arrows and by typing 0, 5000 and an empty value → shows 1, 999, previous value; scroll a list of 15+ recipes → each row keeps its own count after scrolling; "Clear list" → list empty, sidebar "(0)", ticks gone in "All Recipes".
- [ ] **Step 5: Commit** `git commit -am "Crafting list: counters and clear button"`

---

### Task 6: Totals panel (tree, deduct toggle, price column, gil total)

**Files:**
- Modify: `GoblinTweaks/UI/Nodes/IngredientRowNode.cs`
- Modify: `GoblinTweaks/UI/CraftingMaterialsAddon.cs` (`BuildDetailPanel` ~1223, `MakeIngNode` ~707, `ToggleIngredient` ~724, `Update` ~217, `ListedItemIds`/`FCChestMarks`/`IngredientItemIds` ~160-181)
- Modify: `GoblinTweaks/UI/CraftLoc.cs`

**Interfaces:**
- Consumes: `CraftMath.Direct/Children/BaseMaterials/Cost`, `Tweak.CraftData`, `Tweak.Check`, `Options.CartDeductOwned` (Tasks 2–3), `InCartView`.
- Produces:
  - `IngredientRow` gains two trailing optional members: `string? Price = null, string? PriceTip = null`. Existing call sites unchanged.
  - `IngredientRowNode`: when `Price` is not null, a right-aligned price column (~70 px) at the far right, the location column shrinks by that width; `PriceTip` is appended as a new line to `TextTooltip` when the row uses the text tooltip. With `Price == null` the row looks exactly as today.
  - Addon: separate tree state for the totals (`_cartRoots`, `_cartById`), built with `MakeIngNode` writing into the dictionary it is given; expanded nodes remembered by item-id path (`"2/4"`) in `_cartExpanded` so a recount keeps them open.
  - Addon `private void RebuildCart()` — computes roots from `CraftMath.Direct`, children on expand from `CraftMath.Children(itemId, node need, data, deduct)`, price strings, the total line; requests Universalis (`urgent: true`) for every tree item and every base material.
  - CraftLoc keys (en / es):
    `cart.title` "Total materials" / "Total de materiales";
    `cart.deduct` "Deduct what I own" / "Descontar lo que ya tengo";
    `cart.col.item` "Material" / "Material"; `cart.col.qty` "Have / Need" / "Tengo / Necesito"; `cart.col.price` "Est. price" / "Precio est.";
    `cart.total` "Total of what's missing: {0}g" / "Total de lo que falta: {0}g";
    `cart.totalall` "Total: {0}g" / "Total: {0}g";
    `cart.totalmin` "At least {0}g (some items have no price)" / "Al menos {0}g (algunos ítems no tienen precio)";
    `cart.loading` "Loading prices…" / "Cargando precios…";
    `cart.unit` "Market: ~{0}g each" / "Mercado: ~{0}g c/u";
    `cart.empty` "Tick recipes with the box to add them here." / "Marcá recetas con la casilla para sumarlas acá."

- [ ] **Step 1: Row price column** in `IngredientRowNode` as above.
- [ ] **Step 2: Panel nodes.** In cart view the recipe header (icon, name, subtitle, market, shopping button, ingredients label) is hidden and these show: `cart.title`, a `CheckboxNode` `cart.deduct` bound to `Options.CartDeductOwned` (saves, rebuilds), three column header labels aligned with the row columns, a `ListNode<IngredientRow, IngredientRowNode>` for the tree (selection toggles expand like `_ingredientList`), and a total `TextNode` under it. Empty list → only `cart.empty`. Leaving cart view restores the normal panel.
- [ ] **Step 3: Values.** Row `Price`: unit price × (deduct ? `Missing` : `Required`); `"—"` when that quantity is 0; `"…"` while pending; `""` without a price. `PriceTip`: `cart.unit` when a price exists. Total: `CraftMath.Cost` over `BaseMaterials` with Qty = deduct ? `max(0, need − Owned)` : need, unit price via the existing `UnitPrice` (null while `IsPending`). Text: `cart.loading` if `Loading`, `cart.totalmin` if `Partial`, else `cart.total` (deduct on) / `cart.totalall` (deduct off).
- [ ] **Step 4: Refresh.** `RebuildCart` runs on: entering cart view, toggle, count change, clear, deduct toggle, data reload (inventory debounce already reloads). In `Update`, while in cart view, every 0.5 s rebuild the price strings if the number of tree/base items with Universalis data changed (same idea as `_marketDataReadyCount`).
- [ ] **Step 5: Highlights.** In cart view `ListedItemIds` returns the `_cartById` item ids; `FCChestMarks` and `IngredientItemIds` no longer return empty just because `_selectedEntry` is null when `InCartView`.
- [ ] **Step 6: Build** → succeeds.
- [ ] **Step 7: In-game check** (test character with some materials):
  - Two listed recipes that share an ingredient → one row with the sum.
  - Expand an intermediate, change a count → it stays expanded and children amounts change.
  - Deduct on → owned rows show "—" and the total drops; off → every row has a price and the label says "Total:".
  - Hover a row spread across inventories → locations plus "Market: ~Xg each"; hover an icon in the middle column → game item tooltip; right-click a row → item menu.
  - Open the FC chest / a retainer / a shop with My List open → listed materials are outlined/dimmed as for a selected recipe.
  - Move an item into your bags → totals update within ~2 s.
  - Switch window language in tweak settings and close the window while in My List with a counter focused → no crash; reopen → still in My List.
  - English and Spanish: every new text appears translated, nothing shows a raw key.
- [ ] **Step 8: Commit** `git commit -am "Crafting list: totals panel with prices"`

---

### Task 7: Final verification

- [ ] **Step 1:** `dotnet test GoblinTweaks.Tests` → all PASS; `dotnet build -c Release` → succeeds with no new warnings.
- [ ] **Step 2:** Re-run the in-game checks of Tasks 3–6 on the final build; note anything that differs from the spec for the user.
- [ ] **Step 3:** Hand the branch to the user for in-game testing. No release steps.
