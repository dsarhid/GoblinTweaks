using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.Core;

/// <summary>How the auto-pinch prices an item. Filled from the tweak's settings.</summary>
internal sealed record PinchRules(int UndercutAmount, int MaxUndercutPercent, int MinPrice, int SearchDelayMs);

internal enum PinchOutcome { Repriced, Unchanged, Skipped }

/// <summary>
/// Reprices every listing of the retainer whose "Items for sale" window is open, one at a time:
/// open the item's price window, ask the Market Board for the current listings, set the price just below the
/// cheapest other seller and confirm. Driven from the framework update; every step waits for its window and
/// gives up (stopping the whole run) if the game does not answer in time.
/// </summary>
internal sealed unsafe class AutoPincher
{
    private enum Step { Idle, WaitData, OpenMenu, WaitMenu, WaitPriceWindow, Compare, WaitResults, WaitPriceClosed }

    private const string SellListAddon = "RetainerSellList";
    private const string MenuAddon     = "ContextMenu";
    private const string PriceAddon    = "RetainerSell";
    private const string ResultsAddon  = "ItemSearchResult";

    private static readonly TimeSpan MenuTimeout    = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ResultsTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StepGap        = TimeSpan.FromMilliseconds(250);

    private readonly Func<PinchRules> _rules;
    private readonly Action<string>   _say;

    private Step                _step = Step.Idle;
    private DateTime            _deadline;
    private DateTime            _notBefore;
    private DateTime            _nextSearch;
    private List<(uint ItemId, bool Hq)> _items = [];
    private int                 _index;
    private int                 _currentPrice;
    private int                 _expected;          // listings the retainer reports, to know when its list has loaded
    private uint                _expectedId;        // item of the open price window
    private bool                _expectedHq;
    private readonly SearchWatch _watch = new();
    private int                 _repriced, _unchanged, _skipped;

    // Callback values the game's windows expect (see the comments at each use).
    private const int SellListOpenMenu = 0;
    private const int PriceConfirm     = 0;
    private const int PriceCancel      = 1;
    private const int PriceCompare     = 4;

    public AutoPincher(Func<PinchRules> rules, Action<string> say)
    {
        _rules = rules;
        _say   = say;
    }

    public bool IsRunning => _step != Step.Idle;

    /// <summary>Raised when a run ends, whether it finished or was stopped.</summary>
    public event Action? Ended;

    /// <summary>True when the last run went through every item (not stopped or timed out).</summary>
    public bool LastRunCompleted { get; private set; }

    /// <summary>Counts of the current or last run.</summary>
    public int Repriced => _repriced;
    public int Unchanged => _unchanged;
    public int Skipped => _skipped;

    /// <summary>Name shown in the end-of-run message (the retainer being worked on during a tour); null for none.</summary>
    public string? Label { get; set; }

    /// <summary>Starts repricing the open retainer's listings. Returns false (and says why) if it cannot start.</summary>
    public bool Start()
    {
        if (IsRunning) return false;

        if (!Ready(SellListAddon, out _))
        {
            _say("noSellList");
            return false;
        }

        // How many listings the open retainer has; its list fills in a moment after the window opens.
        var retMgr = RetainerManager.Instance();
        var active = retMgr == null ? null : retMgr->GetActiveRetainer();
        _expected  = active == null ? 0 : active->MarketItemCount;
        if (_expected == 0)
        {
            _say("noItems");
            return false;
        }

        _items = [];
        _index = 0;
        LastRunCompleted = false;
        _repriced = _unchanged = _skipped = 0;
        Enter(Step.WaitData, TimeSpan.FromSeconds(8));
        return true;
    }

    public void Stop(string reason)
    {
        if (!IsRunning) return;

        // Leave the game's windows as we found them.
        if (Ready(ResultsAddon, out var results)) results->Close(true);
        if (Ready(PriceAddon, out var price)) Fire(price, PriceCancel);

        _step = Step.Idle;
        LastRunCompleted = false;
        _say($"{reason}|{_repriced}|{_unchanged}|{_skipped}|{Label}");
        Ended?.Invoke();
    }

    /// <summary>Call every framework update.</summary>
    public void Update()
    {
        if (!IsRunning || DateTime.UtcNow < _notBefore) return;

        if (!Ready(SellListAddon, out var sellList))
        {
            Stop("closed");
            return;
        }

        if (DateTime.UtcNow > _deadline)
        {
            Stop($"timeout:{_step} {Diagnose()}");
            return;
        }

        switch (_step)
        {
            case Step.WaitData:
                // Wait until the game has loaded the listings of this retainer (it may still hold the last one's).
                if (CountListings() < _expected) return;
                _items = new List<(uint, bool)>(new (uint, bool)[_expected]);
                Enter(Step.OpenMenu, MenuTimeout);
                break;

            case Step.OpenMenu:
                if (_index >= _items.Count) { Finish(); return; }
                // Right-click on row _index of the sell list: values are (action, row, button).
                Fire(sellList, SellListOpenMenu, _index, 1);
                Enter(Step.WaitMenu, MenuTimeout);
                break;

            case Step.WaitMenu:
                if (!Ready(MenuAddon, out var menu)) return;
                // First entry of the item menu is "Adjust Price".
                Fire(menu, 0, 0, 0u, null, null); // as the game sends it: (0, entry, 0, undefined, undefined)
                Enter(Step.WaitPriceWindow, MenuTimeout);
                break;

            case Step.WaitPriceWindow:
                if (!Ready(PriceAddon, out var price)) return;
                _currentPrice = ((AddonRetainerSell*)price)->AskingPrice->Value;
                ReadItem((AddonRetainerSell*)price, out _expectedId, out _expectedHq);
                Enter(Step.Compare, MenuTimeout);
                break;

            case Step.Compare:
                if (DateTime.UtcNow < _nextSearch) return; // the Market Board throttles quick repeated searches
                if (!Ready(PriceAddon, out price)) { Stop("closed"); return; }
                var before = InfoProxyItemSearch.Instance();
                _watch.Reset(before == null ? 0 : before->SearchItemId);
                Fire(price, PriceCompare);
                _nextSearch = DateTime.UtcNow.AddMilliseconds(_rules().SearchDelayMs);
                Enter(Step.WaitResults, ResultsTimeout);
                break;

            case Step.WaitResults:
                if (!Ready(ResultsAddon, out var results)) return;
                var proxy = InfoProxyItemSearch.Instance();
                if (proxy == null || !_watch.IsFresh(proxy, 0)) return;

                // The item (and its HQ flag) come from the price window itself, not from a guess of the row order.
                // What the game searched is the item; HQ comes from the window (id offset or the HQ symbol in the name).
                var itemId = proxy->SearchItemId;
                if (!Ready(PriceAddon, out price)) { Stop("closed"); return; }
                var hq = _expectedHq || NameHasHq((AddonRetainerSell*)price);

                var rules   = _rules();
                var outcome = Decide(proxy, itemId, hq, _currentPrice, rules, out var newPrice);
                results->Close(true);

                if (!Ready(PriceAddon, out price)) { Stop("closed"); return; }
                if (outcome == PinchOutcome.Repriced)
                {
                    ((AddonRetainerSell*)price)->AskingPrice->SetValue(newPrice);
                    Fire(price, PriceConfirm);
                    _repriced++;
                }
                else
                {
                    Fire(price, PriceCancel);
                    if (outcome == PinchOutcome.Unchanged) _unchanged++; else _skipped++;
                }

                Enter(Step.WaitPriceClosed, MenuTimeout);
                break;

            case Step.WaitPriceClosed:
                if (Ready(PriceAddon, out _)) return;
                _index++;
                Enter(Step.OpenMenu, MenuTimeout);
                break;
        }
    }

    private static bool NameHasHq(AddonRetainerSell* price)
    {
        var name = price->ItemName == null ? string.Empty : price->ItemName->NodeText.ToString();
        return name.Contains(''); // the HQ symbol in the game font
    }

    /// <summary>Number of stacks the retainer's market container holds right now.</summary>
    internal static int CountListings()
    {
        var mgr       = InventoryManager.Instance();
        var container = mgr == null ? null : mgr->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null) return 0;

        var count = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot != null && slot->ItemId != 0) count++;
        }

        return count;
    }

    /// <summary>The item id of an open price window; HQ items carry an offset of 1,000,000.</summary>
    internal static void ReadItem(AddonRetainerSell* window, out uint itemId, out bool hq)
    {
        itemId = 0;
        hq     = false;
        if (window->AtkUnitBase.AtkValuesCount == 0) return;

        var raw = window->AtkUnitBase.AtkValues[0];
        var id  = raw.Type == AtkValueType.Int ? (uint)raw.Int : raw.UInt;
        hq      = id >= 1_000_000;
        itemId  = hq ? id - 1_000_000 : id;
    }

    /// <summary>Extra detail for timeout messages, to see what the game was doing.</summary>
    private string Diagnose()
    {
        var proxy = InfoProxyItemSearch.Instance();
        var info  = $"results={(Ready(ResultsAddon, out _) ? "open" : "closed")} price={(Ready(PriceAddon, out _) ? "open" : "closed")}";
        if (proxy != null)
            info += $" search={proxy->SearchItemId} waiting={proxy->WaitingForListings} listings={proxy->ListingCount} watch=[{_watch.Describe()}] price={_currentPrice} hq={_expectedHq}";
        return $"({info})";
    }

    private void Finish()
    {
        _step = Step.Idle;
        LastRunCompleted = true;
        _say($"done|{_repriced}|{_unchanged}|{_skipped}|{Label}");
        Ended?.Invoke();
    }

    // ── Pricing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Cheapest current listing of the item in the Market Board search, and whether it is one of ours.
    /// An HQ item competes with HQ listings only; a normal one with everything. Null when nobody sells it.
    /// </summary>
    internal static uint? LowestListing(InfoProxyItemSearch* proxy, uint itemId, bool hq, out bool lowestIsOwn)
    {
        var own = new HashSet<ulong>();
        for (var i = 0; i < proxy->PlayerRetainerCount && i < proxy->PlayerRetainers.Length; i++)
            own.Add(proxy->PlayerRetainers[i].RetainerId);

        var retMgr = RetainerManager.Instance();
        if (retMgr != null)
            for (uint i = 0; i < retMgr->GetRetainerCount(); i++)
            {
                var ret = retMgr->GetRetainerBySortedIndex(i);
                if (ret != null) own.Add(ret->RetainerId);
            }

        uint lowest = uint.MaxValue;
        lowestIsOwn = false;
        var  count = Math.Min((int)proxy->ListingCount, proxy->Listings.Length);
        for (var i = 0; i < count; i++)
        {
            ref var listing = ref proxy->Listings[i];
            if (listing.ItemId != itemId || listing.UnitPrice == 0) continue;
            if (hq && !listing.IsHqItem) continue;

            var isOwn = own.Contains(listing.RetainerId);
            if (listing.UnitPrice < lowest || (listing.UnitPrice == lowest && lowestIsOwn && !isOwn))
            {
                lowest      = listing.UnitPrice;
                lowestIsOwn = isOwn;
            }
        }

        return lowest == uint.MaxValue ? null : lowest;
    }

    /// <summary>
    /// The new price is the cheapest listing that is not ours minus the undercut. Nothing changes when our own
    /// listing already is the cheapest, nobody else sells it, or the cut would be bigger than the allowed maximum.
    /// </summary>
    private static PinchOutcome Decide(InfoProxyItemSearch* proxy, uint itemId, bool hq, int currentPrice, PinchRules rules, out int newPrice)
    {
        newPrice = currentPrice;

        var reference = LowestListing(proxy, itemId, hq, out var lowestIsOwn);
        var lowest    = reference ?? uint.MaxValue;

        if (lowest == uint.MaxValue) return PinchOutcome.Skipped;      // nobody sells it: no reference price
        if (lowestIsOwn) return PinchOutcome.Unchanged;                // we are already the cheapest

        var target = (int)Math.Min(int.MaxValue, lowest) - Math.Max(0, rules.UndercutAmount);
        target = Math.Max(target, Math.Max(1, rules.MinPrice));
        if (target == currentPrice) return PinchOutcome.Unchanged;

        // Guard against bait listings: never cut deeper than allowed in a single pass.
        if (currentPrice > 0 && rules.MaxUndercutPercent < 100 && target < currentPrice * (100 - rules.MaxUndercutPercent) / 100)
            return PinchOutcome.Skipped;

        newPrice = target;
        return PinchOutcome.Repriced;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Enter(Step step, TimeSpan timeout)
    {
        _step      = step;
        _deadline  = DateTime.UtcNow + timeout;
        _notBefore = DateTime.UtcNow + StepGap;
    }

    private static bool Ready(string name, out AtkUnitBase* addon) => AddonCallback.Ready(name, out addon);

    private static void Fire(AtkUnitBase* addon, params object?[] values) => AddonCallback.Fire(addon, values);
}

/// <summary>
/// Decides when the Market Board search on screen is the one we asked for. The results window opens before the
/// listings arrive, the previous item's listings are still in memory, and the game leaves its "waiting" flag on
/// even after the listings are in, so none of those alone is reliable. The results are taken as ready when the
/// listing count stopped changing for a moment (after the request really started), or when the request ended.
/// </summary>
internal sealed unsafe class SearchWatch
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan Grace  = TimeSpan.FromSeconds(2.5);

    private DateTime _readyAt;
    private DateTime _countChangedAt;
    private uint     _startId;
    private uint     _lastCount;
    private bool     _sawWaiting;

    /// <summary>Call right before the search is requested; <paramref name="currentSearchId"/> is the item left over from before.</summary>
    public void Reset(uint currentSearchId)
    {
        _readyAt        = DateTime.MinValue;
        _countChangedAt = DateTime.MinValue;
        _startId        = currentSearchId;
        _lastCount      = uint.MaxValue;
        _sawWaiting     = false;
    }

    public bool IsFresh(InfoProxyItemSearch* proxy, uint expectedId)
    {
        var now = DateTime.UtcNow;
        if (_readyAt == DateTime.MinValue) _readyAt = now;
        if (proxy->WaitingForListings) _sawWaiting = true;

        var id = proxy->SearchItemId;
        if (id == 0 || (expectedId != 0 && id != expectedId)) return false;

        var count = proxy->ListingCount;
        if (count != _lastCount) { _lastCount = count; _countChangedAt = now; }

        // Something of this search is on screen: a different item than before, or we saw the request run.
        var started = id != _startId || _sawWaiting;

        if (count > 0 && started && now - _countChangedAt >= Settle) return true; // listings arrived and stopped changing
        if (!proxy->WaitingForListings && now - _readyAt > Grace)       return true; // request over (also: nobody sells it)
        return false;
    }

    public string Describe()
        => $"sawWaiting={_sawWaiting} startId={_startId} count={(_lastCount == uint.MaxValue ? "-" : _lastCount.ToString())}";
}
