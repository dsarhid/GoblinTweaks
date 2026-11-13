# Goblin Hunts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A GoblinTweaks tweak that lists the hunts (A/S) and FATEs Sonar announces in a small GDD-style native table, with a map button, a teleport button, a filter, sound alerts, settings and help.

**Architecture:** The pure logic (entries, merging, expiry, ordering, filter, chat parsing, coordinate math) lives in a new Dalamud-free folder `GoblinTweaks/Hunts/`, compiled into the test project by link and covered by xUnit tests. Dalamud-bound parts sit on top: a fail-soft reflection reader of Sonar's memory, an overlay table node, a travel helper (map + teleport), the tweak that wires chat/framework/objects, and its settings and help windows.

**Tech Stack:** C# / .NET 10, Dalamud API 15 (`Dalamud.NET.Sdk/15.0.0`), KamiToolKit 2.2.38 (native UI), FFXIVClientStructs (`Telepo`, `UIGlobals`), Lumina sheets, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-12-goblin-hunts-design.md` (Spanish). Read it before starting any task.

## Global Constraints

- UI is always native KamiToolKit, never ImGui. Overlay nodes go on `SharedOverlay` through `OverlayNodes`; never `new OverlayController()`.
- Every overlay node of this tweak uses `OverlayLayer.BehindUserInterface`.
- `Enable`/`Disable` run on the game thread; `Disable` undoes everything (`OverlayNodes.Dispose`, `Svc.Chat.ChatMessage -=`, `Svc.Framework.Update -=`, windows disposed).
- Files in `GoblinTweaks/Hunts/` must not reference Dalamud, KamiToolKit, Lumina or FFXIVClientStructs (they compile in `GoblinTweaks.Tests`).
- Tweak class `GoblinHunts`, category `TweakCategory.Other`, commands `/ghunt` (first) and `/goblinhunts`; icon `GoblinTweaks/Assets/IconTweaks/ghuntIcon.png` (128×128 RGBA).
- All text in `GoblinTweaks/Localization/en.json` and `es.json` under `Tweaks.GoblinHunts.*`; Spanish in voseo.
- Expiry: dead hunt 5 min after death; chat-only FATE 30 min after found; hunt or FATE with no update 1 h; ended FATE removed at once.
- Defaults: Rows 8, Locked false, Sound on, SoundId 6 (range 1–16), Opacity 62, FontSize 14 (8–24), Scale 100 (50–200), Filter All.
- Ul'dah aetheryte id: `9` (Ul'dah - Steps of Nald). Dravanian Hinterlands (territory `399`) has no aetheryte: candidates `75` (Idyllshire) then `77` (Anyx Trine).
- Sonar memory failures never call `ReportFailure`: they switch that source off, log one `Svc.Log.Warning`, retry every 30 s.
- Nothing is written to Sonar; no network calls.
- Do not commit unless the user has said to; the "Commit" steps apply only then.
- Build: `dotnet build -c Release` (repo root). Tests: `dotnet test GoblinTweaks.Tests/GoblinTweaks.Tests.csproj`.

## Review Focus

1. Sonar's memory read starts failing mid-session (Sonar updated or reloaded): the tweak stays enabled, entries known only from memory fall back to the tweak's own expiry timers instead of freezing, chat entries keep arriving. → Task 1 test `MemoryLost_EntriesFallBackToOwnTimers`.
2. The same hunt arrives by chat and by memory: one row, one sound. → Task 1 test `SameHuntFromChatAndMemory_IsOneEntryAndNewOnce`.
3. A message from sender "Sonar" that is not a relay (connection notices, B ranks, "Rank None"): ignored. → Task 2 tests `NonRelayText_ReturnsNull`, `RankB_ReturnsNull`.
4. The same hunt name on two worlds, or in two instances of a zone: separate rows. → Task 1 test `SameNameDifferentWorldOrInstance_AreSeparate`; Task 2 test `InstanceIcon_IsParsed`.
5. Enabling the tweak while Sonar already holds a long list: no burst of sounds. → Task 1 test `ReplaceMemory_WithoutAnnounce_ReturnsNoNewEntries`.

---

### Task 1: Hunt list core (entries, merge, expiry, order, filter)

**Files:**
- Create: `GoblinTweaks/Hunts/HuntTypes.cs`
- Create: `GoblinTweaks/Hunts/HuntList.cs`
- Modify: `GoblinTweaks.Tests/GoblinTweaks.Tests.csproj` (add `<Compile Include="..\GoblinTweaks\Hunts\*.cs" Link="Hunts\%(Filename)%(Extension)" />` and `<Using Include="GoblinTweaks.Hunts" />`)
- Test: `GoblinTweaks.Tests/HuntListTests.cs`

**Interfaces:**
- Produces (namespace `GoblinTweaks.Hunts`):
  - `public enum HuntKind { A, S, Fate }`
  - `public enum HuntFilter { All, S, A, Fate }`
  - `public static class HuntFilters { public static HuntFilter Next(HuntFilter f); public static bool Accepts(HuntFilter f, HuntKind k); public static string Label(HuntFilter f); }` — labels `"All"`, `"S"`, `"A"`, `"FATE"`.
  - `[Flags] public enum HuntSources { None = 0, Chat = 1, Memory = 2 }`
  - `public readonly record struct HuntKey(HuntKind Kind, string Name, uint WorldId, uint ZoneId, uint InstanceId)` — `Name` compared case-insensitively (normalize to `ToUpperInvariant()` in a `static HuntKey Of(...)` factory).
  - `public sealed record HuntReport(HuntKind Kind, string Name, uint WorldId, string WorldName, uint ZoneId, uint MapId, uint InstanceId, float X, float Y, float? Percent, int? Players, bool Dead, DateTime? FoundAt, HuntSources Source)` — `X`/`Y` are map coordinates; `Percent` is HP for hunts and progress for FATEs, null when unknown; `Dead` means hunt killed or FATE ended.
  - `public sealed class HuntEntry` — mutable: `Key`, `Kind`, `Name`, `WorldId`, `WorldName`, `ZoneId`, `MapId`, `InstanceId`, `X`, `Y`, `float Percent` (default 100 for hunts, 0 for FATEs), `int? Players`, `bool Dead`, `DateTime FoundAt`, `DateTime UpdatedAt`, `DateTime? DeadAt`, `HuntSources Sources`.
  - `public enum HuntRowState { Untouched, Pulled, Dead, Fate }` and `public static HuntRowState StateOf(HuntEntry e)` on `HuntList` (Fate for any live FATE; Dead for dead hunts; Pulled when `Percent < 100`).
  - `public sealed class HuntList`:
    - `public static readonly TimeSpan DeadKeep = TimeSpan.FromMinutes(5), FateChatKeep = TimeSpan.FromMinutes(30), StaleKeep = TimeSpan.FromHours(1);`
    - `public HuntEntry? Upsert(HuntReport report, DateTime now)` — returns the entry only when it was created by this call.
    - `public IReadOnlyList<HuntEntry> ReplaceMemory(IReadOnlyList<HuntReport> reports, DateTime now, bool announce)` — upserts all; entries no longer reported lose `Memory`; entries left with `None` are removed; returns newly created entries, or empty when `announce` is false.
    - `public void MemoryLost()` — strips `Memory` from every entry (keeps them; own timers now apply).
    - `public void SetLivePercent(HuntKey key, float percent, DateTime now)`
    - `public void Expire(DateTime now)` — removes: dead entries with `now - DeadAt > DeadKeep`; FATEs whose last report had `Dead`; chat-only FATEs with `now - FoundAt > FateChatKeep`; entries without `Memory` and `now - UpdatedAt > StaleKeep`.
    - `public IReadOnlyList<HuntEntry> Visible(HuntFilter filter)` — newest `FoundAt` first.
- Merge rule: `FoundAt` = report's `FoundAt` if given, else the earliest time seen; a report never moves `FoundAt` later. A memory report overwrites `Percent`/`Players`/`Dead`/position; a chat report sets `Dead` and fills only fields the entry lacks. `DeadAt` set the first time `Dead` becomes true.

- [ ] **Step 1: Write the failing tests** in `HuntListTests.cs`:

```csharp
static readonly DateTime T0 = new(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc);
static HuntReport Chat(string name, HuntKind kind = HuntKind.A, uint world = 1, uint inst = 0, bool dead = false, float? pct = null)
    => new(kind, name, world, "Jenova", 100, 10, inst, 18.5f, 13.6f, pct, null, dead, null, HuntSources.Chat);
static HuntReport Mem(string name, float pct, int players, DateTime found, bool dead = false, HuntKind kind = HuntKind.A)
    => new(kind, name, 1, "Jenova", 100, 10, 0, 18.5f, 13.6f, pct, players, dead, found, HuntSources.Memory);

[Fact] void Upsert_NewReturnsEntry_RepeatReturnsNull()      // first Upsert != null, second == null, Visible count 1
[Fact] void Visible_IsNewestFoundFirst()                     // Rusalka at T0, Baal at T0+1m → [Baal, Rusalka]; then updating Rusalka keeps order
[Fact] void Filter_S_A_Fate()                                 // Accepts(S,S)=true, Accepts(S,A)=false, Accepts(All,Fate)=true; Next: All→S→A→Fate→All; Label(Fate)=="FATE"
[Fact] void SameHuntFromChatAndMemory_IsOneEntryAndNewOnce() // Upsert(Chat("Rusalka")) new; ReplaceMemory([Mem("rusalka",62,7,T0)], announce:true) returns empty; one entry, Percent 62, Players 7, Sources Chat|Memory
[Fact] void SameNameDifferentWorldOrInstance_AreSeparate()   // world 1 vs 2, instance 1 vs 2 → 3 entries
[Fact] void DeadHunt_StaysFiveMinutes()                       // dead at T0: Expire(T0+4m59s) keeps, Expire(T0+5m01s) removes; StateOf == Dead
[Fact] void ChatOnlyFate_ExpiresAfter30Minutes()             // Fate from chat at T0: kept at T0+29m, gone at T0+31m
[Fact] void EndedFate_RemovedAtOnce()                         // Mem(kind Fate, dead:true) then Expire(now) → gone
[Fact] void ChatOnlyHunt_ExpiresAfterOneHourWithoutUpdate()  // kept at T0+59m, gone at T0+61m
[Fact] void ReplaceMemory_DropsEntriesSonarNoLongerShows()   // memory-only entry absent from next ReplaceMemory → removed; chat+memory entry → stays with Sources Chat
[Fact] void ReplaceMemory_WithoutAnnounce_ReturnsNoNewEntries() // 3 new reports, announce:false → empty result, 3 entries visible
[Fact] void MemoryLost_EntriesFallBackToOwnTimers()          // memory-only entry, MemoryLost(), Expire(T0+61m) → removed
[Fact] void StateOf_UntouchedPulledFate()                    // Percent 100 → Untouched; 62 → Pulled; live Fate → Fate
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test GoblinTweaks.Tests/GoblinTweaks.Tests.csproj --filter FullyQualifiedName~HuntListTests`
Expected: build FAIL (types not defined).

- [ ] **Step 3: Implement `HuntTypes.cs` and `HuntList.cs`** with the signatures above. Store entries in a `Dictionary<HuntKey, HuntEntry>`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test GoblinTweaks.Tests/GoblinTweaks.Tests.csproj --filter FullyQualifiedName~HuntListTests`
Expected: all PASS.

- [ ] **Step 5: Commit** (when authorized): `git add GoblinTweaks/Hunts GoblinTweaks.Tests` · `git commit -m "Goblin Hunts: hunt list core"`

---

### Task 2: Sonar chat message parser

**Files:**
- Create: `GoblinTweaks/Hunts/SonarChatParser.cs`
- Test: `GoblinTweaks.Tests/SonarChatParserTests.cs`

**Interfaces:**
- Consumes: `HuntKind` (Task 1).
- Produces:
  - `public sealed record SonarChatMessage(HuntKind Kind, string Name, string WorldName, uint InstanceId, float? Percent, bool Dead)`
  - `public static class SonarChatParser { public static SonarChatMessage? Parse(string before, string after); }`
    - `before`: the message text before the map link payload (e.g. `"Rank A: Rusalka "`).
    - `after`: the text payloads after the link terminator, concatenated (icons that are not text are skipped by the caller), e.g. `"  <Jenova> was just killed"`.
- Rules (from `SonarPlugin/Game/PayloadExtensions.cs`): `before` matches `^Rank (\S+): (.+?)\s*$` or `^FATE: (.+?)\s*$`. Rank `A` → A; `S`, `SS`, `SSMinion` → S; anything else → null. Instance: first char in ``–`` → 1–9, else 0. World: text between the last `<` and the following `>`. `Dead` when `after` contains `" was just killed"`. FATE percent: `(N% with` → N; `(in Preparation)` → 0; else null. Return null when `before` or the world does not match.

- [ ] **Step 1: Write the failing tests**:

```csharp
[Fact] void HuntFound()       // Parse("Rank A: Rusalka ", " <Jenova>") == new(HuntKind.A, "Rusalka", "Jenova", 0, null, false)
[Fact] void HuntKilled()      // Parse("Rank S: Bone Crawler ", " <Jenova> was just killed").Dead == true, Kind S
[Fact] void SsAndMinion_AreS() // "Rank SS: X " and "Rank SSMinion: Y " → Kind S
[Fact] void RankB_ReturnsNull()
[Fact] void InstanceIcon_IsParsed() // Parse("Rank A: Supay ", "  <Jenova>").InstanceId == 2
[Fact] void FateRunning()     // Parse("FATE: Long Live the Coeurl ", " <Jenova> (45% with 12:30 remaining)") → Kind Fate, Percent 45
[Fact] void FatePreparation() // " <Jenova> (in Preparation)" → Percent 0
[Fact] void LocalizedName_IsKeptAsIs() // Parse("Rank A: ルサルカ ", " <Jenova>").Name == "ルサルカ"
[Fact] void NonRelayText_ReturnsNull() // Parse("Connected to Sonar server", "") == null; Parse("Rank A: Rusalka ", " no world") == null
```

- [ ] **Step 2: Run to verify they fail** — `dotnet test GoblinTweaks.Tests/GoblinTweaks.Tests.csproj --filter FullyQualifiedName~SonarChatParserTests` → build FAIL.
- [ ] **Step 3: Implement `SonarChatParser.Parse`** with compiled `Regex` fields; percent parsed with `CultureInfo.InvariantCulture`.
- [ ] **Step 4: Run to verify they pass** — same command → all PASS.
- [ ] **Step 5: Commit** (when authorized): `"Goblin Hunts: Sonar chat parser"`

---

### Task 3: Map math (marker coordinates, nearest aetheryte)

**Files:**
- Create: `GoblinTweaks/Hunts/HuntMath.cs`
- Test: `GoblinTweaks.Tests/HuntMathTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct AetheryteSpot(uint Id, float X, float Y)` — map coordinates.
  - `public static class HuntMath`
    - `public static float MarkerToMapCoord(short pixel, ushort sizeFactor)` = `41f / (sizeFactor / 100f) * (pixel / 2048f) + 1f` (MapMarker X/Y are pixels of the 2048 map texture).
    - `public static uint? Nearest(IReadOnlyList<AetheryteSpot> spots, float x, float y)` — smallest Euclidean distance; null when empty.

- [ ] **Step 1: Write the failing tests**:

```csharp
[Fact] void MarkerToMapCoord_Center()   // MarkerToMapCoord(1024, 100) == 21.5f (±0.001); MarkerToMapCoord(1024, 200) == 11.25f
[Fact] void Nearest_PicksClosest()      // spots (1,10,10),(2,30,30); Nearest(spots, 27, 25) == 2
[Fact] void Nearest_EmptyIsNull()
```

- [ ] **Step 2: Run to verify they fail** — `--filter FullyQualifiedName~HuntMathTests` → build FAIL.
- [ ] **Step 3: Implement `HuntMath`.**
- [ ] **Step 4: Run to verify they pass** → all PASS; then run the whole test project → all PASS.
- [ ] **Step 5: Commit** (when authorized): `"Goblin Hunts: map math"`

---

### Task 4: Fail-soft reader of Sonar's memory

**Files:**
- Create: `GoblinTweaks/Services/SonarReader.cs`
- Modify: `GoblinTweaks/Svc.cs` (add `[PluginService] public static IObjectTable Objects { get; private set; } = null!;`, used by Task 6)

**Interfaces:**
- Consumes: `HuntReport`, `HuntKind`, `HuntSources` (Task 1).
- Produces:
  - `internal enum SonarReadStatus { NotInstalled, Ok, Failed }`
  - `internal sealed class SonarReader { public SonarReadStatus Status { get; } public IReadOnlyList<HuntReport>? Read(DateTime now); public void Reset(); }`
    - `Read` never throws. Returns null when Sonar is not loaded or the read failed (then `Status` says which). After a failure it returns null without trying again until 30 s have passed. Logs one `Svc.Log.Warning("GoblinHunts: could not read Sonar ({reason}); the table keeps what Sonar writes in the chat.")` per failure streak; logs `Info` once when reading works again.
    - `Reset` drops every cached reflection member and the cached Sonar instance (called from `Disable`).

Path to the data (SonarDistrib build `15.757.0.1`; all by reflection, no reference to Sonar):
1. Dalamud internals: from `typeof(IDalamudPluginInterface).Assembly`, the generic `Dalamud.Service`1` closed over `Dalamud.Plugin.Internal.PluginManager`, static `Get()` → property `InstalledPlugins` → the `LocalPlugin` whose `InternalName` is `SonarPlugin` or `SonarPlugin-preview` (or starts with `SonarPlugin`) and is loaded → its private instance field (look for the field assignable to `IDalamudPlugin`). Use `Svc.PluginInterface.InstalledPlugins` first to return `NotInstalled` cheaply when no loaded Sonar exists.
2. On the plugin object (`SonarPlugin.SonarPluginIoC`): private field `_container` (DryIoc) → `Resolve(Type)` for the type named `SonarPlugin.Trackers.RelayTrackerViews` from the plugin's assembly → properties `Hunts` and `Fates` (views that already apply Sonar's settings and display timers). Fallback if that fails: private property `Client` → `Trackers` → `Hunts` / `Fates` (raw; the list's own timers then apply).
3. Each view/tracker → `Data` → `States` (dictionary-like; enumerate values) → per state: `Relay`, `LastFound` (double Unix seconds).
4. Hunt relay: `Info.Rank` (enum, `ToString()`: `A`, `S`, `SS`, `SSMinion` map like Task 2; others skipped), `Info.Name` (`ToString()`), `WorldId`, `ZoneId`, `InstanceId`, `Coords` (`X`, `Y` map coordinates), `HpPercent`, `Players`, `IsAlive()`. World name from the `World` sheet (`Svc.Data`). Map id from the `TerritoryType` sheet (`Map.RowId`).
5. FATE relay: `Info.Name`, same position members, `Progress`, `Players`, `IsAlive()` (false → `Dead`).

Never keep references to Sonar objects past one `Read`; cache only `MemberInfo`s and the plugin instance, and re-resolve when the instance changes.

- [ ] **Step 1: Implement `SonarReader`** as above.
- [ ] **Step 2: Build** — `dotnet build -c Release` → `0 Errores`/`0 Errors`.
- [ ] **Step 3: Commit** (when authorized): `"Goblin Hunts: Sonar memory reader"`

(Verified in game in Task 8.)

---

### Task 5: The table node

**Files:**
- Create: `GoblinTweaks/UI/Nodes/HuntTableNode.cs`

**Interfaces:**
- Consumes: `DamageMeterHandleNode` (title + drag handle, in `UI/Nodes/DamageMeterNode.cs`), `MeterRows.Text/Fit/Click/Wheel` (`UI/Nodes/MeterRows.cs`), the padlock/cog/cross glyph drawing style of `DamageMeterNode`.
- Produces:
  - `internal readonly record struct HuntRow(string Badge, Vector4 BadgeColor, string Players, string Name, string Percent, string Zone, string World, bool OtherWorld, float Fraction, Vector4 BarColor, bool Struck, bool CanTeleport)`
  - `internal sealed class HuntTableNode : OverlayNode` with `OverlayLayer => BehindUserInterface`, `public const int MaxRows = 24; public const float PanelWidth = 420f, HeaderH = 26f, RowH = 22f;`
    - `public DamageMeterHandleNode Handle { get; }` (added to the overlay next to the node, like GDD)
    - `public string Title { set; }`, `public string FilterLabel { set; }`, `public bool Locked { set; }`, `public float Opacity { set; }`, `public int FontSize { set; }`, `public float Zoom { set; }`, `public bool Shown { set; }`
    - `public void SetRows(IReadOnlyList<HuntRow> rows, int first, int total)` — same height/scroll-thumb behavior as `DamageMeterNode.SetRows`.
    - `public void SetTooltips(string filter, string lockTip, string settings, string close, string map, string teleport)`
    - Events: `Action? OnToggleFilter, OnToggleLock, OnOpenSettings, OnClose; Action<int>? OnMap, OnTeleport, OnScroll` (row index = index among shown rows).
- Layout of a row, left to right: badge square 18 px with the letter; players; name (cut with "..." via `MeterRows.Fit`); percent right-aligned; zone (cut); world (yellow `#F0C060` when `OtherWorld`, else `#99AABB`); map button; teleport button (grey when `!CanTeleport`). Bar behind the row: width `Fraction * PanelWidth`, color `BarColor`, alpha 0.55. Header: title on the left, then filter button (text, shows `FilterLabel`), padlock, cog, cross.
- Colors (fixed by the approved sketch `layout-v3.html`): badge S `#B58A2A`, A `#3D6FB5`, F `#7B3FB5`; bars untouched `#2E8B3E`, pulled `#C7761A`, dead `#A02828`, FATE `#7B3FB5`; map button `#3A6A8A`, teleport `#C77A1A`.

- [ ] **Step 1: Implement `HuntTableNode`.** Map and teleport buttons draw simple glyphs with `ColorImageNode` pieces (a folded map and a diamond), like the GDD header glyphs.
- [ ] **Step 2: Build** → no errors.
- [ ] **Step 3: Commit** (when authorized): `"Goblin Hunts: table node"`

---

### Task 6: The tweak: sources, list, table, filter, scroll, sound

**Files:**
- Create: `GoblinTweaks/Tweaks/GoblinHunts.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 4, 5.
- Produces:
  - `[Tweak(TweakCategory.Other)] public sealed unsafe class GoblinHunts : Tweak<GoblinHunts.Options>`
  - `public sealed class Options { int OffsetX = -760; int OffsetY = -200; int Rows = 8; bool Visible = true; bool Locked; HuntFilter Filter = HuntFilter.All; bool SoundAlert = true; int SoundId = 6; int Opacity = 62; int FontSize = 14; int Scale = 100; }` (auto-properties with these defaults)
  - `internal Options Config`, `internal string Text(string key)`, `internal void Changed()`, `internal void OpenHelp()`, `internal void PlaySound()` (used by Task 7), `internal void ResetPosition()`.
  - Commands `["/ghunt", "/goblinhunts"]`; `OnCommand` toggles `Options.Visible`.
- Behavior:
  - `Enable`: create `HuntList`, `SonarReader`, `OverlayNodes` with `HuntTableNode` + its `Handle`; subscribe `Svc.Chat.ChatMessage` and `Svc.Framework.Update`. `_primed = false`.
  - Chat handler (`try/catch` → `ReportFailure`): only when `sender.TextValue == "Sonar"`. Find the `MapLinkPayload` in `message.Payloads`; `before` = concatenated `TextPayload.Text` before it; `after` = concatenated `TextPayload.Text` after the next `RawPayload.LinkTerminator`. `SonarChatParser.Parse(before, after)`; resolve world id from the `World` sheet by name (case-insensitive); build a `HuntReport` with `ZoneId = link.TerritoryType.RowId`, `MapId = link.Map.RowId`, `X = link.XCoord`, `Y = link.YCoord`, `Source = Chat`, `Percent = msg.Percent ?? (Fate ? 0 : 100)` only for new entries. `Upsert`; if new and `HuntFilters.Accepts(Settings.Filter, kind)` → `PlaySound()`.
  - Framework update, gated to once per second: `reader.Read(now)`; result non-null → `list.ReplaceMemory(result, now, announce: _primed)`, then `_primed = true`, play sound once if any returned entry passes the filter; result null and status `Failed` → `list.MemoryLost()`. Then live HP: for entries on the player's current world (`Svc.ClientState`/`Svc.Objects.LocalPlayer.CurrentWorld.RowId`), territory and instance, find a `IBattleNpc` in `Svc.Objects` whose `NameId` names the entry in any client language (cache `BNpcName` names per language from `Svc.Data.GetExcelSheet<BNpcName>(lang)`), and `SetLivePercent` with `CurrentHp * 100f / MaxHp`. Then `Expire(now)`, then redraw when changed.
  - Redraw: `Visible(Settings.Filter)` → clamp `_scroll` → `SetRows(rows.Skip(_scroll).Take(Settings.Rows) mapped to HuntRow, _scroll, rows.Count)`. Badge `"S"`, `"A"`, `"F"`; players `"[n]"` or empty; percent `"N%"` (no decimals); zone = `TerritoryType.PlaceName` name + instance char (` + n`) when `InstanceId > 0`; `OtherWorld` = `WorldId != current world`; `Fraction` = `Percent / 100` (dead → 1); `Struck` = dead; `CanTeleport` = !dead.
  - Title: `$"{T("Title")} ({currentWorldName})"`; filter button cycles `Settings.Filter = HuntFilters.Next(...)`, `Changed()`.
  - Scroll, placement (center-anchored offsets), lock, opacity/font/scale and delayed saving: same pattern as `GoblinDamageDone` (`ScrollMain`, `PlacePanel`, `Changed`/`FlushSettings`, `ResetPosition`).
  - `PlaySound()`: when `Settings.SoundAlert`, `UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(Settings.SoundId, 1, 16))`.
  - Map/teleport events call into Task 7 (`HuntTravel`); leave them as no-ops until Task 7.
  - `Disable`: unsubscribe both events, `reader.Reset()`, dispose windows, `OverlayNodes.Dispose()`, flush settings.

- [ ] **Step 1: Implement `GoblinHunts`.**
- [ ] **Step 2: Build** → no errors.
- [ ] **Step 3: Commit** (when authorized): `"Goblin Hunts: tweak, sources and table"`

---

### Task 7: Map and teleport

**Files:**
- Create: `GoblinTweaks/Services/HuntTravel.cs`
- Modify: `GoblinTweaks/Tweaks/GoblinHunts.cs` (wire `OnMap`/`OnTeleport`)

**Interfaces:**
- Consumes: `HuntEntry` (Task 1), `HuntMath.MarkerToMapCoord`, `HuntMath.Nearest`, `AetheryteSpot` (Task 3).
- Produces: `internal static unsafe class HuntTravel`
  - `public static void OpenMap(HuntEntry e)` → `Svc.GameGui.OpenMapWithMapLink(new MapLinkPayload(e.ZoneId, e.MapId, e.X, e.Y))`.
  - `public static TravelResult Teleport(HuntEntry e, uint currentWorldId, uint currentZoneId, uint currentInstance)` with `internal enum TravelResult { Teleported, TeleportedOtherWorld, TeleportedOtherInstance, AlreadyInUldah, NoAttunedAetheryte, Refused }`.
- Rules:
  - Attuned set: `Telepo.Instance()->UpdateAetheryteList()` then the `AetheryteId`s of `TeleportList`.
  - Other world → teleport to aetheryte `9`; if the player is already in Ul'dah's territory, don't teleport (`AlreadyInUldah`). If the world's region differs from the current one (World sheet `DataCenter.Region`), the notice says Data Center Travel is needed.
  - Same world → candidates: territory `399` → `[75, 77]` first attuned; otherwise `MapMarker` rows of the entry's map (`Map.MapMarkerRange`) with `DataType == 3`, `DataKey` = aetheryte id, attuned only, positions through `MarkerToMapCoord(marker.X/Y, map.SizeFactor)`; pick `HuntMath.Nearest`. None → `NoAttunedAetheryte`. Different instance in the same zone → teleport anyway, result `TeleportedOtherInstance`.
  - `Telepo.Instance()->Teleport(id, 0)` false → `Refused`.
- The tweak shows, per result (`Svc.Toasts.ShowNormal` and `Svc.Chat.Print`): other world → `T("Notice.OtherWorld")` formatted with name and world (es: `"{0} está en {1}: usá «Visitar otro mundo» en la aetherita de Ul'dah"`); other region → `T("Notice.OtherRegion")`; other instance → `T("Notice.OtherInstance")` with the instance number; no aetheryte → `T("Notice.NoAetheryte")`; refused → `T("Notice.Refused")`.

- [ ] **Step 1: Implement `HuntTravel` and wire the buttons.**
- [ ] **Step 2: Build** → no errors.
- [ ] **Step 3: Commit** (when authorized): `"Goblin Hunts: map and teleport"`

---

### Task 8: Settings, help, texts, icon, in-game check

**Files:**
- Create: `GoblinTweaks/UI/HuntsSettingsAddon.cs`
- Modify: `GoblinTweaks/Tweaks/GoblinHunts.cs` (settings window, help window, buttons, tooltips, language change)
- Modify: `GoblinTweaks/Localization/en.json`, `GoblinTweaks/Localization/es.json`
- Create: `GoblinTweaks/Assets/IconTweaks/ghuntIcon.png`

**Interfaces:**
- Consumes: `GoblinHunts.Config/Text/Changed/OpenHelp/PlaySound/ResetPosition` (Task 6); `SettingsPanelAddon.Section/Toggle/Number`; `TweakLanguageSelect`; `TextHelpAddon`.
- Produces: `internal sealed class HuntsSettingsAddon : SettingsPanelAddon { public GoblinHunts? Tweak { get; init; } }`, built like `DamageDoneSettingsAddon` (language select in the header, help button bottom left).
- Settings sections and options (spec table): Table → Rows (1–24), Locked; Alert → SoundAlert, SoundId (1–16, plays the sound on change); Appearance → Opacity (0–100 step 5), FontSize (8–24), Scale (50–200 step 5). Window `InternalName = "GtkHunts"`, size `520×480`.
- Tweak buttons: `HelpButton` (opens help), `Buttons` = Settings, Reset position. ⚙ in the table opens settings. `OnLanguageChanged` rebuilds settings and help like GDD.
- Help: `TextHelpAddon` `InternalName = "GtkHuntsHelp"`, size `780×560`, topics `Overview`, `Table`, `Buttons`, `List`, `Sources` with the content listed in the spec's "Ayuda" section.
- Keys under `Tweaks.GoblinHunts.`: `Name`, `Description`, `Command.Help`, `Title`, `Open.Help`, `Button.ResetPosition`, `ResetPosition.Help`, `Help.Title`, `Help.Tooltip`, `Help.<Topic>.Title`/`.Text` ×5, `Section.Table`, `Section.Alert`, `Section.Appearance`, `Setting.<Rows|Locked|SoundAlert|SoundId|Opacity|FontSize|Scale>` and `.Help` for each, `Tooltip.<Filter|Lock|Settings|Close|Map|Teleport>`, `Notice.<OtherWorld|OtherRegion|OtherInstance|NoAetheryte|Refused>`.
- Icon: 128×128 RGBA, same frame as `gddIcon.png` (dark rounded square, thin gold border), flat drawing of a crosshair over a small "S". Draw it with a throwaway script in the session scratchpad (e.g. PowerShell `System.Drawing`) and save only the PNG into the repo. The csproj already embeds `Assets\IconTweaks\*.png`.

- [ ] **Step 1: Implement the settings window, help, buttons and texts (en + es).**
- [ ] **Step 2: Create the icon** and view it next to `gddIcon.png` to check the style matches.
- [ ] **Step 3: Build and run all tests** — `dotnet build -c Release` → no errors; `dotnet test GoblinTweaks.Tests/GoblinTweaks.Tests.csproj` → all PASS.
- [ ] **Step 4: In-game check (the user runs it; report what they see):**
  - Tweak listed under Other with its icon; settings and help open; texts in Spanish and English.
  - With Sonar's chat reports on: a new A/S appears at the top with a sound; "was just killed" turns it red, struck, and it goes after 5 min.
  - Players in brackets and orange/% while someone hits it from afar (memory source); orange also when standing next to it.
  - Filter cycles All → S → A → FATE; the wheel scrolls past "Rows".
  - Map button opens the map with the flag; teleport goes to the nearest aetheryte; a hunt on another world goes to Ul'dah with the notice.
  - Disable Sonar (or unload it) while the tweak runs: no crash, the tweak stays enabled, one warning in `/xllog`.
  - The table stays behind game windows (loot window, tooltips).
- [ ] **Step 5: Commit** (when authorized): `"Goblin Hunts: settings, help, texts and icon"`
