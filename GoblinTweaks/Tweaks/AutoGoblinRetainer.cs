using GoblinTweaks.Localization;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Lumina.Excel.Sheets;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.UI;
using KamiToolKit.Nodes;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Adds two buttons to the retainer list (next to the venture coins): Pinch reprices every listing on the
/// Market Board just below the cheapest seller, and Sell opens a docked window to pick items from bags, armoury
/// chest and retainers and list them for sale. A help button at the bottom of the retainer list explains it all.
/// </summary>
[Tweak(TweakCategory.Inventory)]
public sealed unsafe class AutoGoblinRetainer : Tweak<AutoGoblinRetainer.Options>
{
    public sealed class Options
    {
        /// <summary>Gil taken off the cheapest other listing.</summary>
        public int UndercutAmount { get; set; } = 1;
        /// <summary>Largest price cut allowed in one pass, as % of the current price (100 = no limit).</summary>
        public int MaxUndercutPercent { get; set; } = 70;
        /// <summary>Never price below this.</summary>
        public int MinPrice { get; set; } = 1;
        /// <summary>Wait between two Market Board searches; the game throttles quick repeats.</summary>
        public int SearchDelayMs { get; set; } = 1500;
        /// <summary>After listing, reprice every listing of the retainers that were visited.</summary>
        public bool PinchAfterListing { get; set; } = true;
    }

    // Geometry inside the RetainerList window. Tune if the game's layout differs.
    private const float ButtonW      = 76f;
    private const float ButtonH      = 24f;
    private const float ButtonGap    = 4f;
    private const float MarginRight  = 120f; // leaves room for the venture coin count
    private const float ButtonTop    = 9f;
    private const float SellListMarginRight = 60f;

    // The game answers every price change with "Asking price updated." and something copies each compared price to
    // the clipboard with a chat line. During a run that is dozens of lines, so they are hidden while it works.
    private const uint AskingPriceUpdatedId = 740; // LogMessage row of "Asking price updated."
    private static readonly string[] ClipboardWords = ["clipboard", "portapapeles", "zwischenablage", "presse-papiers", "クリップボード"];
    private string? _askingPriceText;
    private DateTime _quietUntil = DateTime.MinValue;

    private DateTime            _nextCapture = DateTime.MinValue;
    private AutoPincher?        _pincher;
    private RetainerCycler?     _cycler;
    private SellRunner?         _runner;
    private TextButtonNode?     _sellListPinch;
    private TextButtonNode?     _pinchButton;
    private TextButtonNode?     _sellButton;
    private RetainerSellAddon?  _window;
    private AutoGoblinRetainerSettingsAddon? _settingsWindow;
    private TextHelpAddon?      _helpWindow;

    protected internal override void Enable()
    {
        _window = new RetainerSellAddon
        {
            InternalName    = "GtkAutoGoblinRetainer",
            Title           = T("WindowTitle"),
            Size            = new Vector2(660f, 640f),
            Tweak           = this,
            RespectCloseAll = false,
        };

        _settingsWindow = NewSettingsWindow();

        _pincher = new AutoPincher(PinchRulesNow, Say);
        _cycler  = new RetainerCycler(_pincher, Say);
        _runner  = new SellRunner(_pincher, PinchRulesNow, Say);

        _askingPriceText = Svc.Data.GetExcelSheet<LogMessage>()?.GetRowOrDefault(AskingPriceUpdatedId)?.Text.ExtractText();
        Svc.Chat.CheckMessageHandled += OnCheckMessage;

        Svc.Framework.Update += OnUpdate;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,    "RetainerSellList", OnSellListSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,  "RetainerSellList", OnSellListClosing);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup,    "RetainerList", OnRetainerListSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize,  "RetainerList", OnRetainerListClosing);

        // The retainer list may already be open when the tweak is switched on.
        var existing = (AtkUnitBase*)Svc.GameGui.GetAddonByName("RetainerList").Address;
        if (existing is not null && existing->IsVisible)
            AttachButtons(existing);

        var sellList = (AtkUnitBase*)Svc.GameGui.GetAddonByName("RetainerSellList").Address;
        if (sellList is not null && sellList->IsVisible)
            AttachSellListButton(sellList);
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Chat.CheckMessageHandled -= OnCheckMessage;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "RetainerSellList", OnSellListSetup);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "RetainerSellList", OnSellListClosing);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup,   "RetainerList", OnRetainerListSetup);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "RetainerList", OnRetainerListClosing);

        _runner?.Stop("stopped");
        _runner = null;
        _cycler?.Stop("stopped");
        _cycler = null;
        _pincher?.Stop("stopped");
        _pincher = null;
        DetachButtons();
        DetachSellListButton();
        _window?.Dispose();
        _window = null;
        _settingsWindow?.Dispose();
        _settingsWindow = null;
        _helpWindow?.Dispose();
        _helpWindow = null;
    }

    // Remembers the open retainer's contents even when the sell window is closed,
    // because the game only keeps one retainer in memory at a time.
    private void OnUpdate(IFramework _)
    {
        if (_runner is { IsRunning: true } || _cycler is { IsRunning: true } || _pincher is { IsRunning: true })
            _quietUntil = DateTime.UtcNow.AddSeconds(3); // the last messages of a run arrive a moment after it ends

        try   { _runner?.Update(); _cycler?.Update(); _pincher?.Update(); }
        catch (Exception ex)
        {
            _pincher?.Stop("error");
            ReportFailure(ex);
            return;
        }

        if (DateTime.UtcNow < _nextCapture) return;
        _nextCapture = DateTime.UtcNow.AddSeconds(1);

        try
        {
            SellInventory.CaptureActiveRetainer();
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    // ── Chat noise ────────────────────────────────────────────────────────────

    private bool Working => DateTime.UtcNow < _quietUntil;

    /// <summary>Hides the "price copied to clipboard" and "Asking price updated." lines while a run is working.</summary>
    private void OnCheckMessage(IHandleableChatMessage message)
    {
        if (message.IsHandled || !Working) return;

        var text = message.Message.TextValue;
        if (ClipboardWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(_askingPriceText) && text.Contains(_askingPriceText, StringComparison.Ordinal)))
            message.PreventOriginal();
    }

    // ── Retainer list hooks ───────────────────────────────────────────────────

    private void OnRetainerListSetup(AddonEvent type, AddonArgs args)
    {
        try   { AttachButtons((AtkUnitBase*)args.Addon.Address); }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private void OnRetainerListClosing(AddonEvent type, AddonArgs args)
    {
        try
        {
            DetachButtons();
            _window?.Close();
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private void AttachButtons(AtkUnitBase* addon)
    {
        DetachButtons();
        if (addon is null) return;

        var width = addon->GetScaledWidth(true) / Math.Max(addon->Scale, 0.01f);
        var x     = width - MarginRight - ButtonW * 2 - ButtonGap;

        _pinchButton = new TextButtonNode
        {
            String      = T("PinchButton"),
            Position    = new Vector2(x, ButtonTop),
            Size        = new Vector2(ButtonW, ButtonH),
            TextTooltip = T("PinchTooltip"),
            OnClick     = AutoPinch,
        };
        _pinchButton.AttachNode(addon);

        _sellButton = new TextButtonNode
        {
            String      = T("SellButton"),
            Position    = new Vector2(x + ButtonW + ButtonGap, ButtonTop),
            Size        = new Vector2(ButtonW, ButtonH),
            TextTooltip = T("SellTooltip"),
            OnClick     = ToggleWindow,
        };
        _sellButton.AttachNode(addon);
    }

    private void DetachButtons()
    {
        _pinchButton?.Dispose();
        _pinchButton = null;
        _sellButton?.Dispose();
        _sellButton = null;
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void ToggleWindow()
    {
        if (_window is null) return;
        if (_window.IsOpen) _window.Close(); else _window.Open();
    }

    /// <summary>Retainer list button: reprices the listings of every retainer, one after another. Click again to stop.</summary>
    private void AutoPinch()
    {
        if (_cycler is null || _runner is { IsRunning: true }) return;
        if (_cycler.IsRunning) _cycler.Stop("stopped");
        else _cycler.Start();
    }

    /// <summary>"Items for sale" button: reprices the open retainer's listings, or stops a run in progress.</summary>
    private void PinchOpenRetainer()
    {
        if (_pincher is null) return;
        if (_pincher.IsRunning) _pincher.Stop("stopped");
        else _pincher.Start();
    }

    private void OnSellListSetup(AddonEvent type, AddonArgs args)
    {
        try   { AttachSellListButton((AtkUnitBase*)args.Addon.Address); }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private void OnSellListClosing(AddonEvent type, AddonArgs args)
    {
        try
        {
            // A cycle closes the list itself between retainers; only a manual run is cancelled here.
            if (_cycler is not { IsRunning: true } && _runner is not { IsRunning: true }) _pincher?.Stop("closed");
            DetachSellListButton();
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private void AttachSellListButton(AtkUnitBase* addon)
    {
        DetachSellListButton();
        if (addon is null) return;

        var width = addon->GetScaledWidth(true) / Math.Max(addon->Scale, 0.01f);
        _sellListPinch = new TextButtonNode
        {
            String      = T("PinchButton"),
            Position    = new Vector2(width - SellListMarginRight - ButtonW, ButtonTop),
            Size        = new Vector2(ButtonW, ButtonH),
            TextTooltip = T("PinchOpenTooltip"),
            OnClick     = PinchOpenRetainer,
        };
        _sellListPinch.AttachNode(addon);
    }

    private void DetachSellListButton()
    {
        _sellListPinch?.Dispose();
        _sellListPinch = null;
    }

    /// <summary>Chat feedback. Codes are "reason" or "reason|repriced|unchanged|skipped".</summary>
    private void Say(string code)
    {
        var parts  = code.Split('|');
        var reason = parts[0].Split(':')[0];
        var text   = reason switch
        {
            "done"          => string.Format(T("Msg.Done"), Part(parts, 1), Part(parts, 2), Part(parts, 3)) + Who(parts),
            "stopped"       => string.Format(T("Msg.Stopped"), Part(parts, 1), Part(parts, 2), Part(parts, 3)) + Who(parts),
            "closed"        => string.Format(T("Msg.Closed"), Part(parts, 1), Part(parts, 2), Part(parts, 3)),
            "timeout"       => string.Format(T("Msg.Timeout"), Part(parts, 1), Part(parts, 2), Part(parts, 3)) + " " + code.Split('|')[0].Replace("timeout:", string.Empty),
            "error"         => string.Format(T("Msg.Stopped"), Part(parts, 1), Part(parts, 2), Part(parts, 3)),
            "noSellList"    => T("Msg.NoSellList"),
            "noItems"       => T("Msg.NoItems"),
            "noList"        => T("Msg.NoList"),
            "listedOne"     => string.Format(T("Msg.ListedOne"), parts.Length > 1 ? parts[1] : "?", Part(parts, 2), Part(parts, 3)),
            "noSelection"   => T("Msg.NoSelection"),
            "noCapacity"    => T("Msg.NoCapacity"),
            "sellDone"      => string.Format(T("Msg.SellDone"), Part(parts, 1), Part(parts, 2), Part(parts, 3)),
            "sellstop"      => string.Format(T("Msg.SellStopped"), Part(parts, 1), Part(parts, 2), Part(parts, 3)) + (parts.Length > 4 ? " (" + parts[4] + ")" : string.Empty),
            "problem"       => "  - " + (parts.Length > 1 ? parts[1] : string.Empty) + ": " + (parts.Length > 2 ? T("Problem." + parts[2]) : string.Empty),
            "cycleDone"     => string.Format(T("Msg.CycleDone"), Part(parts, 1), Part(parts, 2), Part(parts, 3), Part(parts, 4)),
            "visiting"      => string.Format(T("Msg.Visiting"), parts.Length > 1 ? parts[1] : "?"),
            _               => code,
        };
        Svc.Chat.Print($"[AutoGoblinRetainer] {text}");
    }

    /// <summary>The retainer a message is about, when a tour of several retainers is running.</summary>
    private static string Who(string[] parts) => parts.Length > 4 && parts[4].Length > 0 ? $" [{parts[4]}]" : string.Empty;

    private static string Part(string[] parts, int i) => i < parts.Length ? parts[i] : "0";

    // ── Settings and help windows ─────────────────────────────────────────────

    /// <summary>The buttons in the GoblinTweaks window: the options live in a native window.</summary>
    public override IReadOnlyList<TweakButton> Buttons => [new(TMain("Settings"), null, () => _settingsWindow?.Toggle())];

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, OpenHelp);

    protected override IReadOnlyList<string> CommandNames => ["/agr", "/goblinretainer"];

    protected override void OnCommand() => _settingsWindow?.Toggle();

    private AutoGoblinRetainerSettingsAddon NewSettingsWindow() => new()
    {
        InternalName    = "GtkAutoGoblinRetainerSettings",
        Title           = T("SettingsTitle"),
        Size            = new Vector2(540f, 510f),
        Tweak           = this,
        RespectCloseAll = false,
    };

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;

        Rebuild(ref _settingsWindow, NewSettingsWindow);
        _helpWindow?.Dispose();
        _helpWindow = null;
    }

    internal Options Current => Settings;
    internal void SaveCurrent() => SaveSettings();

    internal void OpenHelp()
    {
        // Built when opened so its text follows the language selected now.
        if (_helpWindow is { IsOpen: true }) { _helpWindow.Close(); return; }
        _helpWindow?.Dispose();

        _helpWindow = new TextHelpAddon
        {
            InternalName    = "GtkAutoGoblinRetainerHelp",
            Title           = T("HelpTitle"),
            Size            = new Vector2(780f, 560f),
            RespectCloseAll = false,
            Pages =
            [
                (T("Help.Overview.Title"), T("Help.Overview.Text")),
                (T("Help.Pinch.Title"),    T("Help.Pinch.Text")),
                (T("Help.Sell.Title"),     T("Help.Sell.Text")),
                (T("Help.Faq.Title"),      T("Help.Faq.Text")),
            ],
        };
        _helpWindow.Open();
    }

    private PinchRules PinchRulesNow()
        => new(Settings.UndercutAmount, Settings.MaxUndercutPercent, Settings.MinPrice, Settings.SearchDelayMs);

    /// <summary>"List on Market Board": lists the ticked stacks (or stops the run in progress).</summary>
    internal void SellSelected(IReadOnlyCollection<(SellableItem Item, int Quantity)> picks)
    {
        if (_runner is null || _cycler is { IsRunning: true }) return;
        if (_runner.IsRunning) { _runner.Stop("stopped"); return; }

        var tasks = new List<ListingTask>();
        foreach (var (item, quantity) in picks)
        {
            // Keys look like "bags|<inventory type>|<slot>".
            var parts = item.Key.Split('|');
            if (parts.Length != 3) continue;
            if (!int.TryParse(parts[1], out var type) || !int.TryParse(parts[2], out var slot)) continue;

            tasks.Add(new ListingTask(item.Key, (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)type, slot, item.ItemId, item.Hq, quantity, item.Name));
        }

        _runner.Start(tasks, Settings.PinchAfterListing);
    }

    // ── Strings used by the windows ───────────────────────────────────────────

    internal string TabTitle(SellSource source) => T(source switch
    {
        SellSource.Bags      => "Tab.Bags",
        _                    => "Tab.Armoury",
    });

    internal string Text(string key) => T(key);

    internal string ColumnText(string key) => T(key);

    /// <summary>The "pinch after listing" option, shown as a checkbox next to the list button and in the settings.</summary>
    internal bool PinchAfter
    {
        get => Settings.PinchAfterListing;
        set { Settings.PinchAfterListing = value; SaveSettings(); }
    }

    internal string HelpTooltip => T("HelpTooltip");

    internal string SelectAllTooltip => T("SelectAll");
    internal string PriceTooltip     => T("PriceTooltip");

    internal string PinchAfterText    => T("Setting.PinchAfter");
    internal string PinchAfterTooltip => T("Setting.PinchAfter.Help");

    internal string SelectedText(int count) => string.Format(T("Selected"), count);

    internal string SellButtonText    => T("ListSelected");
    internal string SellButtonTooltip => T("ListSelectedTooltip");
}
