using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Services;

namespace GoblinTweaks.Core;

/// <summary>One stack to put on the Market Board.</summary>
internal sealed record ListingTask(string Key, InventoryType Type, int Slot, uint ItemId, bool Hq, int Quantity, string Name);

/// <summary>What to do at one retainer: its own items to list, items from the bags/armoury to list, and whether to reprice afterwards.</summary>
internal sealed class RetainerJob(int row, ulong retainerId)
{
    public int Row { get; } = row;
    public ulong RetainerId { get; } = retainerId;
    public List<ListingTask> OwnItems { get; } = [];
    public List<ListingTask> BagItems { get; } = [];
    public bool Pinch { get; set; }
}

/// <summary>
/// Puts the ticked stacks on the Market Board, retainer by retainer, then reprices every listing of the
/// retainers it visited. Items inside a retainer must be sold by that retainer; items from the bags or armoury
/// chest go to whichever retainers still have room (20 listings each). Each listing is priced just under the
/// cheapest other seller, or with the Universalis normal price when nobody else sells the item. Every step waits
/// for its window and the whole run stops on a timeout, leaving the game's windows closed.
/// </summary>
internal sealed unsafe class SellRunner
{
    private enum Step
    {
        Idle, Prefetch, SelectRetainer, WaitMenu, PickEntry, WaitWindow,
        NextItem, OpenArmoury, OpenItemMenu, WaitItemMenu, WaitPriceWindow, Compare, WaitResults, WaitPriceClosed,
        Verify, Pinching, CloseWindow, WaitWindowClosed, WaitMenuAfter, WaitList,
    }

    private const string ListAddon     = "RetainerList";
    private const string TalkAddon     = "Talk";
    private const string MenuAddon     = "SelectString";
    private const string SellListAddon = "RetainerSellList";
    private const string ContextAddon  = "ContextMenu";
    private const string PriceAddon    = "RetainerSell";
    private const string ResultsAddon  = "ItemSearchResult";

    private const int MaxListings    = 20;
    private const int SelectRow      = 2; // RetainerList callback: (2, row)
    private const int SellMenuEntry  = 2; // "Sell items in your inventory on the market"
    private const int OwnMenuEntry   = 3; // "Sell items in your retainer's inventory on the market"
    private const int PutUpForSale   = 0; // first entry of an item's menu while a retainer's market is open
    private const int PriceConfirm   = 0;
    private const int PriceCancel    = 1;
    private const int PriceCompare   = 4;

    private static readonly TimeSpan StepTimeout    = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResultsTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan StepGap        = TimeSpan.FromMilliseconds(300);

    private readonly AutoPincher         _pincher;
    private readonly Func<PinchRules>    _rules;
    private readonly Action<string>      _say;

    private Step      _step = Step.Idle;
    private DateTime  _deadline;
    private DateTime  _notBefore;
    private DateTime  _nextSearch;

    private List<RetainerJob> _jobs = [];
    private RetainerJob?      _job;
    private bool              _ownPhase;               // current window: the retainer's own inventory (true) or the sell list (false)
    private bool              _sellPhaseDone;
    private bool              _ownPhaseDone;
    private Queue<ListingTask> _queue = new();
    private ListingTask?      _task;
    private bool              _armouryAsked;
    private ushort            _menuOwner;      // window the item menu is opened from: the sell list, or the Armoury Chest for armoury stacks
    private DateTime          _verifySeen = DateTime.MinValue;
    private ushort            _windowId;
    private bool              _pincherEnded;
    private readonly SearchWatch _watch = new();
    private bool _confirmed;
    private int _listingsBefore;
    private int _pricePaid;
    private DateTime _verifyUntil;

    private int _listed, _skipped, _unplaced, _visited;
    private readonly List<string> _problems = [];

    // Universalis normal prices (world, then data center), fetched once before the run: itemId -> (nq, hq).
    private Task<Dictionary<uint, (long Nq, long Hq)>?>? _prefetch;
    private Dictionary<uint, (long Nq, long Hq)> _fallback = [];

    public SellRunner(AutoPincher pincher, Func<PinchRules> rules, Action<string> say)
    {
        _pincher = pincher;
        _rules   = rules;
        _say     = say;
        _pincher.Ended += () => _pincherEnded = true;
    }

    public bool IsRunning => _step != Step.Idle;

    // ── Planning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts listing <paramref name="tasks"/>. Returns false (after saying why) when it cannot start; tasks that
    /// do not fit in the retainers' free listing slots are left out and counted.
    /// </summary>
    public bool Start(IReadOnlyList<ListingTask> tasks, bool pinchAfterwards)
    {
        if (IsRunning || _pincher.IsRunning) return false;

        if (!AddonCallback.Ready(ListAddon, out _)) { _say("noList"); return false; }
        if (tasks.Count == 0)                       { _say("noSelection"); return false; }

        var retMgr = RetainerManager.Instance();
        if (retMgr == null) return false;

        // Retainers by list row, with their free listing slots.
        var free   = new Dictionary<ulong, int>();
        var jobs   = new Dictionary<ulong, RetainerJob>();
        var order  = new List<RetainerJob>();
        for (uint i = 0; i < retMgr->GetRetainerCount() && i < 10; i++)
        {
            var ret = retMgr->GetRetainerBySortedIndex(i);
            if (ret == null || !ret->Available) continue;

            free[ret->RetainerId] = Math.Max(0, MaxListings - ret->MarketItemCount);
            var job = new RetainerJob((int)i, ret->RetainerId) { Pinch = pinchAfterwards && ret->MarketItemCount > 0 };
            jobs[ret->RetainerId] = job;
            order.Add(job);
        }

        _unplaced = 0;

        // Items inside a retainer can only be sold by that retainer.
        foreach (var task in tasks.Where(t => RetainerOf(t) != 0))
        {
            var id = RetainerOf(task);
            if (jobs.TryGetValue(id, out var job) && free[id] > 0) { job.OwnItems.Add(task); free[id]--; }
            else _unplaced++;
        }

        // The rest go to any retainer with room, in list order.
        foreach (var task in tasks.Where(t => RetainerOf(t) == 0))
        {
            var target = order.FirstOrDefault(j => free[j.RetainerId] > 0);
            if (target is null) { _unplaced++; continue; }

            target.BagItems.Add(task);
            free[target.RetainerId]--;
        }

        _jobs = order.Where(j => j.OwnItems.Count > 0 || j.BagItems.Count > 0 || j.Pinch).ToList();
        if (_jobs.Count == 0 || _jobs.All(j => j.OwnItems.Count == 0 && j.BagItems.Count == 0))
        {
            _say("noCapacity");
            return false;
        }

        _listed = _skipped = _visited = 0;
        _problems.Clear();
        _fallback = [];

        // Ask Universalis for the fallback prices while the first retainer is being opened.
        var ids = _jobs.SelectMany(j => j.OwnItems.Concat(j.BagItems)).Select(t => t.ItemId).Distinct().ToArray();
        _prefetch = SellPrices.FetchAsync(ids);

        Enter(Step.Prefetch, TimeSpan.FromSeconds(120));
        return true;
    }

    /// <summary>Retainer id when the stack lives in a retainer's inventory, 0 for the bags and armoury.</summary>
    private static ulong RetainerOf(ListingTask task)
        => task.Key.StartsWith("ret", StringComparison.Ordinal) && ulong.TryParse(task.Key.Split('|')[0][3..], out var id) ? id : 0;


    // ── Stopping ──────────────────────────────────────────────────────────────

    public void Stop(string reason)
    {
        if (!IsRunning) return;

        if (_pincher.IsRunning) _pincher.Stop("stopped");
        if (AddonCallback.Ready(ResultsAddon, out var results)) results->Close(true);
        if (AddonCallback.Ready(PriceAddon, out var price)) AddonCallback.Fire(price, PriceCancel);

        _step = Step.Idle;
        _say($"sellstop|{_listed}|{_skipped}|{_unplaced}|{reason}");
        foreach (var problem in _problems) _say($"problem|{problem}");
    }

    private void Finish()
    {
        _step = Step.Idle;
        _say($"sellDone|{_listed}|{_skipped}|{_unplaced}");
        foreach (var problem in _problems) _say($"problem|{problem}");
    }

    // ── Driver ────────────────────────────────────────────────────────────────

    public void Update()
    {
        if (!IsRunning || DateTime.UtcNow < _notBefore) return;

        if (DateTime.UtcNow > _deadline)
        {
            // The game ignoring "Put Up for Sale" for one stack (for example Market Prohibited items) must not stop the whole run.
            if (_step is Step.OpenArmoury or Step.WaitItemMenu or Step.WaitPriceWindow && _task is not null)
            {
                Skip("WouldNot");
                return;
            }

            Stop($"timeout:{_step}");
            return;
        }

        switch (_step)
        {
            case Step.Prefetch:
                if (_prefetch is { IsCompleted: false }) return;
                _fallback = _prefetch is { IsCompletedSuccessfully: true } ? _prefetch.Result ?? [] : [];
                _job = null;
                StartNextJob();
                break;

            case Step.SelectRetainer:
                if (!AddonCallback.Ready(ListAddon, out var list)) return;
                AddonCallback.Fire(list, SelectRow, (uint)_job!.Row, null);
                Enter(Step.WaitMenu);
                break;

            case Step.WaitMenu:
                // The retainer greets us first; keep advancing the dialog until the menu shows.
                if (AddonCallback.Ready(MenuAddon, out _)) { NextPhaseOrQuit(); return; }
                AdvanceTalk();
                break;

            case Step.PickEntry:
                if (!AddonCallback.Ready(MenuAddon, out var menu)) return;
                AddonCallback.Fire(menu, _ownPhase ? OwnMenuEntry : SellMenuEntry);
                Enter(Step.WaitWindow);
                break;

            case Step.WaitWindow:
                if (!FindWindow(out var window)) return;
                _windowId = window->Id;
                _queue    = new Queue<ListingTask>(_ownPhase ? _job!.OwnItems : _job!.BagItems);
                Enter(Step.NextItem);
                break;

            case Step.NextItem:
                if (_queue.Count == 0)
                {
                    // After the new listings, reprice everything this retainer sells (the sell list is open).
                    if (!_ownPhase && _job!.Pinch)
                    {
                        _pincherEnded = false;
                        if (_pincher.Start()) { Enter(Step.Pinching, TimeSpan.FromMinutes(10)); return; }
                    }

                    Enter(Step.CloseWindow);
                    return;
                }

                _task = _queue.Dequeue();
                _menuOwner    = _windowId;
                _armouryAsked = false;
                Enter(SellInventory.IsArmoury(_task.Type) ? Step.OpenArmoury : Step.OpenItemMenu);
                break;

            case Step.OpenArmoury:
            {
                // The game only offers "Put Up for Sale" for an armoury stack when the menu is opened from the
                // Armoury Chest itself, so open it (like the "Open Armoury Chest" button does) and use its window.
                if (!AddonCallback.Ready("ArmouryBoard", out var board))
                {
                    if (!_armouryAsked)
                    {
                        _armouryAsked = true;
                        var agentModule = AgentModule.Instance();
                        agentModule->GetAgentByInternalId(AgentId.ArmouryBoard)->Show();
                    }

                    return;
                }

                _menuOwner = board->Id;
                Enter(Step.OpenItemMenu);
                break;
            }

            case Step.OpenItemMenu:
                OpenItemMenu();
                break;

            case Step.WaitItemMenu:
                if (!AddonCallback.Ready(ContextAddon, out var context)) return;
                var agent = AgentInventoryContext.Instance();
                if (agent == null || agent->ContextItemCount <= 0 || agent->TargetInventoryId != _task!.Type || agent->TargetInventorySlotId != _task.Slot)
                    return;
                if (agent->IsContextItemDisabled(PutUpForSale)) { Skip("NotAllowed"); return; }
                AddonCallback.Fire(context, 0, PutUpForSale, 0u, null, null);
                Enter(Step.WaitPriceWindow);
                break;

            case Step.WaitPriceWindow:
                if (!AddonCallback.Ready(PriceAddon, out var price)) return;
                var prepared = PrepareListing((AddonRetainerSell*)price);
                if (prepared is null) return;                          // the window has not filled in its item yet
                if (prepared == false) { Skip("Unexpected"); return; }
                Enter(Step.Compare);
                break;

            case Step.Compare:
                if (DateTime.UtcNow < _nextSearch) return; // the Market Board throttles quick repeated searches
                if (!AddonCallback.Ready(PriceAddon, out price)) { Stop("closed"); return; }
                var before = InfoProxyItemSearch.Instance();
                _watch.Reset(before == null ? 0 : before->SearchItemId);
                AddonCallback.Fire(price, PriceCompare);
                _nextSearch = DateTime.UtcNow.AddMilliseconds(_rules().SearchDelayMs);
                Enter(Step.WaitResults, ResultsTimeout);
                break;

            case Step.WaitResults:
                if (!AddonCallback.Ready(ResultsAddon, out var results)) return;
                var proxy = InfoProxyItemSearch.Instance();
                if (proxy == null || !_watch.IsFresh(proxy, _task!.ItemId)) return;

                var newPrice = ChoosePrice(proxy, _task);
                results->Close(true);
                if (!AddonCallback.Ready(PriceAddon, out price)) { Stop("closed"); return; }

                if (newPrice is { } p)
                {
                    ((AddonRetainerSell*)price)->AskingPrice->SetValue(p);
                    _listingsBefore = ListingsNow();
                    _pricePaid      = p;
                    AddonCallback.Fire(price, PriceConfirm);
                    _confirmed = true;
                }
                else
                {
                    AddonCallback.Fire(price, PriceCancel);
                    _skipped++;
                    _problems.Add($"{_task.Name}|NoPrice");
                    _confirmed = false;
                }

                Enter(Step.WaitPriceClosed);
                break;

            case Step.WaitPriceClosed:
                if (AddonCallback.Ready(PriceAddon, out _)) return;
                if (_confirmed) { _verifyUntil = DateTime.UtcNow.AddSeconds(5); _verifySeen = DateTime.MinValue; Enter(Step.Verify, TimeSpan.FromSeconds(14)); }
                else Enter(Step.NextItem);
                break;

            case Step.Verify:
                // The listing shows up at once but the server can still refuse it, putting the item back where it was.
                // So it only counts when it is still there a few seconds later.
                if (ListingsNow() > _listingsBefore)
                {
                    if (_verifySeen == DateTime.MinValue) { _verifySeen = DateTime.UtcNow; return; }
                    if (DateTime.UtcNow - _verifySeen < TimeSpan.FromSeconds(3)) return;

                    _listed++;
                    _say($"listedOne|{_task!.Name}|{_task.Quantity}|{_pricePaid}");
                    Enter(Step.NextItem);
                }
                else if (_verifySeen != DateTime.MinValue)
                {
                    _skipped++;
                    _problems.Add($"{_task!.Name}|Refused");
                    Enter(Step.NextItem);
                }
                else if (DateTime.UtcNow > _verifyUntil)
                {
                    _skipped++;
                    _problems.Add($"{_task!.Name}|NotAdded");
                    Enter(Step.NextItem);
                }
                break;

            case Step.Pinching:
                if (_pincher.IsRunning && !_pincherEnded) { _deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10); return; }
                if (!_pincher.LastRunCompleted) { _step = Step.Idle; return; } // it already reported why it stopped
                Enter(Step.CloseWindow);
                break;

            case Step.CloseWindow:
                if (AddonCallback.Ready("ArmouryBoard", out var armoury)) armoury->Close(true);
                if (FindWindow(out var open))
                {
                    if (_ownPhase) open->Close(true);
                    else AddonCallback.Fire(open, -1);
                }

                Enter(Step.WaitWindowClosed);
                break;

            case Step.WaitWindowClosed:
                if (FindWindow(out _)) return;
                Enter(Step.WaitMenuAfter);
                break;

            case Step.WaitMenuAfter:
                if (!AddonCallback.Ready(MenuAddon, out _)) return;
                NextPhaseOrQuit();
                break;

            case Step.WaitList:
                if (AddonCallback.Ready(ListAddon, out _)) { StartNextJob(); return; }
                AdvanceTalk(); // the retainer says goodbye
                break;
        }
    }

    // ── Steps ─────────────────────────────────────────────────────────────────

    private void StartNextJob()
    {
        var done = _job;
        _jobs = _jobs.Where(j => j != done).ToList();
        if (_jobs.Count == 0) { Finish(); return; }

        _job = _jobs[0];
        _visited++;
        _ownPhase      = false;
        _ownPhaseDone  = _job.OwnItems.Count == 0;
        _sellPhaseDone = _job.BagItems.Count == 0 && !_job.Pinch;
        Enter(Step.SelectRetainer);
    }

    /// <summary>Own inventory first, then the sell list (bags/armoury items and the reprice), then leave the retainer.</summary>
    private void NextPhaseOrQuit()
    {
        if (!_ownPhaseDone)       { _ownPhase = true;  _ownPhaseDone  = true; Enter(Step.PickEntry); return; }
        if (!_sellPhaseDone)      { _ownPhase = false; _sellPhaseDone = true; Enter(Step.PickEntry); return; }

        // "Quit" is always the last entry of the retainer menu.
        if (!AddonCallback.Ready(MenuAddon, out var menu)) { Enter(Step.WaitMenuAfter); return; }
        var entries = AddonCallback.MenuEntries(menu);
        if (entries.Count == 0) return;
        AddonCallback.Fire(menu, entries.Count - 1);
        Enter(Step.WaitList);
    }

    /// <summary>Listings the open retainer has, from its market container and from the retainer record (the larger one).</summary>
    private static int ListingsNow()
    {
        var retMgr = RetainerManager.Instance();
        var active = retMgr == null ? null : retMgr->GetActiveRetainer();
        return Math.Max(AutoPincher.CountListings(), active == null ? 0 : active->MarketItemCount);
    }

    private bool FindWindow(out AtkUnitBase* window)
    {
        if (!_ownPhase) return AddonCallback.Ready(SellListAddon, out window);
        return AddonCallback.Ready("InventoryRetainerLarge", out window) || AddonCallback.Ready("InventoryRetainer", out window);
    }

    private void OpenItemMenu()
    {
        var task = _task!;

        // The slot must still hold what was ticked (the inventory may have changed since).
        var mgr       = InventoryManager.Instance();
        var container = mgr == null ? null : mgr->GetInventoryContainer(task.Type);
        var slot      = container == null || task.Slot >= container->Size ? null : container->GetInventorySlot(task.Slot);
        var hq        = slot != null && (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        if (slot == null || slot->ItemId != task.ItemId || hq != task.Hq)
        {
            Skip("Gone");
            return;
        }

        var agent = AgentInventoryContext.Instance();
        if (agent == null) { Stop("error"); return; }

        agent->OpenForItemSlot(task.Type, task.Slot, 0, _menuOwner);
        Enter(Step.WaitItemMenu);
    }

    /// <summary>Checks the price window is for the expected item and sets the quantity (the whole stack unless edited).</summary>
    private bool? PrepareListing(AddonRetainerSell* window)
    {
        var task = _task!;

        // The first value of this window is the item's ICON id, not its item id, so identify the item by the
        // name it shows. Null while the name is not there yet.
        var name = window->ItemName == null ? string.Empty : window->ItemName->NodeText.ToString();
        if (name.Length == 0) return null;
        if (!name.Contains(task.Name, StringComparison.Ordinal)) return false;

        if (window->Quantity != null)
        {
            var stack = (int)Math.Max(1, task.Quantity);
            window->Quantity->SetValue(Math.Clamp(task.Quantity, 1, stack));
        }

        return true;
    }

    /// <summary>
    /// Just under the cheapest other seller; the same price when the cheapest is one of our own retainers (never
    /// undercut ourselves); the Universalis normal price when nobody else sells it. Null when there is no reference.
    /// </summary>
    private int? ChoosePrice(InfoProxyItemSearch* proxy, ListingTask task)
    {
        var rules = _rules();
        var floor = Math.Max(1, rules.MinPrice);

        var lowest = AutoPincher.LowestListing(proxy, task.ItemId, task.Hq, out var own);
        if (lowest is { } l)
        {
            var target = own ? (long)l : l - Math.Max(0, rules.UndercutAmount);
            return (int)Math.Clamp(target, floor, int.MaxValue);
        }

        if (_fallback.TryGetValue(task.ItemId, out var normal))
        {
            var price = task.Hq ? normal.Hq : normal.Nq; // only sales of the same quality count
            if (price > 0) return (int)Math.Clamp(price, floor, int.MaxValue);
        }

        return null;
    }

    /// <summary>Gives up on the current stack (closing anything it opened) and carries on with the next one.</summary>
    private void Skip(string reason)
    {
        if (AddonCallback.Ready(ResultsAddon, out var results)) results->Close(true);
        if (AddonCallback.Ready(PriceAddon, out var price)) AddonCallback.Fire(price, PriceCancel);
        if (AddonCallback.Ready(ContextAddon, out var context)) context->Close(true);

        _skipped++;
        _problems.Add($"{_task?.Name}|{reason}");
        Enter(Step.NextItem);
    }

    private void AdvanceTalk()
    {
        if (!AddonCallback.Ready(TalkAddon, out var talk)) return;

        AddonCallback.Fire(talk);
        _notBefore = DateTime.UtcNow + TimeSpan.FromMilliseconds(450); // let the next line of dialog appear
    }

    private void Enter(Step step, TimeSpan? timeout = null)
    {
        _step      = step;
        _deadline  = DateTime.UtcNow + (timeout ?? StepTimeout);
        _notBefore = DateTime.UtcNow + StepGap;
    }
}
