# Crafting Materials "By source" map and teleport buttons — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Map and Teleport buttons on the zone rows of My List "By source": the map shows the gathering areas like the
Gathering Log; Teleport goes to the nearest attuned aetheryte and, once landed, equips the gathering class's gearset.

**Architecture:** Pure math and the job-switch state machine in `Crafting/TravelMath.cs` (tested). Areas per item and
zone in `Core/GatheringSources.cs`. Game calls (`AgentMap`, `Telepo`, `RaptureGearsetModule`) in
`Services/GatherTravel.cs`, sharing the aetheryte search with `HuntTravel`. Buttons in `IngredientRowNode`.

**Tech Stack:** C#/.NET 10, Dalamud, FFXIVClientStructs, KamiToolKit, Lumina, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-15-gcm-source-travel-design.md`

## Global Constraints

- Native KamiToolKit UI only, never ImGui. Texts in `UI/CraftLoc.cs` (en + es, voseo).
- No Dalamud types in `Crafting/` (compiled by `GoblinTweaks.Tests`).
- Verified game data (2026-11-15): `ExportedGatheringPoint` X/Y are world coordinates, Radius in yalms (Granite,
  Central Shroud base 178 → map 14.1, 22.1, radius 82). `FishingSpot` X/Z and `SpearfishingNotebook` X/Y are map texture
  pixels; their radius scale is unverified (÷7 assumed, check in game). `GatheringPointName` row =
  `ExportedGatheringPoint.GatheringPointType` ("mineral deposit"). `GatheringType.IconMain`: 60438/60437/60433/60432,
  spearfishing 60929. Classes: Miner 16, Botanist 17, Fisher 18.
- `AgentMap.AddGatheringTempMarker(mapX, mapY, radius, iconId, styleFlags = 4, tooltip)` and
  `OpenMap(mapId, territoryId, title, MapType.GatheringLog)`; `ResetMapMarkers()` clears temp markers. Unit of
  mapX/mapY assumed world coordinates (to confirm in game; one conversion function).
- Job switch only after the zone load ends, in the area's zone, not in combat or casting; cancel after 60 s or in
  another zone; a new click replaces the pending one; disabling the tweak cancels it.

## Review Focus

- A list row recycled from a material row to a header/vendor row must hide the buttons and not keep a dead or stale hit
  area (collision list rebuilt when the set of shown buttons changes).
- Mouse wheel over the buttons must still scroll the list.
- Teleport accepted but cancelled by the player (moved while casting): no job change, pending expires.
- Teleport inside the zone you are already in: the job changes after that load.
- An item with no usable area (no map, radius 0): no buttons, no crash.

---

### Task 1: TravelMath (pure, tested)

**Files:** Create `GoblinTweaks/Crafting/TravelMath.cs`, `GoblinTweaks.Tests/TravelMathTests.cs`

- `float WorldToMapCoord(float world, ushort sizeFactor, short offset)` = `41/(sf/100) * ((world+offset)*(sf/100)+1024)/2048 + 1`.
- `float PixelToWorld(float pixel, ushort sizeFactor, short offset)` = `(pixel-1024)/(sf/100) - offset`.
- `int? PickGearset(IEnumerable<(int Id, uint ClassJob, int ItemLevel)> sets, uint classJob)`: highest item level, tie lowest id.
- `JobSwitch` state machine: `Step(bool betweenAreas, uint territory, bool ready, double elapsedSeconds)` returning
  `Wait | Switch | Cancel`; target territory given at construction; Switch only after a load was seen and ended,
  territory == target and ready; Cancel when elapsed > 60 or the load ended in another territory.
- Tests: Granite (−368.4, 29.0, sf 100, offset 0) → (14.1, 22.1); sf 200 with offsets; pixel 1024 → −offset;
  gearset none / several / tie; state machine paths (arrive, other zone, timeout, not ready waits, no load yet waits).

### Task 2: Areas in GatheringSources

**Files:** Modify `GoblinTweaks/Core/GatheringSources.cs`; add `GatherArea` record to `Crafting/SourcePlan.cs`
(`GatherArea(uint ZoneId, uint MapId, GatherClass Class, float X, float Y, float Radius, uint IconId, string Label)`, world units).

- `IReadOnlyList<GatherArea> Areas(uint itemId, uint zoneId, GatherClass cls)`.
- Mining/botany/spearfishing: `ExportedGatheringPoint` row of the point's base (dedupe by base); spearfishing falls back
  to `SpearfishingNotebook`. Fishing: `FishingSpot` (pixels → world, radius ÷ 7 ÷ scale). Map: `TerritoryType.Map`.
  Label "Lv. {level} {GatheringPointName, title case in English}" (fishing: "Lv. {level} {place}"). Skip invalid / radius 0 / no map.

### Task 3: GatherTravel service

**Files:** Create `GoblinTweaks/Services/GatherTravel.cs`; modify `Services/HuntTravel.cs` (extract
`NearestAttuned(uint zoneId, uint mapId, IEnumerable<(float X, float Y)> mapPoints)` without changing Hunts behavior).

- `OpenMap(areas, title)`, `Teleport(areas, cls) → result`, pending `JobSwitch` polled from `Framework.Update`,
  `Cancel()`. Marks re-placed on arrival. Notices through toast + chat (tweak name).

### Task 4: Row buttons and wiring

**Files:** Modify `UI/Nodes/IngredientRowNode.cs`, `UI/CraftingMaterialsAddon.cs`, `UI/CraftLoc.cs`,
`UI/CraftingHelpAddon.cs`, `Tweaks/CraftingMaterials.cs`.

- `IngredientRow` gains `bool TravelColumn`, `TravelTarget? Travel` (item id, name, zone id, class).
- Two 18×18 buttons right of the price (Goblin Hunts look), hit areas with tooltip, wheel forwarded to the list; hidden
  when `Travel` is null; static `OnMap` / `OnTeleport` / `OnHitAreasChanged` callbacks.
- Column header price label moves left by the button column in By source.
- Texts: `travel.map`, `travel.tp`, `travel.refused`, `travel.noaetheryte`, `travel.nogearset`, help line.
