using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace GoblinTweaks.UI;

/// <summary>
/// While the bags, a retainer's bags or the chocobo saddlebag are open, dims every square of the open page that does not
/// hold one of the items it is given (the ingredients of the recipe selected in Crafting Materials), and pulses the
/// tabs that are not open but do. Like the FC chest highlighter, the outlines are native nodes added to the game's own
/// windows, removed when those close, and it does nothing at all if the layout is not what it expects.
/// </summary>
/// <remarks>
/// It does not rely on the exact names of the game's windows, nor on which page of the containers each one shows (that
/// differs between the normal, large and expanded bags): every loaded window whose name says it is of the bags, of a
/// retainer or of the saddlebag is read as a grid of squares, and what each square holds is read from the icon it is
/// drawing. The tabs are the one thing that needs to know what is on the pages that are not open, and for them the
/// containers are read, only after checking against what is drawn that they agree. What it finds is written once to
/// the Dalamud log, to see how a window was read.
/// </remarks>
internal sealed unsafe class InventoryHighlighter : IDisposable
{
    private enum Group { Player, Retainer, Saddlebag }

    private static readonly Vector4 Gold = new(1f, 0.85f, 0.2f, 1f);

    private static readonly TimeSpan ScanEvery = TimeSpan.FromMilliseconds(250);

    // Names that start like a bag window but are not a grid of the containers.
    private static readonly string[] NotBags = ["Context", "Event", "Menu", "Detail", "Crystal", "Catalog", "Transfer", "Sell", "Task", "List", "Cleanup"];

    /// <summary>Supplies the item ids to mark. Null/empty = nothing to mark.</summary>
    public Func<IReadOnlyCollection<uint>>? Source;

    private readonly Dictionary<string, List<Outline>> _outlines = [];
    private readonly Dictionary<string, List<int>> _pulsing = [];
    private readonly Dictionary<Group, List<bool>> _wanted = [];
    private readonly Dictionary<uint, uint> _icons = [];
    private readonly HashSet<uint> _wantedIcons = [];

    // For the windows whose list of items is not known from the containers (a retainer's, the saddlebag's): what each tab
    // held the last time it was open. A tab is known after it was seen, and forgotten when the window closes.
    private readonly Dictionary<(Group Group, int Tab), int> _visited = [];
    private readonly Dictionary<Group, int> _itemCount = [];
    private readonly Dictionary<Group, List<int>> _ranks = [];

    // What the containers of each group hold, by the icon the items are drawn with: several items can share an icon, and
    // only the quality and the size of the stack tell a square apart (and whether it is the wanted item).
    private readonly Dictionary<Group, Dictionary<uint, List<Candidate>>> _candidates = [];
    private readonly Dictionary<(Group Group, int Tab), int> _tabItems = [];
    private string _wantedSignature = string.Empty;
    private readonly HashSet<string> _logged = [];
    private DateTime _nextScan = DateTime.MinValue;
    private bool _running;

    // The windows we have a say in when they close. Only those: a listener for every window makes Dalamud wrap the closing
    // of the windows of other plugins too (the ones of this one included), which is not ours to touch.
    private readonly HashSet<string> _listening = [];

    public void Start()
    {
        if (_running) return;
        _running = true;
        Svc.Framework.Update += OnUpdate;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        Svc.Framework.Update -= OnUpdate;
        foreach (var name in _listening) Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, name, OnClosing);
        _listening.Clear();
        ClearAll(immediately: true);
    }

    public void Dispose() => Stop();

    // A window is about to be destroyed: our nodes must leave it first.
    private void OnClosing(AddonEvent type, AddonArgs args) => Clear(args.AddonName);

    // Nodes taken off the screen that are disposed a moment later (see ClearAll), with the window they belong to.
    private readonly List<(DateTime Due, string Addon, List<Outline> Nodes)> _retired = [];

    /// <summary>Removes the nodes of a window now. Used when that window is about to be destroyed.</summary>
    private void Clear(string addon)
    {
        _pulsing.Remove(addon);
        if (_outlines.Remove(addon, out var nodes))
            Dispose(nodes);

        // Nodes already taken off the screen of that window go too: they must not outlive it.
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Addon != addon) continue;
            Dispose(_retired[i].Nodes);
            _retired.RemoveAt(i);
        }
    }

    private static void Dispose(List<Outline> nodes)
    {
        foreach (var outline in nodes)
        {
            outline.Fill?.Dispose();
            outline.Border?.Dispose();
        }
    }

    /// <summary>
    /// Takes every outline off the screen. They are not disposed in the same frame: the window of Crafting Materials may be
    /// closing in it, and a window of the game must not have its nodes freed in the middle of that. They are hidden
    /// at once and disposed a couple of seconds later (or when their window closes, whichever comes first).
    /// </summary>
    private void ClearAll(bool immediately = false)
    {
        foreach (var name in _outlines.Keys.ToList())
        {
            if (immediately)
            {
                Clear(name);
                continue;
            }

            _pulsing.Remove(name);
            var nodes = _outlines[name];
            _outlines.Remove(name);
            foreach (var outline in nodes)
            {
                if (outline.Fill is { } fill) fill.IsVisible = false;
                if (outline.Border is { } border) border.IsVisible = false;
            }
            _retired.Add((DateTime.UtcNow.AddSeconds(2), name, nodes));
            Svc.Log.Information($"InventoryHighlighter: {nodes.Count} outlines of {name} taken off the screen; they are disposed in a moment.");
        }

        if (immediately)
        {
            foreach (var (_, _, nodes) in _retired) Dispose(nodes);
            _retired.Clear();
        }
    }

    /// <summary>Disposes the outlines whose time has come.</summary>
    private void DisposeRetired()
    {
        if (_retired.Count == 0) return;

        var now = DateTime.UtcNow;
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Due > now) continue;
            Dispose(_retired[i].Nodes);
            _retired.RemoveAt(i);
        }
    }

    // ── Reading the game ──────────────────────────────────────────────────────

    private void OnUpdate(IFramework framework)
    {
        try
        {
            DisposeRetired();

            var ids = Source?.Invoke();
            if (ids is null || ids.Count == 0)
            {
                ClearAll();
                return;
            }

            var now = DateTime.UtcNow;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanEvery;
                ScanContainers(ids);
                Mark();
            }

            Pulse();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "InventoryHighlighter: update failed");
            Source = null; // stop instead of spamming the log every frame
            ClearAll(immediately: true);
        }
    }

    /// <summary>The icons of the wanted items, and for each group slot by slot from the first slot of its first container: does it hold one.</summary>
    private void ScanContainers(IReadOnlyCollection<uint> ids)
    {
        var wanted = ids as HashSet<uint> ?? [.. ids];

        _wantedIcons.Clear();
        foreach (var id in wanted)
            if (IconOf(id) is var icon and not 0)
                _wantedIcons.Add(icon);

        var signature = string.Join(",", _wantedIcons.Order());
        if (signature != _wantedSignature)
        {
            _wantedSignature = signature;
            _visited.Clear();
            _tabItems.Clear();
        }

        _wanted.Clear();
        _itemCount.Clear();
        _ranks.Clear();
        _candidates.Clear();
        var manager = InventoryManager.Instance();
        if (manager == null) return;

        foreach (var group in new[] { Group.Player, Group.Retainer, Group.Saddlebag })
        {
            var flags = new List<bool>();
            var items = 0;
            var keys  = new List<(long Key, bool Wanted)>();
            var byIcon = new Dictionary<uint, List<Candidate>>();
            var dump  = new System.Text.StringBuilder();
            foreach (var (type, fallback) in ContainersOf(group))
            {
                var container = manager->GetInventoryContainer(type);
                var loaded    = container != null && container->IsLoaded;
                var size      = loaded ? container->Size : fallback;
                dump.Append($" {type}({(loaded ? size : -1)}):");
                for (var i = 0; i < size; i++)
                {
                    var slot = loaded ? container->GetInventorySlot(i) : null;
                    flags.Add(slot != null && slot->ItemId != 0 && wanted.Contains(slot->ItemId));
                    if (slot != null && slot->ItemId != 0)
                    {
                        items++;
                        keys.Add((SortKey(slot->ItemId), wanted.Contains(slot->ItemId)));

                        var icon = IconOf(slot->ItemId);
                        if (!byIcon.TryGetValue(icon, out var same)) byIcon[icon] = same = [];
                        same.Add(new Candidate((slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0, (int)slot->Quantity, wanted.Contains(slot->ItemId), SortKey(slot->ItemId), slot->ItemId));
                    }
                    if (slot != null && slot->ItemId != 0) dump.Append($" {i}={slot->ItemId}{((slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0 ? "hq" : "")}");
                }
            }

            // The bags are not drawn slot by slot as the containers hold them: the game lists the items one after another.
            // That list is what the number array of the inventory holds, in the order it is drawn.
            _candidates[group] = byIcon;

            if (group == Group.Player)
            {
                var array = InventoryNumberArray.Instance();
                if (array != null)
                {
                    var entries = new List<(uint Icon, bool Hq, int Quantity)>();
                    foreach (var item in array->Items)
                        entries.Add((BaseIcon(item.IconId), item.IconId >= 1_000_000 && item.IconId < 2_000_000, item.StackCount));

                    flags.Clear();
                    flags.AddRange(ResolveByOrder(Group.Player, entries));
                }
            }
            else if (group == Group.Retainer)
            {
                // What the retainer's containers hold, to compare with what its window draws (see the log of its grid).
                LogOnce("containers:" + dump, "InventoryHighlighter: retainer containers (slot=item id):" + dump);
            }

            _wanted[group]    = flags;
            _itemCount[group] = items;

            // Where the wanted items fall among all of them in the order the window lists them.
            keys.Sort((a, b) => a.Key.CompareTo(b.Key));
            var ranks = new List<int>();
            for (var i = 0; i < keys.Count; i++)
                if (keys[i].Wanted) ranks.Add(i);
            _ranks[group] = ranks;
        }
    }

    /// <summary>
    /// The order a retainer's (or the saddlebag's) window lists its items in, as a number: by the sort category of the item,
    /// then by the category it is shown under, then by its id. It reproduces which tab holds which item; inside a tab it
    /// is close to the game's order but not the same, and it does not need to be.
    /// </summary>
    private long SortKey(uint itemId)
    {
        var sheet = Svc.Data.GetExcelSheet<LuminaItem>();
        if (!sheet.TryGetRow(itemId, out var row)) return long.MaxValue;

        var param = row.ItemSortCategory.ValueNullable is { } sort ? Convert.ToInt64(sort.Param) : 0L;
        var major = row.ItemUICategory.ValueNullable is { } ui ? Convert.ToInt64(ui.OrderMajor) : 0L;
        var minor = row.ItemUICategory.ValueNullable is { } ui2 ? Convert.ToInt64(ui2.OrderMinor) : 0L;
        return param * 1_000_000_000_000L + major * 10_000_000_000L + minor * 100_000_000L + itemId;
    }

    /// <summary>
    /// The tabs of a window predicted to hold something wanted, from where the wanted items fall in the order of all of
    /// them: the tabs are consecutive stretches of that order. The length of a stretch is known for a tab that was open
    /// and estimated for the rest, so a wanted item near the edge of an estimated stretch marks both tabs. Null when the
    /// prediction does not hold for the open tab (it said another number of wanted items than the tab shows).
    /// </summary>
    private HashSet<int>? ModelTabs(Group group, int selected, int perPage, int tabCount)
    {
        if (!_ranks.TryGetValue(group, out var ranks) || perPage <= 0) return null;

        var total = _itemCount.GetValueOrDefault(group);
        if (total == 0 || !_tabItems.ContainsKey((group, selected))) return null;

        var usable = Math.Max(1, Math.Min(tabCount, (int)Math.Ceiling(total * 1.1 / perPage)));
        var known  = _tabItems.Where(pair => pair.Key.Group == group && pair.Key.Tab < usable).ToList();
        var unknown = usable - known.Count;
        var estimate = unknown <= 0
            ? perPage
            : Math.Clamp((total - known.Sum(pair => pair.Value)) / (double)unknown, perPage * 0.6, perPage);

        // Where each tab starts in the order, and whether that start is only an estimate.
        var starts   = new double[usable + 1];
        var guessed  = new bool[usable + 1];
        for (var t = 0; t < usable; t++)
        {
            var isKnown = _tabItems.TryGetValue((group, t), out var count);
            starts[t + 1]  = starts[t] + (isKnown ? count : estimate);
            guessed[t + 1] = guessed[t] || !isKnown;
        }

        // The open tab says whether the prediction is to be trusted.
        if (selected < usable)
        {
            var inOpen = ranks.Count(r => r >= starts[selected] && r < starts[selected + 1]);
            if (_visited.TryGetValue((group, selected), out var seen) && inOpen != seen) return null;
        }

        var tabs = new HashSet<int>();
        foreach (var rank in ranks)
        {
            for (var t = 0; t < usable; t++)
            {
                var left  = guessed[t] ? 3.0 : 0.0;
                var right = guessed[t + 1] ? 3.0 : 0.0;
                if (rank >= starts[t] - left && rank < starts[t + 1] + right)
                    tabs.Add(t);
            }

            // Past the last stretch it can only be in the last tab.
            if (rank >= starts[usable]) tabs.Add(usable - 1);
        }

        return tabs;
    }

    /// <summary>
    /// Whether a tab that is not open should pulse, for a window whose list of items is not known beforehand. A tab that
    /// was seen says what it held. For one that was not, it is guessed: if some of what is wanted has not been found
    /// in the tabs seen so far, it is in one that was not looked at, and the tabs that can hold items at all (the items
    /// of the containers fill pages of about <paramref name="perPage"/>) are the candidates.
    /// </summary>
    private bool RemainingGuess(Group group, int tab, int selected, int perPage, List<bool> wanted, int tabCount)
    {
        if (_visited.TryGetValue((group, tab), out var seenHere)) return seenHere > 0;

        // The order the window lists the items in is reproduced well enough to say which tab holds a wanted one.
        if (ModelTabs(group, selected, perPage, tabCount) is { } predicted) return predicted.Contains(tab);

        var total = wanted.Count(w => w);
        var found = _visited.Where(pair => pair.Key.Group == group).Sum(pair => pair.Value);
        if (total <= found || perPage <= 0) return false;

        var items = _itemCount.GetValueOrDefault(group);
        var pages = (int)Math.Ceiling(items * 1.1 / perPage);
        return tab < Math.Min(pages, tabCount);
    }

    /// <summary>An item the containers hold: how it is drawn, whether it is wanted, and where it falls in the listing order.</summary>
    private sealed record Candidate(bool Hq, int Quantity, bool Wanted, long Key, uint Id);

    /// <summary>
    /// The items of a group that can be what is drawn with <paramref name="icon"/>: the ones of that quality and stack size,
    /// or, when there are none, the ones of that quality, or all with that icon. In the order the windows list them.
    /// Null when the containers of the group are not known.
    /// </summary>
    private List<Candidate>? CandidatesFor(Group group, uint icon, bool hq, int quantity)
    {
        if (!_candidates.TryGetValue(group, out var byIcon) || !byIcon.TryGetValue(icon, out var all) || all.Count == 0)
            return null;

        var narrowed = all.Where(c => c.Hq == hq && c.Quantity == quantity).ToList();
        if (narrowed.Count == 0) narrowed = [.. all.Where(c => c.Hq == hq)];
        if (narrowed.Count == 0) narrowed = all;

        return [.. narrowed.OrderBy(c => c.Key).ThenBy(c => c.Id)];
    }

    /// <summary>
    /// For each entry drawn (an icon, its quality and the size of its stack, in the order it is listed), whether it is a
    /// wanted item. Different items share icons, so an entry is compared with the items of the containers that look the
    /// same. If every one of them is wanted it is; if none is, it is not. If they are mixed, the entries that look the
    /// same are matched with those items in listing order, which is by category and then by id; and when there are not
    /// as many entries as items (some are in another tab) it cannot be told, and they count as wanted so that nothing
    /// wanted is hidden.
    /// </summary>
    private bool[] ResolveByOrder(Group group, IReadOnlyList<(uint Icon, bool Hq, int Quantity)> entries)
    {
        var result = new bool[entries.Count];
        var places = new Dictionary<(uint Icon, bool Hq, int Quantity), List<int>>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (!_wantedIcons.Contains(entries[i].Icon)) continue;

            var key = (entries[i].Icon, entries[i].Hq, entries[i].Quantity);
            if (!places.TryGetValue(key, out var list)) places[key] = list = [];
            list.Add(i);
        }

        foreach (var (key, indexes) in places)
        {
            var candidates = CandidatesFor(group, key.Icon, key.Hq, key.Quantity);
            bool[] answer;
            if (candidates is null || candidates.All(c => c.Wanted)) answer = [.. indexes.Select(_ => true)];
            else if (!candidates.Any(c => c.Wanted))                 answer = [.. indexes.Select(_ => false)];
            else if (candidates.Count == indexes.Count)              answer = [.. candidates.Select(c => c.Wanted)];
            else                                                     answer = [.. indexes.Select(_ => true)];

            for (var j = 0; j < indexes.Count; j++)
                result[indexes[j]] = answer[j];
        }

        return result;
    }

    /// <summary>The icon an item is drawn with: a high-quality item is drawn with its icon + 1,000,000, a collectable + 500,000.</summary>
    private static uint BaseIcon(uint id)
    {
        if (id == uint.MaxValue) return 0;
        if (id >= 1_000_000) return id - 1_000_000;
        return id >= 500_000 ? id - 500_000 : id;
    }

    /// <summary>The id of the icon an item is drawn with; 0 when it has none.</summary>
    private uint IconOf(uint itemId)
    {
        if (_icons.TryGetValue(itemId, out var icon)) return icon;

        var sheet = Svc.Data.GetExcelSheet<LuminaItem>();
        icon = sheet.TryGetRow(itemId, out var row) ? row.Icon : 0u;
        return _icons[itemId] = icon;
    }

    /// <summary>The containers of a group in the order the game lays them out, with the size to assume when one is not loaded.</summary>
    private static IEnumerable<(InventoryType Type, int Size)> ContainersOf(Group group)
    {
        switch (group)
        {
            case Group.Player:
                yield return (InventoryType.Inventory1, 35);
                yield return (InventoryType.Inventory2, 35);
                yield return (InventoryType.Inventory3, 35);
                yield return (InventoryType.Inventory4, 35);
                break;
            case Group.Retainer:
                for (var page = 0; page < 7; page++)
                    yield return ((InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page), 25);
                break;
            default:
                yield return (InventoryType.SaddleBag1, 35);
                yield return (InventoryType.SaddleBag2, 35);
                yield return (InventoryType.PremiumSaddleBag1, 35);
                yield return (InventoryType.PremiumSaddleBag2, 35);
                break;
        }
    }

    private static Group? GroupOf(string name)
    {
        if (NotBags.Any(word => name.Contains(word, StringComparison.Ordinal))) return null;
        if (name.StartsWith("InventoryRetainer", StringComparison.Ordinal) || name.StartsWith("RetainerGrid", StringComparison.Ordinal))
            return Group.Retainer;
        if (name.StartsWith("InventoryBuddy", StringComparison.Ordinal))
            return Group.Saddlebag;
        if (name.StartsWith("Inventory", StringComparison.Ordinal))
            return Group.Player;
        return null;
    }

    private readonly record struct Box(float X, float Y, float W, float H, bool Selected);

    /// <summary>One square of a grid: where it is and the icon it draws (0 when it draws none, or none that could be read).</summary>
    private readonly record struct Slot(Box Box, uint[] Icons, bool Hq = false, int Quantity = 1);

    private sealed class View
    {
        public required string Name { get; init; }
        public required nint Address { get; init; }
        public List<Slot> Slots { get; } = [];
        public List<Box> Tabs { get; } = [];
        public Dictionary<ComponentType, int> Kinds { get; } = [];
    }

    private void Mark()
    {
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return;

        var list  = &stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        var views = new Dictionary<Group, List<View>>();
        var seen  = new HashSet<string>();
        for (var i = 0; i < list->Count; i++)
        {
            var addon = list->Entries[i].Value;
            if (addon == null || !addon->IsVisible || addon->UldManager.NodeList == null) continue;

            var name = addon->NameString;
            if (GroupOf(name) is not { } group) continue;

            var view = new View { Name = name, Address = (nint)addon };
            Collect(&addon->UldManager, view, 0);
            Normalize(view);
            if (view.Slots.Count == 0 && view.Tabs.Count == 0)
            {
                LogOnce($"{name}:none", $"InventoryHighlighter: {group} {name}: no slots found; components seen: {Describe(view.Kinds)}.");
                continue;
            }

            seen.Add(name);
            if (!views.TryGetValue(group, out var of)) views[group] = of = [];
            of.Add(view);
        }

        // Windows that were marked and are not there any more.
        foreach (var name in _outlines.Keys.Where(name => !seen.Contains(name)).ToList()) Clear(name);

        // A window that is closed forgets what its tabs held: it may be another retainer next time.
        foreach (var key in _visited.Keys.Where(key => !views.ContainsKey(key.Group)).ToList())
            _visited.Remove(key);
        foreach (var key in _tabItems.Keys.Where(key => !views.ContainsKey(key.Group)).ToList())
            _tabItems.Remove(key);

        foreach (var (group, of) in views)
        {
            if (_wanted.TryGetValue(group, out var wanted))
                Mark(group, of, wanted);
            else
                foreach (var view in of) Clear(view.Name);
        }
    }

    private void Mark(Group group, List<View> views, List<bool> wanted)
    {
        var grids    = views.Where(v => v.Slots.Count > 0).OrderBy(v => PageOf(v.Name) ?? int.MaxValue).ThenBy(v => v.Slots[0].Box.Y).ThenBy(v => v.Slots[0].Box.X).ToList();
        var tabs     = views.OrderByDescending(v => v.Tabs.Count).FirstOrDefault(v => v.Tabs.Count >= 2);
        var selected = tabs is null ? 0 : Math.Max(0, tabs.Tabs.FindIndex(tab => tab.Selected));

        // Like the FC chest: nothing is dimmed unless something wanted is in these containers at all.
        var any = wanted.Contains(true);

        // What the open page is drawing, by icon. The tabs are only trusted when the containers say the same about it.
        // The grids of the open tab are read as one listing, in the order of their names, so that squares that look the
        // same can be told apart by the order they come in.
        var entries = new List<(uint Icon, bool Hq, int Quantity)>();
        foreach (var grid in grids)
            foreach (var slot in grid.Slots)
                entries.Add((slot.Icons.FirstOrDefault(_wantedIcons.Contains) is var wantedIcon and not 0 ? wantedIcon : slot.Icons.FirstOrDefault(), slot.Hq, slot.Quantity));

        var resolved = ResolveByOrder(group, entries);
        var drawn    = new Dictionary<View, bool[]>();
        var at       = 0;
        foreach (var grid in grids)
        {
            drawn[grid] = [.. resolved.Skip(at).Take(grid.Slots.Count)];
            at += grid.Slots.Count;
        }

        if (group != Group.Player && tabs is not null)
        {
            _visited[(group, selected)]  = drawn.Values.Sum(flags => flags.Count(f => f));
            _tabItems[(group, selected)] = grids.Sum(grid => grid.Slots.Count(s => s.Icons.Length > 0));
        }

        // The grids of an open tab are laid end to end in the order of their names, and the tabs follow one another: tab N
        // is the next stretch as long as everything the open one draws. The tabs are only trusted when what the open tab
        // is drawing is what the list says it should be.
        var perPage = grids.Sum(g => g.Slots.Count);
        var agrees  = perPage > 0;
        var offset  = 0;
        foreach (var grid in grids)
        {
            for (var i = 0; agrees && i < grid.Slots.Count; i++)
            {
                var index    = selected * perPage + offset + i;
                var expected = index < wanted.Count && wanted[index];
                agrees = expected == drawn[grid][i];
            }
            offset += grid.Slots.Count;
        }

        foreach (var grid in grids)
        {
            var readable = grid.Slots.Count(s => s.Icons.Length > 0);
            var count    = drawn[grid].Count(d => d);
            LogOnce($"{grid.Name}:{grid.Slots.Count}:{tabs?.Tabs.Count ?? 0}:{selected}:{any}:{agrees}:{count}:{readable}",
                $"InventoryHighlighter: {group} {grid.Name}: {grid.Slots.Count} slots ({readable} with a readable icon, {count} wanted), "
                + $"{tabs?.Tabs.Count ?? 0} tabs (from {tabs?.Name ?? "-"}, open {selected}); wanted icons {_wantedIcons.Count}; wanted in containers: {any}; "
                + $"containers agree with what is drawn: {agrees}; wanted icons [{string.Join(",", _wantedIcons)}]; "
                + $"icons by square: {string.Join(" ", grid.Slots.Select(s => "[" + string.Join(",", s.Icons) + "]"))}.");
        }

        foreach (var view in views)
        {
            var targets = new List<Target>();

            // Every square of the open page that does not hold an ingredient goes dim, so the ones that do stay as they are.
            // Unless nothing could be read from the icons: then it cannot be told which are which, and nothing is dimmed.
            if (any && drawn.TryGetValue(view, out var flags) && view.Slots.Any(s => s.Icons.Length > 0))
            {
                for (var i = 0; i < view.Slots.Count; i++)
                    if (!flags[i])
                        targets.Add(new Target(view.Slots[i].Box, Dim: true));
            }

            // A tab that is not open but whose pages hold something wanted pulses. For the bags the list of items is
            // known (and checked against what is drawn); for the others, a tab is known once it has been open.
            if (any && ReferenceEquals(view, tabs) && (group != Group.Player || agrees))
            {
                for (var t = 0; t < view.Tabs.Count; t++)
                {
                    if (t == selected) continue;

                    var has = false;
                    if (group == Group.Player)
                    {
                        var from = t * perPage;
                        for (var i = from; i < from + perPage && i < wanted.Count && !has; i++)
                            has = wanted[i];
                    }
                    else
                    {
                        has = RemainingGuess(group, t, selected, perPage, wanted, view.Tabs.Count);
                    }

                    if (has) targets.Add(new Target(view.Tabs[t], Dim: false));
                }
            }

            if (ReferenceEquals(view, tabs))
                LogOnce($"tabs:{view.Name}:{selected}:{any}:{agrees}:{targets.Count(t => !t.Dim)}:{string.Join(",", _visited.Where(p => p.Key.Group == group).Select(p => p.Key.Tab + "=" + p.Value))}:{wanted.Count(w => w)}",
                    $"InventoryHighlighter: {group} tabs of {view.Name}: open {selected}, wanted in containers {any}, bag list agrees {agrees}, seen {string.Join(",", _visited.Where(p => p.Key.Group == group).Select(p => (p.Key.Tab + 1) + "=" + p.Value))}, tabs to pulse {targets.Count(t => !t.Dim)}; tabs at {string.Join(" ", view.Tabs.Select(t => $"({t.X:0},{t.Y:0},{t.W:0}x{t.H:0})"))}.");

            Place(view, targets);
        }
    }

    /// <summary>The number in the name of a grid ("InventoryGrid1", "InventoryGrid1E"); null when it has none.</summary>
    private static int? PageOf(string name)
    {
        var end = name.Length;
        if (end > 0 && name[end - 1] == 'E') end--;

        var start = end;
        while (start > 0 && char.IsDigit(name[start - 1])) start--;
        return start < end && int.TryParse(name[start..end], out var page) ? page : null;
    }

    private void LogOnce(string key, string message)
    {
        if (_logged.Add(key)) Svc.Log.Information(message);
    }

    private static string Describe(Dictionary<ComponentType, int> kinds)
        => kinds.Count == 0 ? "-" : string.Join(", ", kinds.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} x{pair.Value}"));

    // ── Drawing ───────────────────────────────────────────────────────────────

    /// <summary>What to draw over one box: a dim fill over a square, or a pulsing border around a tab.</summary>
    private readonly record struct Target(Box Box, bool Dim);

    private sealed record Outline(ColorImageNode? Fill, BorderNineGridNode? Border, bool Tint = false);

    /// <summary>A border needs room to be drawn (its corners are as big as a small tab): a small tab is tinted instead.</summary>
    private static bool IsTint(Target target) => !target.Dim && target.Box.W < 60f;

    private void Place(View view, List<Target> targets)
    {
        if (targets.Count == 0)
        {
            Clear(view.Name);
            return;
        }

        var addon = (AtkUnitBase*)view.Address;
        // Before a node goes into a window, we must be told when it is going to close.
        if (_listening.Add(view.Name))
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, view.Name, OnClosing);

        if (!_outlines.TryGetValue(view.Name, out var nodes))
            _outlines[view.Name] = nodes = [];

        // Same kind and count as last time: only move them. Otherwise start over.
        var same = nodes.Count == targets.Count;
        for (var i = 0; same && i < targets.Count; i++)
            same = nodes[i].Tint == IsTint(targets[i]) && (nodes[i].Fill is not null) == (targets[i].Dim || IsTint(targets[i]));
        if (!same)
        {
            foreach (var outline in nodes)
            {
                outline.Fill?.Dispose();
                outline.Border?.Dispose();
            }
            nodes.Clear();

            foreach (var target in targets)
            {
                if (target.Dim)
                {
                    var fill = new ColorImageNode { Color = new Vector4(0f, 0f, 0f, 0.62f) };
                    fill.AttachNode(addon);
                    nodes.Add(new Outline(fill, null));
                }
                else if (IsTint(target))
                {
                    var tint = new ColorImageNode { Color = Gold with { W = 0.4f } };
                    tint.AttachNode(addon);
                    nodes.Add(new Outline(tint, null, Tint: true));
                }
                else
                {
                    var border = new BorderNineGridNode { Color = Gold };
                    border.AttachNode(addon);
                    nodes.Add(new Outline(null, border));
                }
            }
        }

        // Relative to the root of the window the nodes belong to, so they follow it when it is dragged.
        var scale = addon->Scale <= 0f ? 1f : addon->Scale;
        var rootX = addon->RootNode != null ? addon->RootNode->ScreenX : addon->X;
        var rootY = addon->RootNode != null ? addon->RootNode->ScreenY : addon->Y;

        if (targets.Any(t => !t.Dim))
        {
            var tab = targets.First(t => !t.Dim).Box;
            LogOnce($"place:{view.Name}:{tab.X:0}:{tab.Y:0}",
                $"InventoryHighlighter: tab outline in {view.Name}: tab at ({tab.X:0},{tab.Y:0}) {tab.W:0}x{tab.H:0}; window at ({addon->X},{addon->Y}), root at ({rootX:0},{rootY:0}), scale {scale}, so the node goes to ({(tab.X - rootX) / scale:0},{(tab.Y - rootY) / scale:0}).");
        }

        var pulsing = new List<int>();
        for (var i = 0; i < targets.Count; i++)
        {
            var box      = targets[i].Box;
            var position = new Vector2((box.X - rootX) / scale, (box.Y - rootY) / scale);
            var size     = new Vector2(box.W, box.H);

            if (nodes[i].Fill is { } fill2)
            {
                fill2.Position = position;
                fill2.Size     = size;
                if (nodes[i].Tint) pulsing.Add(i);
            }
            if (nodes[i].Border is { } border2)
            {
                border2.Position = position;
                border2.Size     = size;
                pulsing.Add(i);
            }
        }

        _pulsing[view.Name] = pulsing;
    }

    /// <summary>The outlines of the tabs fade in and out; this runs every frame.</summary>
    private void Pulse()
    {
        var alpha = 0.6f + 0.4f * MathF.Sin(Environment.TickCount64 / 1000f * 4f);
        foreach (var (name, indexes) in _pulsing)
        {
            if (!_outlines.TryGetValue(name, out var nodes)) continue;
            foreach (var index in indexes)
            {
                if (index >= nodes.Count) continue;
                if (nodes[index].Border is { } border) border.Color = Gold with { W = alpha };
                else if (nodes[index].Tint && nodes[index].Fill is { } tint) tint.Color = Gold with { W = alpha * 0.6f };
            }
        }
    }

    // ── Walking the nodes ─────────────────────────────────────────────────────

    private static void Collect(AtkUldManager* uld, View view, int depth)
    {
        if (depth > 8 || uld == null || uld->NodeList == null) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || (ushort)node->Type < 1000) continue; // component nodes only

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || !node->IsVisible()) continue;

            var box  = new Box(node->ScreenX, node->ScreenY, node->Width * node->ScaleX, node->Height * node->ScaleY, false);
            var type = component->GetComponentType();
            view.Kinds[type] = view.Kinds.GetValueOrDefault(type) + 1;

            if (type == ComponentType.DragDrop)
                view.Slots.Add(ReadSlot(&component->UldManager, box));
            else if (type == ComponentType.RadioButton)
                view.Tabs.Add(box with { Selected = ((AtkComponentRadioButton*)component)->IsSelected });
            else
                Collect(&component->UldManager, view, depth + 1);
        }
    }

    /// <summary>What a square is drawing: the icon of its item (high quality or not) and the number of the stack.</summary>
    private static Slot ReadSlot(AtkUldManager* uld, Box box)
    {
        var icons = new List<uint>();
        var hq    = false;
        var count = 1;
        ReadSlot(uld, 0, icons, ref hq, ref count);
        return new Slot(box, [.. icons.Distinct()], hq, count);
    }

    private static void ReadSlot(AtkUldManager* uld, int depth, List<uint> icons, ref bool hq, ref int count)
    {
        if (depth > 3 || uld == null || uld->NodeList == null) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || !node->IsVisible()) continue;

            if (node->Type == NodeType.Image)
            {
                var image = (AtkImageNode*)node;
                var parts = image->PartsList;
                if (parts != null && parts->Parts != null && image->PartId < parts->PartCount)
                {
                    var asset = parts->Parts[image->PartId].UldAsset;
                    if (asset != null && asset->AtkTexture.Resource != null)
                    {
                        // 0xFFFFFFFF is no icon; a high-quality item is drawn with its icon + 1,000,000, a collectable + 500,000.
                        var id = asset->AtkTexture.Resource->IconId;
                        if (id != 0 && id != uint.MaxValue)
                        {
                            if (id >= 1_000_000 && id < 2_000_000) hq = true;
                            icons.Add(BaseIcon(id));
                        }
                    }
                }
            }
            else if (node->Type == NodeType.Text)
            {
                // The number of a stack is only drawn when there is more than one.
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (int.TryParse(text.Replace(",", "").Replace(".", "").Trim(), out var number) && number > 1)
                    count = number;
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null) ReadSlot(&component->UldManager, depth + 1, icons, ref hq, ref count);
            }
        }
    }

    /// <summary>
    /// Keeps the slots of the commonest size (the crystals and other odd ones go), in reading order; tabs too, in rows
    /// tolerant to the open tab being drawn a little higher.
    /// </summary>
    private static void Normalize(View view)
    {
        var size = view.Slots.GroupBy(s => (MathF.Round(s.Box.W), MathF.Round(s.Box.H))).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        if (size is { } keep)
            view.Slots.RemoveAll(s => (MathF.Round(s.Box.W), MathF.Round(s.Box.H)) != keep);

        view.Slots.Sort((a, b) => Reading(a.Box, b.Box));
        view.Tabs.Sort((a, b) => MathF.Round(a.Y / 12f) != MathF.Round(b.Y / 12f) ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
    }

    private static int Reading(Box a, Box b)
        => MathF.Abs(a.Y - b.Y) > 4f ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
}
