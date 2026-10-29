using System.Text;
using Dalamud.Game;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.Services;
using GoblinTweaks.UI;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace GoblinTweaks.Tweaks;

/// <summary>A listing priced far below what the item normally sells for.</summary>
/// <param name="AveragePrice">Average sale price of the last days, the "normal" price of the item.</param>
/// <param name="NextPrice">Cheapest listing of another seller, 0 if there is none.</param>
/// <param name="SalesPerDay">Units of the item sold per day on the home world, where it would be resold.</param>
public sealed record SniperDeal(
    uint ItemId, string Name, uint IconId, int ItemLevel, string? Category,
    long AveragePrice, long Price, long NextPrice, int Quantity, string World, bool Hq, DateTime Reviewed, double SalesPerDay)
{
    /// <summary>Days given to the resale when estimating <see cref="ResaleChance"/>.</summary>
    public const int ResaleDays = 7;

    /// <summary>
    /// Chance, in percent, that at least one unit sells on the home world within <see cref="ResaleDays"/> days,
    /// taking sales as random and independent (Poisson) at the rate of <see cref="SalesPerDay"/>.
    /// </summary>
    public int ResaleChance => (int)Math.Round(100 * (1 - Math.Exp(-SalesPerDay * ResaleDays)));

    public int Discount => (int)Math.Round(100 - Price * 100.0 / AveragePrice);

    public long Profit => AveragePrice - Price;

    /// <summary>Identifies this exact listing, so a deal already seen is not announced again.</summary>
    public string Key => $"{ItemId}:{World}:{Price}";
}

/// <summary>A marketable item, as needed by the scanner, the settings search and the window.</summary>
public readonly record struct SniperItem(uint Id, string Name, uint IconId, int ItemLevel, string? Category);

/// <summary>Why the last market scan failed.</summary>
public enum SniperError
{
    None,
    /// <summary>Universalis answered with a server error (5xx) or is rate limiting.</summary>
    Busy,
    /// <summary>No answer in time.</summary>
    Timeout,
    /// <summary>Could not connect at all, or the answer made no sense.</summary>
    Offline,
}

/// <summary>Progress of the market scan, replaced as a whole so any thread can read it.</summary>
public sealed record SniperStatus(bool Scanning, int Done, int Total, DateTime LastScan, SniperError Error);

/// <summary>
/// Watches the market board (through Universalis) for listings priced far below what the item
/// normally sells for, e.g. a mount posted at 100,000 gil instead of 1,000,000. The number of new
/// finds is shown in the Server Info Bar; clicking it opens a native window with the list. New finds
/// can also be announced in the chat, with a sound.
/// </summary>
/// <remarks>
/// Nothing is read from or sent to the game: prices come from the public Universalis API, which
/// is crowd-sourced and can be out of date. The scan runs in the background, never on the game thread.
/// </remarks>
[Tweak(TweakCategory.Other)]
public sealed class GoblinSniper : Tweak<GoblinSniper.Options>
{
    public sealed class Options
    {
        /// <summary>Show the entry with the number of deals found in the Server Info Bar.</summary>
        public bool Notify { get; set; } = true;

        /// <summary>Scan every world of the home data center instead of only the home world.</summary>
        public bool WholeDataCenter { get; set; } = true;

        /// <summary>How far below the normal price a listing has to be, in percent.</summary>
        public int DiscountPercent { get; set; } = 30;

        /// <summary>Items that normally sell for less than this are ignored.</summary>
        public int MinAveragePrice { get; set; } = 50_000;

        public int ScanMinutes { get; set; } = 5;

        /// <summary>Listings whose data was uploaded longer ago than this are ignored.</summary>
        public int MaxAgeHours { get; set; } = 24;

        /// <summary>Items nobody bought for more than this many days are ignored: they would just sit on the market.</summary>
        public int MaxDaysWithoutSale { get; set; } = 60;

        /// <summary>Keys of <see cref="Categories"/> to watch.</summary>
        public HashSet<string> Categories { get; set; } = ["Mounts", "Minions"];

        /// <summary>Individual item ids to watch, whatever their category.</summary>
        public List<uint> Items { get; set; } = [];

        /// <summary>Items never listed as deals, whatever their category (chosen from the window).</summary>
        public List<uint> Ignored { get; set; } = [];

        /// <summary>Announce each new deal in the chat.</summary>
        public bool ChatAlert { get; set; } = true;

        /// <summary>Play a sound when new deals are found.</summary>
        public bool SoundAlert { get; set; }

        /// <summary>
        /// Earlier versions chose the language of this tweak's windows here; it now uses the language choice every
        /// tweak has, and the item names follow the game client. Moved on enable, then left empty.
        /// </summary>
        public string DataLanguage { get; set; } = string.Empty;
    }

    // Fixed, not translated: Dalamud remembers whether an entry is hidden and where it goes by its title.
    private const string DtrTitle = "GoblinTweaks: Sniper";

    // The <se.6> chat sound effect.
    private const uint AlertSound = 6;

    // A chat line for each new deal up to this many at once; the rest are counted in one more line.
    private const int MaxChatDeals = 3;

    /// <summary>
    /// Watchable item types: settings key and the rule that tells which items belong to it.
    /// An item gets the first type that matches, so the specific ones go before the broad ones.
    /// </summary>
    public static readonly (string Key, Func<LuminaItem, bool> Matches)[] Categories =
    [
        // By what using the item unlocks (ItemAction).
        ("Mounts", item => UseAction(item) == 1322),
        ("Minions", item => UseAction(item) == 853),
        ("Orchestrion", item => UseAction(item) == 25183),
        ("TripleTriad", item => UseAction(item) == 3357),
        ("Bardings", item => UseAction(item) == 1013),
        ("FashionAccessories", item => UseAction(item) == 20086),
        ("Unlocks", item => UseAction(item) == 2633),

        // By market board category (ItemSearchCategory), single ones first, then whole groups.
        ("Dyes", item => item.ItemSearchCategory.RowId == 54),
        ("Materia", item => item.ItemSearchCategory.RowId == 57),
        ("Workshop", item => item.ItemSearchCategory.RowId == 79),
        ("Registrable", item => item.ItemSearchCategory.RowId == 90),
        ("Weapons", item => item.ItemSearchCategory.Value.Category == 1),
        ("Armor", item => item.ItemSearchCategory.Value.Category == 2),
        ("Furnishings", item => item.ItemSearchCategory.Value.Category == 4),
    ];

    private static uint UseAction(LuminaItem item) => item.ItemAction.RowId == 0 ? 0 : item.ItemAction.Value.Action.RowId;

    private const int MaxSearchResults = 30;
    private static readonly TimeSpan RescanDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

    private sealed record ScanRequest(string Scope, string HomeWorld, int WorldsInDataCenter, bool WholeDataCenter, int Discount, int MinAveragePrice, TimeSpan MaxAge, TimeSpan MaxTimeWithoutSale, HashSet<string> Categories, HashSet<uint> Items, HashSet<uint> Ignored, ClientLanguage Language);

    private sealed class Catalog(ClientLanguage language, Dictionary<uint, SniperItem> items)
    {
        /// <summary>Language of the item names.</summary>
        public ClientLanguage Language { get; } = language;

        public Dictionary<uint, SniperItem> Items { get; } = items;

        public SniperItem[] ByName { get; } = [.. items.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)];

        public Dictionary<string, int> CategoryCounts { get; } = items.Values
            .Where(item => item.Category != null)
            .GroupBy(item => item.Category!)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private readonly HashSet<string> _seen = [];
    private readonly HashSet<string> _announced = [];
    private GoblinSniperSettingsAddon? _settingsAddon;
    private GoblinSniperAddon? _addon;
    private TextHelpAddon? _help;
    private IDtrBarEntry? _entry;
    private string? _shownText;
    private string? _shownTooltip;
    private CancellationTokenSource? _cancel;
    private DateTime _nextPoll = DateTime.MinValue;
    private DateTime _nextScan = DateTime.MinValue;
    private int _buildingCatalog;
    private bool _scanQueued;

    // Every scan gets a number; only the current one may change the status or the list. A cancelled scan that is
    // still finishing its last request must not mark the tweak as scanning again, or no scan would ever start.
    private readonly Lock _scanLock = new();
    private int _scanGeneration;

    // Deals listed the last time the deals were announced; the first list after enabling is not announced line
    // by line, so logging in does not flood the chat.
    private bool _announcedOnce;

    // Written by the background scan, read by the UI.
    private volatile IReadOnlyList<SniperDeal> _deals = [];
    private volatile SniperStatus _status = new(false, 0, 0, DateTime.MinValue, SniperError.None);
    private volatile Catalog? _catalog;

    // Settings window item search.

    /// <summary>Current deals, best discount first. A new list instance is published whenever a scan changes it.</summary>
    public IReadOnlyList<SniperDeal> Deals => _deals;

    public SniperStatus Status => _status;

    public DateTime NextScan => _nextScan;

    /// <summary>A scan was requested and has not finished yet; asking for another one would only restart it.</summary>
    public bool ScanPending => _scanQueued || _status.Scanning;

    /// <summary>Language of the item names: the one of the game client, so a copied name works in the market board search.</summary>
    public static ClientLanguage DataLanguage => Svc.ClientState.ClientLanguage;

    public string SettingsTitle => $"{Text("Name")} — {Text("Settings")}";

    /// <summary>Text of this tweak in the language chosen for it, for its native windows.</summary>
    internal string Text(string key) => T(key);

    private GoblinSniperSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkGoblinSniperSettings",
        Title           = SettingsTitle,
        Size            = new System.Numerics.Vector2(560f, 700f),
        Tweak           = this,
        RespectCloseAll = false,
    };

    private GoblinSniperAddon NewAddon() => new()
    {
        InternalName = "GtkGoblinSniper",
        Title        = Text("Name"),
        Size         = new System.Numerics.Vector2(990f, 620f),
        Tweak        = this,
    };

    protected internal override void Enable()
    {
        MoveOldLanguageSetting();

        _deals = [];
        _status = new SniperStatus(false, 0, 0, DateTime.MinValue, SniperError.None);
        _seen.Clear();
        _announced.Clear();
        _announcedOnce = false;
        _shownText = _shownTooltip = null;
        _nextScan = DateTime.UtcNow + RescanDelay;
        _scanQueued = true;

        _addon = NewAddon();

        _entry = Svc.DtrBar.Get(DtrTitle);
        _entry.Shown = false;
        _entry.OnClick = _ => _addon?.Open();

        _settingsAddon = NewSettingsAddon();

        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        CancelScan();

        _help?.Dispose();
        _help = null;

        _settingsAddon?.Dispose();
        _settingsAddon = null;

        _addon?.Dispose();
        _addon = null;
        _entry?.Remove();
        _entry = null;

        _deals = [];
        _catalog = null;
        _seen.Clear();
        _announced.Clear();
    }

    /// <summary>Earlier versions kept the window language in the options of this tweak; it now uses the language choice every tweak has.</summary>
    private void MoveOldLanguageSetting()
    {
        if (Settings.DataLanguage.Length == 0) return;

        if (Language.Length == 0) SetLanguage(Settings.DataLanguage);
        Settings.DataLanguage = string.Empty;
        SaveSettings(notify: false);
    }

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;

        Rebuild(ref _addon, NewAddon);
        Rebuild(ref _settingsAddon, NewSettingsAddon);
        _shownText = _shownTooltip = null;

        var helpWasOpen = _help is { IsOpen: true };
        _help?.Dispose();
        _help = null;
        if (helpWasOpen) Svc.Framework.RunOnTick(ToggleHelp, delayTicks: 6);
    }

    /// <summary>Help topics, in order; each has a "Help.{topic}.Title" and "Help.{topic}.Text".</summary>
    private static readonly string[] HelpTopics = ["About", "Columns", "Resale", "Sorting", "Data", "Scanning", "Items"];

    /// <summary>Opens the help, or closes it if it is open. It belongs to the tweak, so it is closed with it.</summary>
    internal void ToggleHelp()
    {
        _help ??= new TextHelpAddon
        {
            InternalName    = "GtkGoblinSniperHelp",
            Title           = Text("Help.Title"),
            Size            = new System.Numerics.Vector2(860f, 600f),
            RespectCloseAll = false,
            Pages           = [.. HelpTopics.Select(topic => (Text($"Help.{topic}.Title"), Text($"Help.{topic}.Text")))],
        };
        _help.Toggle();
    }

    // Any settings change re-scans with the new options.
    protected override void OnSettingsChanged() => RequestScan();

    /// <summary>Starts a new scan shortly, cancelling the one in progress.</summary>
    public void RequestScan()
    {
        CancelScan();
        _nextScan = DateTime.UtcNow + RescanDelay;
        _scanQueued = true;
    }

    public void OpenSettings()
    {
        if (_settingsAddon is { } window)
        {
            if (window.IsOpen) window.Close(); else window.Open();
        }
    }

    /// <summary>Called while the window is open: everything listed has been seen, so the tooltip stops calling it new.</summary>
    public void MarkAllSeen()
    {
        _seen.Clear();
        foreach (var deal in _deals)
            _seen.Add(deal.Key);
    }

    private void CancelScan()
    {
        lock (_scanLock)
        {
            _scanGeneration++;
            _status = _status with { Scanning = false };
        }

        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
    }

    /// <summary>Changes the status, unless the scan asking for it is no longer the current one.</summary>
    private bool SetStatus(int generation, Func<SniperStatus, SniperStatus> change)
    {
        lock (_scanLock)
        {
            if (generation != _scanGeneration) return false;
            _status = change(_status);
            return true;
        }
    }

    /// <summary>When the next scan is due, unless the scan asking for it is no longer the current one.</summary>
    private void SetNextScan(int generation, DateTime when)
    {
        lock (_scanLock)
        {
            if (generation == _scanGeneration) _nextScan = when;
        }
    }

    /// <summary>Stops listing an item and never lists it again (until it is taken off the ignored list in the settings).</summary>
    internal void Ignore(uint itemId)
    {
        if (Settings.Ignored.Contains(itemId)) return;

        Settings.Ignored.Add(itemId);
        _deals = [.. _deals.Where(deal => deal.ItemId != itemId)];
        SaveSettings();
        _settingsAddon?.ItemsChanged();
    }

    /// <summary>Lists an ignored item again.</summary>
    internal void Unignore(uint itemId)
    {
        if (Settings.Ignored.Remove(itemId)) SaveSettings();
    }

    /// <summary>The page of the item on Universalis, with its price history.</summary>
    internal static void OpenOnUniversalis(uint itemId) => Util.OpenLink($"https://universalis.app/market/{itemId}");

    /// <summary>Deal keys that were already listed when the window was last opened (the rest show as new).</summary>
    internal IReadOnlySet<string> SeenKeys => _seen;

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < _nextPoll) return;
        _nextPoll = DateTime.UtcNow.AddSeconds(1);

        try
        {
            UpdateEntry();
            AnnounceNewDeals();
            StartScanIfDue();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void UpdateEntry()
    {
        if (_entry == null) return;

        if (_entry.Shown != Settings.Notify) _entry.Shown = Settings.Notify;
        if (!Settings.Notify) return;

        var deals  = _deals;
        var unseen = deals.Count(deal => !_seen.Contains(deal.Key));
        var text   = $"{SeIconChar.BoxedStar.ToIconString()} {deals.Count}";

        var tip = new StringBuilder();
        tip.AppendLine(string.Format(Text("DtrTooltip"), deals.Count, unseen));
        foreach (var deal in deals.Take(5))
            tip.AppendLine($"  -{deal.Discount}%  {deal.Name}  {deal.Price:N0} ({deal.World})");
        tip.Append(Text("DtrClick"));
        var tooltip = tip.ToString();

        // Dalamud redraws the entry on every assignment: only touch it when the text really changed.
        if (text != _shownText)
        {
            _entry.Text = new SeStringBuilder().AddText(text).Build();
            _shownText  = text;
        }
        if (tooltip != _shownTooltip)
        {
            _entry.Tooltip = new SeStringBuilder().AddText(tooltip).Build();
            _shownTooltip  = tooltip;
        }
    }

    /// <summary>
    /// Says in the chat which deals are new since the last look (an item link, the price, the discount and the world),
    /// and plays a sound if chosen. The first list after enabling only says how many were found.
    /// </summary>
    private void AnnounceNewDeals()
    {
        // Until the first scan ends, wait: what it finds is announced once, as a count.
        if (!_announcedOnce && _status.LastScan == DateTime.MinValue) return;

        var deals = _deals;
        var fresh = deals.Where(deal => !_announced.Contains(deal.Key)).ToList();

        // Forget what is no longer listed, so the set does not grow for ever.
        _announced.IntersectWith(deals.Select(deal => deal.Key));
        if (fresh.Count == 0) { _announcedOnce = true; return; }

        foreach (var deal in fresh) _announced.Add(deal.Key);

        // The open window already shows them.
        if (_addon is { IsOpen: true }) { _announcedOnce = true; return; }

        if (Settings.ChatAlert)
        {
            if (!_announcedOnce)
            {
                Svc.Chat.Print(string.Format(Text("Chat.Found"), fresh.Count), Text("Name"));
            }
            else
            {
                foreach (var deal in fresh.Take(MaxChatDeals))
                {
                    var line = new SeStringBuilder()
                        .AddText(Text("Chat.New") + " ")
                        .AddItemLink(deal.ItemId, deal.Hq)
                        .AddText(" " + string.Format(Text("Chat.Deal"), deal.Price.ToString("N0"), deal.Discount, deal.World))
                        .Build();
                    Svc.Chat.Print(line, Text("Name"));
                }

                if (fresh.Count > MaxChatDeals)
                    Svc.Chat.Print(string.Format(Text("Chat.More"), fresh.Count - MaxChatDeals), Text("Name"));
            }
        }

        if (Settings.SoundAlert) UIGlobals.PlayChatSoundEffect(AlertSound);
        _announcedOnce = true;
    }

    private void StartScanIfDue()
    {
        if (_status.Scanning || DateTime.UtcNow < _nextScan || !Svc.PlayerState.IsLoaded) return;

        var world     = Svc.PlayerState.HomeWorld.Value;
        var homeWorld = world.Name.ExtractText();
        var scope     = Settings.WholeDataCenter ? world.DataCenter.Value.Name.ExtractText() : homeWorld;
        if (string.IsNullOrEmpty(scope)) return;

        _nextScan = DateTime.UtcNow.AddMinutes(Math.Clamp(Settings.ScanMinutes, 5, 120));

        var worlds  = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.World>().Count(other => other.IsPublic && other.DataCenter.RowId == world.DataCenter.RowId);
        var request = new ScanRequest(scope, homeWorld, Math.Max(1, worlds), Settings.WholeDataCenter, Math.Clamp(Settings.DiscountPercent, 1, 99), Settings.MinAveragePrice,
            TimeSpan.FromHours(Math.Clamp(Settings.MaxAgeHours, 1, 72)),
            TimeSpan.FromDays(Math.Clamp(Settings.MaxDaysWithoutSale, 1, 365)),
            [.. Settings.Categories], [.. Settings.Items], [.. Settings.Ignored], DataLanguage);

        _scanQueued = false;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;

        int generation;
        lock (_scanLock)
        {
            generation = ++_scanGeneration;
            _status = _status with { Scanning = true, Done = 0, Total = 0, Error = SniperError.None };
        }

        _ = Task.Run(() => ScanAsync(request, generation, token), token);
    }

    private async Task ScanAsync(ScanRequest request, int generation, CancellationToken token)
    {
        try
        {
            var catalog = GetCatalog(request.Language);
            var ids = catalog.Items.Values
                .Where(item => (item.Category != null && request.Categories.Contains(item.Category)) || request.Items.Contains(item.Id))
                .Where(item => !request.Ignored.Contains(item.Id))
                .Select(item => item.Id)
                .ToArray();
            var watched = ids.ToHashSet();

            // Deals of the previous scan stay listed until their item is looked at again, as long as it is still
            // watched: an item type or item taken out of the settings takes its deals with it.
            var previous = _deals.Where(deal => watched.Contains(deal.ItemId)).ToList();
            Publish(generation, [.. previous]);
            var scanned  = new HashSet<uint>();
            var deals    = new List<SniperDeal>();
            var done     = 0;
            var batches  = 0;
            var failed   = 0;
            Exception? lastFailure = null;
            foreach (var batch in ids.Chunk(UniversalisService.MarketBatchSize))
            {
                token.ThrowIfCancellationRequested();
                SetStatus(generation, status => status with { Done = done, Total = ids.Length });
                batches++;

                List<MarketSnapshot> market;
                try
                {
                    market = await UniversalisService.GetMarketAsync(request.Scope, request.HomeWorld, batch, request.WholeDataCenter, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && UniversalisService.IsTransient(ex))
                {
                    // One batch Universalis cannot answer must not stop the rest: the scan goes on without it,
                    // and its items keep the deals of the previous scan until they are looked at again.
                    failed++;
                    lastFailure = ex;
                    done += batch.Length;
                    continue;
                }

                var found = deals.Count;
                foreach (var snapshot in market)
                {
                    if (catalog.Items.TryGetValue(snapshot.ItemId, out var item) && Evaluate(item, snapshot, request) is { } deal)
                        deals.Add(deal);
                }

                done += batch.Length;
                token.ThrowIfCancellationRequested();

                // Publish as soon as this batch changes the list: a new deal, or an old one that is gone.
                scanned.UnionWith(batch);
                if (deals.Count > found || previous.Any(deal => batch.Contains(deal.ItemId)))
                    Publish(generation, [.. deals, .. previous.Where(deal => !scanned.Contains(deal.ItemId))]);
            }

            token.ThrowIfCancellationRequested();

            // Not a single batch answered: Universalis is down or overloaded, handled as any failed scan.
            if (lastFailure is not null && failed == batches)
                throw lastFailure;

            Publish(generation, [.. deals, .. previous.Where(deal => !scanned.Contains(deal.ItemId))]);
            if (lastFailure is null)
            {
                SetStatus(generation, _ => new SniperStatus(false, done, ids.Length, DateTime.UtcNow, SniperError.None));
                return;
            }

            // Some batches were left out: the scan counts, and the next one comes as soon as after a failure.
            Svc.Log.Warning(lastFailure, "{tweak}: market scan finished without {failed} of {batches} batches", Id, failed, batches);
            SetNextScan(generation, DateTime.UtcNow + RetryDelay);
            SetStatus(generation, _ => new SniperStatus(false, done, ids.Length, DateTime.UtcNow, ErrorOf(lastFailure)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancelled by Disable or by a newer scan request, which owns the status now.
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "{tweak}: market scan failed", Id);
            if (token.IsCancellationRequested) return;

            // Usually Universalis being overloaded: keep what is listed so far and try again soon.
            SetNextScan(generation, DateTime.UtcNow + RetryDelay);
            SetStatus(generation, status => status with { Scanning = false, Error = ErrorOf(ex) });
        }
    }

    private static SniperError ErrorOf(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: not null } => SniperError.Busy,
        TaskCanceledException                         => SniperError.Timeout,
        _                                             => SniperError.Offline,
    };

    /// <summary>Replaces the list read by the window and the Server Info Bar, unless a newer scan took over.</summary>
    private void Publish(int generation, List<SniperDeal> deals)
    {
        deals.Sort((a, b) => b.Discount.CompareTo(a.Discount));
        lock (_scanLock)
        {
            if (generation == _scanGeneration) _deals = deals;
        }
    }

    private static SniperDeal? Evaluate(SniperItem item, MarketSnapshot market, ScanRequest request)
    {
        if (market.Listings.Count == 0) return null;

        var cheapest = market.Listings[0];
        if (cheapest.Reviewed != DateTime.MinValue && DateTime.UtcNow - cheapest.Reviewed > request.MaxAge) return null;

        // Normal price of the same quality, or of the other one when that quality never sold.
        var sales   = market.Sales;
        var average = cheapest.Hq ? sales.AverageHq : sales.AverageNq;
        if (average <= 0) average = cheapest.Hq ? sales.AverageNq : sales.AverageHq;
        if (average <= 0 || average < request.MinAveragePrice) return null;

        // Universalis does not say how long a listing has been up, so "stuck on the market" is
        // judged by the item itself: no sale at all in the scanned area for too long.
        if (sales.LastSale != DateTime.MinValue && DateTime.UtcNow - sales.LastSale > request.MaxTimeWithoutSale) return null;

        // Resale happens on the home world. Without sales there, assume an even share of the data center's.
        var salesPerDay = sales.WorldSalesPerDay > 0 ? sales.WorldSalesPerDay : sales.DataCenterSalesPerDay / request.WorldsInDataCenter;

        var keep = 100 - request.Discount;
        if (cheapest.Price * 100 > average * keep) return null;

        // It must also undercut the other sellers: if everyone lists low, the price just dropped.
        var competitor = market.Listings.Skip(1).FirstOrDefault(listing => listing.Retainer != cheapest.Retainer || listing.World != cheapest.World);
        if (competitor != null && cheapest.Price * 100 > competitor.Price * keep) return null;

        return new SniperDeal(item.Id, item.Name, item.IconId, item.ItemLevel, item.Category,
            average, cheapest.Price, competitor?.Price ?? 0, cheapest.Quantity,
            cheapest.World ?? request.HomeWorld, cheapest.Hq, cheapest.Reviewed, salesPerDay);
    }

    /// <summary>Indexes every item that can be sold on the market board. Runs on a background thread.</summary>
    private Catalog GetCatalog(ClientLanguage language)
    {
        var catalog = _catalog;
        if (catalog == null || catalog.Language != language)
            _catalog = catalog = BuildCatalog(language);

        return catalog;
    }

    private static Catalog BuildCatalog(ClientLanguage language)
    {
        var items = new Dictionary<uint, SniperItem>();

        foreach (var item in Svc.Data.GetExcelSheet<LuminaItem>(language))
        {
            if (item.ItemSearchCategory.RowId == 0) continue;

            var name = item.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            string? category = null;
            foreach (var (key, matches) in Categories)
            {
                if (!matches(item)) continue;
                category = key;
                break;
            }

            items[item.RowId] = new SniperItem(item.RowId, name, item.Icon, (int)item.LevelItem.RowId, category);
        }

        return new Catalog(language, items);
    }

    private void EnsureCatalog()
    {
        var language = DataLanguage;
        if (_catalog?.Language == language || Interlocked.Exchange(ref _buildingCatalog, 1) == 1) return;

        Task.Run(() =>
        {
            try   { GetCatalog(language); }
            catch (Exception ex) { Svc.Log.Warning(ex, "{tweak}: could not index items", Id); }
            finally { Volatile.Write(ref _buildingCatalog, 0); }
        });
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    public override TweakButton? OpenButton => new(TMain("Open"), TMain("Open.Help"), () => _addon?.Toggle());

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelp);

    protected override IReadOnlyList<string> CommandNames => ["/gsn", "/gsnipe"];

    protected override void OnCommand() => _addon?.Toggle();

    public override IReadOnlyList<TweakButton> Buttons => [new(Loc.Get("Window.Settings"), TMain("Settings.Help"), OpenSettings)];

    // ── Access for the native settings window ─────────────────────────────────

    internal Options Current => Settings;

    internal void SaveCurrent() => SaveSettings();

    /// <summary>True once the item names exist in the language in use (they load in the background).</summary>
    internal bool CatalogReady()
    {
        EnsureCatalog();
        return _catalog is { } catalog && catalog.Language == DataLanguage;
    }

    internal int CategoryCount(string key) => _catalog?.CategoryCounts.GetValueOrDefault(key) ?? 0;

    /// <summary>Items whose name contains the text, at most <see cref="MaxSearchResults"/>.</summary>
    internal List<(uint Id, string Name)> SearchItems(string term)
        => _catalog is not { } catalog || catalog.Language != DataLanguage
            ? []
            : catalog.ByName.Where(item => item.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSearchResults).Select(item => (item.Id, item.Name)).ToList();

    internal string ItemName(uint id)
        => _catalog is { } catalog && catalog.Items.TryGetValue(id, out var item) ? item.Name : $"#{id}";
}
