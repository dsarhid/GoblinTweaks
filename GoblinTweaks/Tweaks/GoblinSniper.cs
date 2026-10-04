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
public sealed record SniperDeal(
    uint ItemId, string Name, uint IconId, int ItemLevel, string? Category,
    long AveragePrice, long Price, long NextPrice, int Quantity, string World, bool Hq, DateTime Reviewed)
{
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

    private sealed record ScanRequest(string Scope, string HomeWorld, bool WholeDataCenter, int Discount, int MinAveragePrice, TimeSpan MaxAge, HashSet<string> Categories, HashSet<uint> Items, ClientLanguage Language);

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

    private readonly WindowSystem _settingsWindows = new("GoblinTweaks.SniperSettings");
    private readonly HashSet<string> _seen = [];
    private GoblinSniperSettingsWindow? _settingsWindow;
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
    private string _search = string.Empty;
    private string _searchedFor = string.Empty;
    private List<SniperItem> _searchResults = [];

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
            Size         = new System.Numerics.Vector2(920f, 620f),
            Tweak        = this,
        };

        _entry = Svc.DtrBar.Get(Name);
        _entry.Shown = false;
        _entry.OnClick = _ => _addon?.Open();

        _settingsWindow = new GoblinSniperSettingsWindow(this);
        _settingsWindows.AddWindow(_settingsWindow);
        Svc.PluginInterface.UiBuilder.Draw += _settingsWindows.Draw;

        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        CancelScan();

        Svc.PluginInterface.UiBuilder.Draw -= _settingsWindows.Draw;
        _settingsWindows.RemoveAllWindows();
        _settingsWindow = null;

        _addon?.Close();
        _addon = null;
        _entry?.Remove();
        _entry = null;

        _deals = [];
        _catalog = null;
        _searchResults = [];
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
        if (_settingsWindow is not null)
            _settingsWindow.IsOpen = true;
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

        var request = new ScanRequest(scope, homeWorld, Settings.WholeDataCenter, Math.Clamp(Settings.DiscountPercent, 1, 99), Settings.MinAveragePrice,
            TimeSpan.FromHours(Math.Clamp(Settings.MaxAgeHours, 1, 72)),
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
                foreach (var snapshot in await UniversalisService.GetMarketAsync(request.Scope, batch, request.WholeDataCenter, token).ConfigureAwait(false))
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
        var average = cheapest.Hq ? market.AverageHq : market.AverageNq;
        if (average <= 0) average = cheapest.Hq ? market.AverageNq : market.AverageHq;
        if (average <= 0 || average < request.MinAveragePrice) return null;

        var keep = 100 - request.Discount;
        if (cheapest.Price * 100 > average * keep) return null;

        // It must also undercut the other sellers: if everyone lists low, the price just dropped.
        var competitor = market.Listings.Skip(1).FirstOrDefault(listing => listing.Retainer != cheapest.Retainer || listing.World != cheapest.World);
        if (competitor != null && cheapest.Price * 100 > competitor.Price * keep) return null;

        return new SniperDeal(item.Id, item.Name, item.IconId, item.ItemLevel, item.Category,
            average, cheapest.Price, competitor?.Price ?? 0, cheapest.Quantity,
            cheapest.World ?? request.HomeWorld, cheapest.Hq, cheapest.Reviewed);
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

    /// <summary>Full tweak configuration, drawn in its own window (opened from the Settings button).</summary>
    public void DrawConfigContents()
    {
        EnsureCatalog();
        var catalog = _catalog;
        if (catalog?.Language != DataLanguage)
            catalog = null; // still loading the names in the newly chosen language
        var scale = ImGuiHelpers.GlobalScale;

        DrawLanguagePicker();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var notify = Settings.Notify;
        if (Widgets.SettingToggle(T("Notify"), T("Notify.Help"), ref notify))
        { Settings.Notify = notify; SaveSettings(); }

        var dataCenter = Settings.WholeDataCenter;
        if (Widgets.SettingToggle(T("WholeDataCenter"), T("WholeDataCenter.Help"), ref dataCenter))
        { Settings.WholeDataCenter = dataCenter; SaveSettings(); }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Palette.Accent, T("Rules"));
        ImGui.Spacing();

        // Sliders change the value while dragging and save (and re-scan) once on release.
        var discount = Settings.DiscountPercent;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.SliderInt(T("Discount"), ref discount, 10, 95, "%d%%"))
            Settings.DiscountPercent = discount;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveSettings();
        DrawHelp(T("Discount.Help"));

        var minPrice = Settings.MinAveragePrice;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.InputInt(T("MinPrice"), ref minPrice, 10_000, 100_000))
            Settings.MinAveragePrice = Math.Max(0, minPrice);
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveSettings();
        DrawHelp(T("MinPrice.Help"));

        var minutes = Settings.ScanMinutes;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.SliderInt(T("ScanMinutes"), ref minutes, 5, 60))
            Settings.ScanMinutes = minutes;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveSettings();

        var maxAge = Settings.MaxAgeHours;
        ImGui.SetNextItemWidth(220 * scale);
        if (ImGui.SliderInt(T("MaxAge"), ref maxAge, 1, 72, "%d h"))
            Settings.MaxAgeHours = maxAge;
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveSettings();
        DrawHelp(T("MaxAge.Help"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Palette.Accent, T("Types"));
        DrawHelp(T("Types.Help"));
        ImGui.Spacing();

        foreach (var (key, _) in Categories)
        {
            var watched = Settings.Categories.Contains(key);
            var label   = T($"Category.{key}");
            if (catalog != null)
                label += $" ({catalog.CategoryCounts.GetValueOrDefault(key)})";

            if (ImGui.Checkbox($"{label}##{key}", ref watched))
            {
                if (watched) Settings.Categories.Add(key);
                else Settings.Categories.Remove(key);
                SaveSettings();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Palette.Accent, T("Items"));
        DrawHelp(T("Items.Help"));
        ImGui.Spacing();

        if (catalog == null)
        {
            ImGui.TextColored(Palette.Muted, T("Loading"));
            return;
        }

        DrawItemSearch(catalog);
        DrawWatchedItems(catalog);
    }

    private void DrawLanguagePicker()
    {
        ImGui.TextColored(Palette.Accent, T("Language"));
        ImGui.Spacing();

        string Label(string code) => code.Length == 0
            ? T("Language.Same")
            : DataLanguages.First(l => l.Code == code).Label;

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##sniperlang", Label(Settings.DataLanguage)))
        {
            if (combo)
            {
                foreach (var (code, _) in DataLanguages)
                {
                    if (ImGui.Selectable(Label(code), code == Settings.DataLanguage) && code != Settings.DataLanguage)
                    {
                        Settings.DataLanguage = code;

                        // Names already listed are in the old language: start over.
                        _deals = [];
                        _searchedFor = string.Empty;
                        _searchResults = [];
                        SaveSettings();
                    }
                }
            }
        }

        DrawHelp(T("Language.Help"));
    }

    private void DrawItemSearch(Catalog catalog)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##snipersearch", T("Items.Search"), ref _search, 64);

        var term = _search.Trim();
        if (term != _searchedFor)
        {
            _searchedFor = term;
            _searchResults = term.Length < 2
                ? []
                : catalog.ByName.Where(item => item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)).Take(MaxSearchResults).ToList();
        }

        foreach (var item in _searchResults)
        {
            if (Settings.Items.Contains(item.Id)) continue;

            if (ImGui.Selectable($"+ {item.Name}##add{item.Id}"))
            {
                Settings.Items.Add(item.Id);
                SaveSettings();
                break;
            }
        }

        if (term.Length >= 2 && _searchResults.Count == 0)
            ImGui.TextColored(Palette.Muted, T("Items.NoResults"));
    }

    private void DrawWatchedItems(Catalog catalog)
    {
        ImGui.Spacing();
        if (Settings.Items.Count == 0)
        {
            ImGui.TextColored(Palette.Muted, T("Items.Empty"));
            return;
        }

        foreach (var id in Settings.Items)
        {
            if (ImGui.SmallButton($"x##remove{id}"))
            {
                Settings.Items.Remove(id);
                SaveSettings();
                break;
            }

            ImGui.SameLine();
            ImGui.Text(catalog.Items.TryGetValue(id, out var item) ? item.Name : $"#{id}");
        }
    }

    private static void DrawHelp(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Palette.Muted, text);
        ImGui.PopTextWrapPos();
    }
}
