using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.Core;

/// <summary>
/// Walks through every retainer that has something on the Market Board, and for each one: selects it in the
/// retainer list, skips its greeting, opens "Sell items in your inventory on the market", lets the auto-pinch
/// reprice it, then quits the retainer and goes back to the list. Callback values come from the game's own
/// clicks (recorded with the diagnostic log); every step waits for its window and aborts on a timeout.
/// </summary>
internal sealed unsafe class RetainerCycler
{
    private enum Step { Idle, SelectRetainer, WaitMenu, PickSell, WaitSellList, Pinching, CloseSellList, WaitMenuAgain, WaitList }

    private const string ListAddon     = "RetainerList";
    private const string TalkAddon     = "Talk";
    private const string MenuAddon     = "SelectString";
    private const string SellListAddon = "RetainerSellList";

    // RetainerList callback that selects a row: (2, row). SelectString entry 2 is "Sell items in your inventory on the market".
    private const int SelectRetainer = 2;
    private const int SellMenuEntry  = 2;

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StepGap     = TimeSpan.FromMilliseconds(350);

    private readonly AutoPincher    _pincher;
    private readonly Action<string> _say;

    private Step       _step = Step.Idle;
    private DateTime   _deadline;
    private DateTime   _notBefore;
    private List<int>  _rows = [];   // retainer-list rows still to visit
    private int        _visited;
    private bool       _pincherEnded;

    public RetainerCycler(AutoPincher pincher, Action<string> say)
    {
        _pincher = pincher;
        _say     = say;
        _pincher.Ended += () => _pincherEnded = true;
    }

    public bool IsRunning => _step != Step.Idle;

    public bool Start()
    {
        if (IsRunning || _pincher.IsRunning) return false;

        if (!AddonCallback.Ready(ListAddon, out _))
        {
            _say("noList");
            return false;
        }

        _rows = [];
        var retMgr = RetainerManager.Instance();
        if (retMgr != null)
            for (uint i = 0; i < retMgr->GetRetainerCount() && i < 10; i++)
            {
                var ret = retMgr->GetRetainerBySortedIndex(i);
                if (ret != null && ret->Available && ret->MarketItemCount > 0)
                    _rows.Add((int)i);
            }

        if (_rows.Count == 0)
        {
            _say("noItems");
            return false;
        }

        _visited = 0;
        Enter(Step.SelectRetainer);
        return true;
    }

    public void Stop(string reason)
    {
        if (!IsRunning) return;

        _step = Step.Idle;
        if (_pincher.IsRunning) _pincher.Stop(reason);
        else _say($"{reason}|0|0|0");
    }

    public void Update()
    {
        if (!IsRunning || DateTime.UtcNow < _notBefore) return;

        if (DateTime.UtcNow > _deadline)
        {
            Stop($"timeout:{_step}");
            return;
        }

        switch (_step)
        {
            case Step.SelectRetainer:
                if (_rows.Count == 0) { Finish(); return; }
                if (!AddonCallback.Ready(ListAddon, out var list)) return;
                AddonCallback.Fire(list, SelectRetainer, (uint)_rows[0], null);
                Enter(Step.WaitMenu);
                break;

            case Step.WaitMenu:
                // The retainer greets us first; keep advancing the dialog until the menu shows.
                if (AddonCallback.Ready(MenuAddon, out _)) { Enter(Step.PickSell); return; }
                AdvanceTalk();
                break;

            case Step.PickSell:
                if (!AddonCallback.Ready(MenuAddon, out var menu)) return;
                AddonCallback.Fire(menu, SellMenuEntry);
                Enter(Step.WaitSellList);
                break;

            case Step.WaitSellList:
                if (!AddonCallback.Ready(SellListAddon, out _)) return;
                _rows.RemoveAt(0);
                _visited++;
                _pincherEnded = false;
                if (!_pincher.Start())
                {
                    // Nothing to reprice for this retainer; carry on with the next one.
                    Enter(Step.CloseSellList);
                    return;
                }

                Enter(Step.Pinching, TimeSpan.FromMinutes(10));
                break;

            case Step.Pinching:
                if (_pincher.IsRunning && !_pincherEnded) { _deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10); return; }
                if (!_pincher.LastRunCompleted) { _step = Step.Idle; return; } // it already reported why it stopped
                Enter(Step.CloseSellList);
                break;

            case Step.CloseSellList:
                if (AddonCallback.Ready(SellListAddon, out var sellList))
                {
                    AddonCallback.Fire(sellList, -1);
                    return;
                }

                Enter(Step.WaitMenuAgain);
                break;

            case Step.WaitMenuAgain:
                if (!AddonCallback.Ready(MenuAddon, out menu)) return;
                // "Quit" is always the last entry of the retainer menu.
                var entries = AddonCallback.MenuEntries(menu);
                if (entries.Count == 0) return;
                AddonCallback.Fire(menu, entries.Count - 1);
                Enter(Step.WaitList);
                break;

            case Step.WaitList:
                if (AddonCallback.Ready(ListAddon, out _))
                {
                    Enter(Step.SelectRetainer);
                    return;
                }

                AdvanceTalk(); // the retainer says goodbye
                break;
        }
    }

    private void AdvanceTalk()
    {
        if (!AddonCallback.Ready(TalkAddon, out var talk)) return;

        AddonCallback.Fire(talk);
        _notBefore = DateTime.UtcNow + TimeSpan.FromMilliseconds(450); // let the next line of dialog appear
    }

    private void Finish()
    {
        _step = Step.Idle;
        _say($"cycleDone|{_visited}");
    }

    private void Enter(Step step, TimeSpan? timeout = null)
    {
        _step      = step;
        _deadline  = DateTime.UtcNow + (timeout ?? StepTimeout);
        _notBefore = DateTime.UtcNow + StepGap;
    }
}
