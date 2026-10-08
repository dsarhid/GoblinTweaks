using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Services;
using GoblinTweaks.Tweaks;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native window docked next to the retainer list: one tab per inventory (bags, armoury chest, chocobo
/// and each retainer) listing the sellable stacks with a checkbox. The selection is kept while
/// switching tabs.
/// </summary>
internal sealed unsafe class RetainerSellAddon : NativeAddon
{
    private const float TabBarH    = 28f;
    private const float BottomBarH = 66f;

    public AutoGoblinRetainer? Tweak { get; init; }

    private enum SortKey { Name, Qty, Level, Price }

    private SortKey _sortKey  = SortKey.Name;
    private bool    _sortDesc;
    private readonly Dictionary<SortKey, (TextNode Node, string Label)> _headers = [];

    // Universalis normal price per unit (nq, hq), fetched in the background for the stacks shown.
    private readonly Dictionary<uint, (long Nq, long Hq)> _prices = [];
    private readonly HashSet<uint> _priceRequested = [];
    private readonly Queue<uint> _priceQueue = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<(uint Id, (long Nq, long Hq) Price)> _priceArrived = new();
    private Task? _priceWorker;
    private int _priceGeneration; // bumped when the window closes so a worker still running stops quietly

    private CheckboxNode? _pinchCheck;
    private ColorImageNode? _allFill;
    private List<SellTab> _tabs = [];
    private int           _activeTab;
    private readonly HashSet<string> _selected = [];
    private readonly Dictionary<string, SellableItem> _known = []; // every stack seen, so ticks survive tab switches
    private readonly Dictionary<string, int> _sellQuantity = []; // edited amounts; missing = the whole stack

    private readonly List<TextButtonNode>        _tabButtons = [];
    private ListNode<SellableItem, SellItemRowNode>? _list;
    private TextNode?                            _count;
    private TextButtonNode?                      _sellButton;

    private List<SellableItem> _shown = [];
    private string?            _shownTabId;

    private bool     _pendingReload = true;
    private DateTime _nextRefresh   = DateTime.MinValue;

    private int SellQuantityOf(SellableItem item)
        => Math.Clamp(_sellQuantity.GetValueOrDefault(item.Key, item.Quantity), 1, Math.Max(1, item.Quantity));

    /// <summary>Keys of every ticked stack (across all tabs).</summary>
    public IReadOnlyCollection<string> SelectedKeys => _selected;

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        SellItemRowNode.PriceOf          = PriceOf;
        SellItemRowNode.PriceTooltip     = Tweak?.PriceTooltip ?? string.Empty;
        SellItemRowNode.IsSelected       = key => _selected.Contains(key);
        SellItemRowNode.SellQuantity        = item => SellQuantityOf(item);
        SellItemRowNode.SellQuantityChanged = (key, quantity) => _sellQuantity[key] = quantity;
        SellItemRowNode.SelectionChanged = (key, on) =>
        {
            if (on) _selected.Add(key); else _selected.Remove(key);
            UpdateCount();
        };

        // Items whose price never arrived last time (offline, overloaded) are asked for again.
        _priceRequested.RemoveWhere(id => !_prices.ContainsKey(id));
        _priceQueue.Clear();

        BuildLayout();
        _pendingReload = true;
        DockNextToRetainerList(addon);
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        // Rescan every couple of seconds so freshly opened retainers show up.
        if (!_pendingReload && DateTime.UtcNow >= _nextRefresh) _pendingReload = true;
        if (_pendingReload) Reload();

        _list?.Update();
        PollPrices();

        // The same option can be changed in the settings window; keep the checkbox in step.
        if (Tweak is not null && _pinchCheck is not null && _pinchCheck.IsChecked != Tweak.PinchAfter)
            _pinchCheck.IsChecked = Tweak.PinchAfter;
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _priceGeneration++;
        SellItemRowNode.Live.Clear();
        SellItemRowNode.PriceOf = null;
        _headers.Clear();
        _allFill = null;
        _tabButtons.Clear();
        _tabs = [];
        _shown = [];
        _shownTabId = null;
        _list = null;
        _count = null;
        _sellButton = null;
        SellItemRowNode.IsSelected = null;
        SellItemRowNode.SelectionChanged = null;
        SellItemRowNode.SellQuantity = null;
        SellItemRowNode.SellQuantityChanged = null;
        base.OnFinalize(addon);
    }

    private void Reload()
    {
        _pendingReload = false;
        _nextRefresh   = DateTime.UtcNow.AddSeconds(2);

        try
        {
            var tabs = SellInventory.BuildTabs(source => Tweak?.TabTitle(source) ?? source.ToString());
            if (_tabs.Count != tabs.Count || _tabs.Zip(tabs).Any(p => p.First.Id != p.Second.Id))
            {
                _tabs = tabs;
                if (_activeTab >= _tabs.Count) _activeTab = 0;
                RebuildTabBar();
            }

            if (_list is not null && _activeTab < _tabs.Count)
            {
                var items = SellInventory.Read(_tabs[_activeTab]);
                RequestPrices(items);
                items = Sort(items);

                foreach (var item in items) _known[item.Key] = item;

                // Drop selections whose stack no longer exists in that tab.
                var alive = items.Select(i => i.Key).ToHashSet();
                _selected.RemoveWhere(k => k.StartsWith(_tabs[_activeTab].Id + "|") && !alive.Contains(k));

                // Reassigning the list resets its scroll, so only do it when something changed.
                var tabId = _tabs[_activeTab].Id;
                if (_shownTabId != tabId || !_shown.SequenceEqual(items))
                {
                    _shownTabId       = tabId;
                    _shown            = items;
                    _list.OptionsList = items;
                }
            }

            UpdateCount();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "RetainerSellAddon: reload failed");
        }
    }

    private void RebuildTabBar()
    {
        foreach (var button in _tabButtons) button.Dispose();
        _tabButtons.Clear();
        if (_tabs.Count == 0) return;

        var c     = ContentStartPosition;
        var cs    = ContentSize;
        var gap   = 3f;
        var width = (cs.X - gap * (_tabs.Count - 1)) / _tabs.Count;

        for (var i = 0; i < _tabs.Count; i++)
        {
            var index  = i;
            var button = new TextButtonNode
            {
                String    = _tabs[i].Title,
                Position  = new Vector2(c.X + i * (width + gap), c.Y),
                Size      = new Vector2(width, TabBarH),
                IsChecked = i == _activeTab,
            };
            button.OnClick = () =>
            {
                _activeTab = index;
                for (var j = 0; j < _tabButtons.Count; j++)
                    _tabButtons[j].IsChecked = j == index;
                _pendingReload = true;
            };
            button.AttachNode(this);
            _tabButtons.Add(button);
        }
    }

    private void UpdateCount()
    {
        UpdateAllBox();

        if (_count is null) return;

        _count.String = Tweak?.SelectedText(_selected.Count) ?? _selected.Count.ToString();
    }

    private void BuildLayout()
    {
        var c  = ContentStartPosition;
        var cs = ContentSize;

        // Column headers, aligned with the row columns (rows end before the scrollbar).
        const float scrollGap = 22f;
        var headY = c.Y + TabBarH + 4f;
        // "Select all" box above the rows' boxes: ticks every stack of the current tab, or clears them when all are ticked.
        var allFrame = new ColorImageNode { Position = new Vector2(c.X + 6f, headY), Size = new Vector2(16f, 16f), Color = new Vector4(0.80f, 0.72f, 0.50f, 1f) };
        allFrame.AttachNode(this);
        _allFill = new ColorImageNode { Position = new Vector2(c.X + 8f, headY + 2f), Size = new Vector2(12f, 12f), Color = new Vector4(0.10f, 0.09f, 0.08f, 1f) };
        _allFill.AttachNode(this);
        var allHit = new CollisionNode
        {
            CollisionType = CollisionType.Hit,
            Position      = new Vector2(c.X + 2f, headY - 2f),
            Size          = new Vector2(24f, 20f),
            TextTooltip   = Tweak?.SelectAllTooltip ?? string.Empty,
        };
        allHit.AddEvent(AtkEventType.MouseDown, (_, _, _, _, eventData) =>
        {
            if (eventData is not null && eventData->MouseData.ButtonId == 0) ToggleAll();
        });
        allHit.AttachNode(this);

        // Clicking a header sorts by it; clicking it again reverses the order.
        var right = c.X + cs.X - scrollGap;
        AddSortHeader(SortKey.Name, Tweak?.ColumnText("ColName") ?? "Name", c.X + 4f + 24f + 4f + 26f + 6f, headY, 200f, AlignmentType.Left);
        AddSortHeader(SortKey.Qty, Tweak?.ColumnText("ColQty") ?? "Qty", right - SellItemRowNode.PriceW - SellItemRowNode.LevelW - SellItemRowNode.SellW - SellItemRowNode.QtyW, headY, SellItemRowNode.QtyW, AlignmentType.Right);
        AddHeader(Tweak?.ColumnText("ColSell") ?? "Sell", right - SellItemRowNode.PriceW - SellItemRowNode.LevelW - SellItemRowNode.SellW + 6f, headY, SellItemRowNode.SellW - 6f, AlignmentType.Center);
        AddSortHeader(SortKey.Level, Tweak?.ColumnText("ColLevel") ?? "Item level", right - SellItemRowNode.PriceW - SellItemRowNode.LevelW, headY, SellItemRowNode.LevelW, AlignmentType.Right);
        AddSortHeader(SortKey.Price, Tweak?.ColumnText("ColPrice") ?? "Price", right - SellItemRowNode.PriceW, headY, SellItemRowNode.PriceW - 6f, AlignmentType.Right);
        UpdateHeaders();

        var listY = headY + 20f;
        var listH = cs.Y - (listY - c.Y) - BottomBarH - 4f;
        _list = new ListNode<SellableItem, SellItemRowNode>
        {
            Position                 = new Vector2(c.X, listY),
            Size                     = new Vector2(cs.X, listH),
            ItemSpacing              = 2f,
            AllowMultipleSelection   = false,
            ShowNoResultsPlaceholder = true,
            OptionsList              = [],
        };
        _list.AttachNode(this);

        var barY = c.Y + cs.Y - BottomBarH;
        new HorizontalLineNode { Position = new Vector2(c.X, barY), Size = new Vector2(cs.X, 2f) }.AttachNode(this);

        _count = new TextNode
        {
            Position = new Vector2(c.X + 6f, barY + 7f),
            Size     = new Vector2(cs.X - 48f, 18f),
            FontSize = 13,
        };
        _count.AttachNode(this);

        // Help button, at the right end of the bottom bar.
        var helpButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(c.X + cs.X - 30f, barY + 5f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak?.HelpTooltip ?? string.Empty,
        };
        helpButton.OnClick = () => Tweak?.OpenHelp();
        helpButton.AttachNode(this);

        // Whether to reprice every listing of the visited retainers once the new ones are listed.
        _pinchCheck = new CheckboxNode
        {
            String      = Tweak?.PinchAfterText ?? "Auto-pinch",
            Position    = new Vector2(c.X + 6f, barY + 33f),
            Size        = new Vector2(cs.X - 214f, 24f),
            IsChecked   = Tweak?.PinchAfter ?? true,
            TextTooltip = Tweak?.PinchAfterTooltip ?? string.Empty,
        };
        _pinchCheck.OnClick = isChecked => { if (Tweak is not null) Tweak.PinchAfter = isChecked; };
        _pinchCheck.AttachNode(this);

        _sellButton = new TextButtonNode
        {
            String      = Tweak?.SellButtonText ?? "Sell",
            Position    = new Vector2(c.X + cs.X - 190f, barY + 32f),
            Size        = new Vector2(184f, 28f),
            TextTooltip = Tweak?.SellButtonTooltip ?? string.Empty,
        };
        _sellButton.OnClick = () => Tweak?.SellSelected(_selected
            .Where(k => _known.ContainsKey(k))
            .Select(k => (_known[k], SellQuantityOf(_known[k])))
            .ToList());
        _sellButton.AttachNode(this);
    }

    /// <summary>Ticks every stack of the current tab, or clears them all when they already are.</summary>
    private void ToggleAll()
    {
        if (_shown.Count == 0) return;

        var allOn = _shown.All(i => _selected.Contains(i.Key));
        foreach (var item in _shown)
        {
            if (allOn) _selected.Remove(item.Key);
            else _selected.Add(item.Key);
        }

        SellItemRowNode.RefreshAll();
        UpdateCount();
    }

    private void UpdateAllBox()
    {
        if (_allFill is null) return;

        var allOn = _shown.Count > 0 && _shown.All(i => _selected.Contains(i.Key));
        _allFill.Color = allOn ? new Vector4(0.95f, 0.78f, 0.25f, 1f) : new Vector4(0.10f, 0.09f, 0.08f, 1f);
    }

    private TextNode AddHeader(string text, float x, float y, float width, AlignmentType alignment)
    {
        var node = new TextNode
        {
            String        = text,
            Position      = new Vector2(x, y),
            Size          = new Vector2(width, 16f),
            FontSize      = 12,
            TextColor     = new Vector4(0.85f, 0.73f, 0.49f, 1f),
            AlignmentType = alignment,
        };
        node.AttachNode(this);
        return node;
    }

    private void AddSortHeader(SortKey key, string text, float x, float y, float width, AlignmentType alignment)
    {
        _headers[key] = (AddHeader(text, x, y, width, alignment), text);

        var hit = new CollisionNode
        {
            CollisionType = CollisionType.Hit,
            Position      = new Vector2(x - 2f, y - 2f),
            Size          = new Vector2(width + 4f, 20f),
        };
        hit.AddEvent(AtkEventType.MouseDown, (_, _, _, _, eventData) =>
        {
            if (eventData is not null && eventData->MouseData.ButtonId == 0) SetSort(key);
        });
        hit.AttachNode(this);
    }

    private void SetSort(SortKey key)
    {
        if (_sortKey == key) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = key != SortKey.Name; } // numbers start with the biggest, names A to Z

        UpdateHeaders();
        _pendingReload = true;
    }

    /// <summary>Marks the sorted column with an arrow (^ ascending, v descending) and a brighter color.</summary>
    private void UpdateHeaders()
    {
        foreach (var (key, (node, label)) in _headers)
        {
            var active = key == _sortKey;
            node.String    = active ? $"{label} {(_sortDesc ? "v" : "^")}" : label;
            node.TextColor = active ? new Vector4(1f, 0.95f, 0.75f, 1f) : new Vector4(0.85f, 0.73f, 0.49f, 1f);
        }
    }

    private List<SellableItem> Sort(List<SellableItem> items)
    {
        IOrderedEnumerable<SellableItem> ordered = _sortKey switch
        {
            SortKey.Qty   => _sortDesc ? items.OrderByDescending(i => i.Quantity) : items.OrderBy(i => i.Quantity),
            SortKey.Level => _sortDesc ? items.OrderByDescending(i => i.ItemLevel) : items.OrderBy(i => i.ItemLevel),
            // Unknown prices always go last, whichever way the rest is ordered.
            SortKey.Price => items.OrderBy(i => PriceOf(i) is > 0 ? 0 : 1)
                                  .ThenBy(i => _sortDesc ? -(PriceOf(i) ?? 0) : (PriceOf(i) ?? 0)),
            _             => _sortDesc ? items.OrderByDescending(i => i.Name) : items.OrderBy(i => i.Name),
        };

        return ordered.ThenBy(i => i.Name).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
    }

    // ── Universalis prices ────────────────────────────────────────────────────

    /// <summary>
    /// Average price per unit of the last 30 days of sales for the stack: null while loading, 0 when there were
    /// no sales (shown as "-"). A normal stack uses normal sales and an HQ stack HQ sales.
    /// </summary>
    private long? PriceOf(SellableItem item)
        => _prices.TryGetValue(item.ItemId, out var p) ? (item.Hq ? p.Hq : p.Nq) : null;

    /// <summary>Queues the items shown that have no price yet; a background worker asks Universalis a few at a time.</summary>
    private void RequestPrices(List<SellableItem> items)
    {
        foreach (var id in items.Select(i => i.ItemId).Distinct())
            if (!_prices.ContainsKey(id) && _priceRequested.Add(id))
                _priceQueue.Enqueue(id);

        if (_priceWorker is { IsCompleted: false } || _priceQueue.Count == 0) return;

        var generation = _priceGeneration;
        var batches    = new List<uint[]>();
        while (_priceQueue.Count > 0)
            batches.Add(Enumerable.Range(0, Math.Min(SellPrices.ChunkSize, _priceQueue.Count)).Select(_ => _priceQueue.Dequeue()).ToArray());

        _priceWorker = SellPrices.StreamAsync(batches, () => generation == _priceGeneration, (id, price) => _priceArrived.Enqueue((id, price)));
    }

    /// <summary>Takes the prices that arrived since the last frame and redraws.</summary>
    private void PollPrices()
    {
        var any = false;
        while (_priceArrived.TryDequeue(out var arrived))
        {
            _prices[arrived.Id] = arrived.Price;
            any = true;
        }

        if (!any) return;

        // Only a price sort needs the order redone (which resets the scroll); otherwise just redraw the numbers.
        if (_sortKey == SortKey.Price) _pendingReload = true;
        else SellItemRowNode.RefreshAll();
    }

    /// <summary>Places the window flush against the right edge of the retainer list.</summary>
    private static void DockNextToRetainerList(AtkUnitBase* addon)
    {
        var retainerList = (AtkUnitBase*)Svc.GameGui.GetAddonByName("RetainerList").Address;
        if (retainerList is null || addon is null) return;

        var x = (short)(retainerList->X + (short)retainerList->GetScaledWidth(true));
        addon->SetPosition(x, retainerList->Y);
    }
}
