using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
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
/// finds is shown in the Server Info Bar; clicking it opens a native window with the list.
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

        /// <summary>Game-data language for item names ("" = follow GoblinTweaks). Independent of the UI language.</summary>
        public string DataLanguage { get; set; } = string.Empty;
    }

    // Language choices for the item names. "" follows the GoblinTweaks language.
    private static readonly (string Code, string Label)[] DataLanguages =
    [
        ("",   ""),          // label resolved from Loc at draw time
        ("en", "English"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("ja", "日本語"),
    ];

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

    private sealed record ScanRequest(string Scope, string HomeWorld, int WorldsInDataCenter, bool WholeDataCenter, int Discount, int MinAveragePrice, TimeSpan MaxAge, TimeSpan MaxTimeWithoutSale, HashSet<string> Categories, HashSet<uint> Items, ClientLanguage Language);

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
    private GoblinSniperSettingsAddon? _settingsAddon;
    private GoblinSniperAddon? _addon;
    private IDtrBarEntry? _entry;
    private CancellationTokenSource? _cancel;
    private DateTime _nextPoll = DateTime.MinValue;
    private DateTime _nextScan = DateTime.MinValue;
    private int _buildingCatalog;
    private bool _scanQueued;

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

    /// <summary>Game-data language used for item names.</summary>
    public ClientLanguage DataLanguage => (string.IsNullOrEmpty(Settings.DataLanguage) ? Loc.CurrentLanguage : Settings.DataLanguage) switch
    {
        "de" => ClientLanguage.German,
        "fr" => ClientLanguage.French,
        "ja" => ClientLanguage.Japanese,
        _    => ClientLanguage.English, // "en", "es" and anything without game data
    };

    public string SettingsTitle => $"{Name} — {T("Settings")}";

    /// <summary>Localized text of this tweak, for the native window.</summary>
    internal string Text(string key) => T(key);

    protected internal override void Enable()
    {
        _deals = [];
        _status = new SniperStatus(false, 0, 0, DateTime.MinValue, SniperError.None);
        _seen.Clear();
        _nextScan = DateTime.UtcNow + RescanDelay;
        _scanQueued = true;

        _addon = new GoblinSniperAddon
        {
            InternalName = "GtkGoblinSniper",
            Title        = Name,
            Size         = new System.Numerics.Vector2(990f, 620f),
            Tweak        = this,
        };

        _entry = Svc.DtrBar.Get(Name);
        _entry.Shown = false;
        _entry.OnClick = _ => _addon?.Open();

        _settingsAddon = new GoblinSniperSettingsAddon
        {
            InternalName    = "GtkGoblinSniperSettings",
            Title           = SettingsTitle,
            Size            = new System.Numerics.Vector2(560f, 700f),
            Tweak           = this,
            RespectCloseAll = false,
        };

        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        CancelScan();

        _settingsAddon?.Close();
        _settingsAddon = null;

        _addon?.Close();
        _addon = null;
        _entry?.Remove();
        _entry = null;

        _deals = [];
        _catalog = null;
        _seen.Clear();
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
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
        _status = _status with { Scanning = false };
    }

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < _nextPoll) return;
        _nextPoll = DateTime.UtcNow.AddSeconds(1);

        try
        {
            UpdateEntry();
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

        _entry.Shown = Settings.Notify;
        if (!Settings.Notify) return;

        var deals  = _deals;
        var unseen = deals.Count(deal => !_seen.Contains(deal.Key));
        _entry.Text = new SeStringBuilder().AddText($"{SeIconChar.BoxedStar.ToIconString()} {deals.Count}").Build();

        var tip = new StringBuilder();
        tip.AppendLine(string.Format(T("DtrTooltip"), deals.Count, unseen));
        foreach (var deal in deals.Take(5))
            tip.AppendLine($"  -{deal.Discount}%  {deal.Name}  {deal.Price:N0} ({deal.World})");
        tip.Append(T("DtrClick"));

        _entry.Tooltip = new SeStringBuilder().AddText(tip.ToString()).Build();
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
            [.. Settings.Categories], [.. Settings.Items], DataLanguage);

        _scanQueued = false;
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        _status = _status with { Scanning = true, Done = 0, Total = 0, Error = SniperError.None };
        _ = Task.Run(() => ScanAsync(request, token), token);
    }

    private async Task ScanAsync(ScanRequest request, CancellationToken token)
    {
        try
        {
            var catalog = GetCatalog(request.Language);
            var ids = catalog.Items.Values
                .Where(item => (item.Category != null && request.Categories.Contains(item.Category)) || request.Items.Contains(item.Id))
                .Select(item => item.Id)
                .ToArray();

            // Deals of the previous scan stay listed until their item is looked at again.
            var previous = _deals;
            var scanned  = new HashSet<uint>();
            var deals    = new List<SniperDeal>();
            var done     = 0;
            foreach (var batch in ids.Chunk(UniversalisService.MarketBatchSize))
            {
                token.ThrowIfCancellationRequested();
                _status = _status with { Done = done, Total = ids.Length };

                var found = deals.Count;
                foreach (var snapshot in await UniversalisService.GetMarketAsync(request.Scope, request.HomeWorld, batch, request.WholeDataCenter, token).ConfigureAwait(false))
                {
                    if (catalog.Items.TryGetValue(snapshot.ItemId, out var item) && Evaluate(item, snapshot, request) is { } deal)
                        deals.Add(deal);
                }

                done += batch.Length;
                token.ThrowIfCancellationRequested();

                // Publish as soon as this batch changes the list: a new deal, or an old one that is gone.
                scanned.UnionWith(batch);
                if (deals.Count > found || previous.Any(deal => batch.Contains(deal.ItemId)))
                    Publish([.. deals, .. previous.Where(deal => !scanned.Contains(deal.ItemId))]);
            }

            token.ThrowIfCancellationRequested();
            Publish(deals);
            _status = new SniperStatus(false, done, ids.Length, DateTime.UtcNow, SniperError.None);
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
            _nextScan = DateTime.UtcNow + RetryDelay;
            _status = _status with
            {
                Scanning = false,
                Error = ex switch
                {
                    HttpRequestException { StatusCode: not null } => SniperError.Busy,
                    TaskCanceledException                         => SniperError.Timeout,
                    _                                             => SniperError.Offline,
                },
            };
        }
    }

    /// <summary>Replaces the list read by the window and the Server Info Bar.</summary>
    private void Publish(List<SniperDeal> deals)
    {
        deals.Sort((a, b) => b.Discount.CompareTo(a.Discount));
        _deals = deals;
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

    public override void DrawSettings()
    {
        // Open the native window (icon hints that a separate window opens).
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ExternalLinkAlt, T("Open")))
            _addon?.Open();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("Open.Help"));

        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Cog, T("Settings")))
            OpenSettings();
    }

    // ── Access for the native settings window ─────────────────────────────────

    internal Options Current => Settings;

    internal void SaveCurrent() => SaveSettings();

    /// <summary>Language choices for the item names; the first follows the GoblinTweaks language.</summary>
    internal IReadOnlyList<(string Code, string Text)> LanguageChoices
        => DataLanguages.Select(l => (l.Code, l.Code.Length == 0 ? T("Language.Same") : l.Label)).ToList();

    /// <summary>Changes the language of the item names and starts over, since the names already listed are in the old one.</summary>
    internal void SetDataLanguage(string code)
    {
        if (code == Settings.DataLanguage) return;

        Settings.DataLanguage = code;
        _deals = [];
        SaveSettings();
    }

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
