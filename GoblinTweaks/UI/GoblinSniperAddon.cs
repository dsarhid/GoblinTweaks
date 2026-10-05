using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native window of the GoblinSniper tweak: a searchable, sortable list of the listings found far
/// below their normal price, with scan status on top and help in the bottom-left corner.
/// </summary>
internal unsafe class GoblinSniperAddon : NativeAddon
{
    private static readonly Vector4 TitleGold = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 BodyGrey  = new(204 / 255f, 204 / 255f, 204 / 255f, 1f);
    private static readonly Vector4 MutedGrey = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Vector4 ErrorRed  = new(0.92f, 0.42f, 0.38f, 1f);

    private const float TopBarH    = 38f;
    private const float HeaderH    = 22f;
    private const float BottomBarH = 34f;
    private const float ScrollBarW = 18f;

    private const string SortDiscount = "Discount";
    private const string SortProfit   = "Profit";
    private const string SortPrice    = "Price";
    private const string SortAverage  = "Average";
    private const string SortLevel    = "Level";
    private const string SortName     = "Name";
    private const string SortType     = "Type";
    private const string SortResale   = "Resale";
    private const string SortWorld    = "World";
    private const string SortUpdated  = "Updated";

    private const string AllWorlds = "";
    private const string AnyAge    = "0";
    private static readonly List<string> AgeOptions = [AnyAge, "1", "3", "6", "12"]; // hours

    /// <summary>Help topics, in order; each has a "Help.{topic}.Title" and "Help.{topic}.Text".</summary>
    private static readonly string[] HelpTopics = ["About", "Columns", "Resale", "Sorting", "Data", "Scanning", "Items"];

    private TextInputNode? _searchInput;
    private StringDropDownNode? _sortDropdown;
    private StringDropDownNode? _worldDropdown;
    private StringDropDownNode? _ageDropdown;
    private TextNode? _status;
    private TextNode? _count;
    private CircleButtonNode? _clearSearchButton;
    private CircleButtonNode? _clearFiltersButton;
    private CircleButtonNode? _refreshButton;
    private CircleButtonNode? _settingsButton;
    private CircleButtonNode? _helpButton;
    private ListNode<SniperDeal, SniperRowNode>? _list;
    private TextHelpAddon? _helpAddon;

    private IReadOnlyList<SniperDeal>? _shownDeals;
    private string _searchText = string.Empty;
    private string _sortMode = SortDiscount;
    private string _worldFilter = AllWorlds;
    private string _ageFilter = AnyAge;
    private List<string> _worlds = [];
    private bool _pendingFilter;
    private bool _resetScroll;
    private int _collisionRebuildFrames;
    private DateTime _nextStatus = DateTime.MinValue;
    private string _notice = string.Empty;
    private DateTime _noticeUntil = DateTime.MinValue;

    public required GoblinSniper Tweak { get; init; }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        SniperRowNode.SubText    = TypeLabel;
        SniperRowNode.NextFormat   = Tweak.Text("Row.Next");
        SniperRowNode.PerDayFormat = Tweak.Text("Row.PerDay");

        var c  = ContentStartPosition;
        var cs = ContentSize;

        BuildTopBar(c, new Vector2(cs.X, TopBarH));
        BuildHeader(new Vector2(c.X, c.Y + TopBarH), cs.X - ScrollBarW);

        var listY = c.Y + TopBarH + HeaderH;
        _list = new ListNode<SniperDeal, SniperRowNode>
        {
            Position                 = new Vector2(c.X, listY),
            Size                     = new Vector2(cs.X, cs.Y - TopBarH - HeaderH - BottomBarH),
            ItemSpacing              = 2f,
            ShowNoResultsPlaceholder = true,
            AllowMultipleSelection   = false,
            OptionsList              = [],
        };
        _list.OnItemSelected = deal => { if (deal is not null) CopyName(deal); };
        _list.AttachNode(this);

        BuildBottomBar(new Vector2(c.X, c.Y + cs.Y - BottomBarH), new Vector2(cs.X, BottomBarH));

        _shownDeals    = null;
        _pendingFilter = true;
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        // A finished scan publishes a new list instance.
        if (!ReferenceEquals(_shownDeals, Tweak.Deals))
            _pendingFilter = true;

        if (_pendingFilter) ApplyFilter();

        _list?.Update();

        // The ListNode only rebuilds the addon's collision node list on scroll, so freshly
        // recycled rows lose hover + click after a repopulation. Rebuild it for a couple frames.
        if (_collisionRebuildFrames > 0)
        {
            _collisionRebuildFrames--;
            if (InternalAddon is not null)
                InternalAddon->UpdateCollisionNodeList(false);
        }

        if (DateTime.UtcNow >= _nextStatus)
        {
            _nextStatus = DateTime.UtcNow.AddSeconds(1);
            UpdateStatus();
        }
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _searchInput    = null;
        _sortDropdown   = null;
        _worldDropdown  = null;
        _ageDropdown    = null;
        _status         = null;
        _count          = null;
        _clearSearchButton  = null;
        _clearFiltersButton = null;
        _refreshButton  = null;
        _settingsButton = null;
        _helpButton     = null;
        _list           = null;
        _shownDeals     = null;

        _helpAddon?.Close();
        _helpAddon = null;

        SniperRowNode.SubText = null;

        base.OnFinalize(addon);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildTopBar(Vector2 pos, Vector2 size)
    {
        var cy = pos.Y + (size.Y - 26f) / 2f;

        _searchInput = new TextInputNode
        {
            Position          = new Vector2(pos.X + 4f, cy - 1f),
            Size              = new Vector2(170f, 28f),
            PlaceholderString = Tweak.Text("Window.Search"),
            MaxCharacters     = 64,
        };
        _searchInput.OnInputReceived = str => { _searchText = str.ToString(); FilterChanged(); };
        _searchInput.OnInputComplete = str => { _searchText = str.ToString(); FilterChanged(); };
        _searchInput.AttachNode(this);

        _clearSearchButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.CrossSmall,
            Position    = new Vector2(pos.X + 176f, cy + 2f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Window.ClearSearch"),
        };
        _clearSearchButton.OnClick = ClearSearch;
        _clearSearchButton.AttachNode(this);

        new TextNode
        {
            String        = Tweak.Text("Window.Sort"),
            Position      = new Vector2(pos.X + 200f, cy + 6f),
            Size          = new Vector2(78f, 16f),
            TextColor     = TitleGold,
            FontSize      = 12,
            AlignmentType = AlignmentType.Right,
        }.AttachNode(this);

        // The label function goes first: setting the selected option renders its label straight away.
        _sortDropdown = new StringDropDownNode
        {
            GetLabelFunction = key => Tweak.Text($"Sort.{key}"),
            Position         = new Vector2(pos.X + 284f, cy + 1f),
            Size             = new Vector2(184f, 24f),
            MaxListOptions   = 10,
            Options          = [SortDiscount, SortProfit, SortResale, SortPrice, SortAverage, SortLevel, SortName, SortType, SortWorld, SortUpdated],
            SelectedOption   = _sortMode,
        };
        _sortDropdown.OnOptionSelected = mode => { _sortMode = mode; FilterChanged(); };
        _sortDropdown.AttachNode(this);

        // Filter by world: its options are the worlds that have a deal right now.
        _worldDropdown = new StringDropDownNode
        {
            GetLabelFunction = world => world == AllWorlds ? Tweak.Text("Filter.AllWorlds") : world,
            Position         = new Vector2(pos.X + 474f, cy + 1f),
            Size             = new Vector2(150f, 24f),
            MaxListOptions   = 10,
            Options          = [AllWorlds, .. _worlds],
            SelectedOption   = _worldFilter,
        };
        _worldDropdown.OnOptionSelected = world => { _worldFilter = world; FilterChanged(); };
        _worldDropdown.AttachNode(this);

        // Filter by how recently the price was uploaded.
        _ageDropdown = new StringDropDownNode
        {
            GetLabelFunction = hours => hours == AnyAge ? Tweak.Text("Filter.AnyAge") : string.Format(Tweak.Text("Filter.Age"), hours),
            Position         = new Vector2(pos.X + 630f, cy + 1f),
            Size             = new Vector2(150f, 24f),
            MaxListOptions   = 5,
            Options          = AgeOptions,
            SelectedOption   = _ageFilter,
        };
        _ageDropdown.OnOptionSelected = hours => { _ageFilter = hours; FilterChanged(); };
        _ageDropdown.AttachNode(this);

        _clearFiltersButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.Undo,
            Position    = new Vector2(pos.X + 784f, cy + 2f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Window.ClearFilters"),
        };
        _clearFiltersButton.OnClick = ClearFilters;
        _clearFiltersButton.AttachNode(this);

        _refreshButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.Refresh,
            Position    = new Vector2(pos.X + size.X - 32f, cy),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Tweak.Text("Window.Refresh"),
        };
        _refreshButton.OnClick = RequestScan;
        _refreshButton.AttachNode(this);

        _settingsButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = new Vector2(pos.X + size.X - 64f, cy),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Tweak.Text("Settings"),
        };
        _settingsButton.OnClick = Tweak.OpenSettings;
        _settingsButton.AttachNode(this);
    }

    private void BuildHeader(Vector2 pos, float width)
    {
        var columns = SniperColumns.For(width);

        HeaderLabel(pos, columns.NameX, columns.NameW, "Column.Item", AlignmentType.Left);
        HeaderLabel(pos, columns.LevelX, SniperColumns.LevelW, "Column.Level", AlignmentType.Right);
        HeaderLabel(pos, columns.AverageX, SniperColumns.PriceW, "Column.Average", AlignmentType.Right);
        HeaderLabel(pos, columns.PriceX, SniperColumns.PriceW, "Column.Price", AlignmentType.Right);
        HeaderLabel(pos, columns.DiscountX, SniperColumns.DiscountW, "Column.Discount", AlignmentType.Right);
        HeaderLabel(pos, columns.ResaleX, SniperColumns.ResaleW, "Column.Resale", AlignmentType.Right);
        HeaderLabel(pos, columns.WorldX, SniperColumns.WorldW, "Column.World", AlignmentType.Left);
        HeaderLabel(pos, columns.AgeX, SniperColumns.AgeW, "Column.Age", AlignmentType.Right);

        new HorizontalLineNode
        {
            Position = new Vector2(pos.X, pos.Y + HeaderH - 3f),
            Size     = new Vector2(width + ScrollBarW, 2f),
        }.AttachNode(this);
    }

    private void HeaderLabel(Vector2 pos, float x, float width, string key, AlignmentType alignment)
    {
        new TextNode
        {
            String        = Tweak.Text(key),
            Position      = new Vector2(pos.X + x, pos.Y + 2f),
            Size          = new Vector2(width, 14f),
            TextColor     = TitleGold,
            FontSize      = 12,
            AlignmentType = alignment,
        }.AttachNode(this);
    }

    private void BuildBottomBar(Vector2 pos, Vector2 size)
    {
        new HorizontalLineNode
        {
            Position = new Vector2(pos.X, pos.Y),
            Size     = new Vector2(size.X, 2f),
        }.AttachNode(this);

        _helpButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(pos.X + 4f, pos.Y + (size.Y - 22f) / 2f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Help.Title"),
        };
        _helpButton.OnClick = OpenHelp;
        _helpButton.AttachNode(this);

        // Scan status, next to the help button; it has the whole bar up to the counter.
        _status = new TextNode
        {
            Position  = new Vector2(pos.X + 34f, pos.Y + (size.Y - 16f) / 2f),
            Size      = new Vector2(Math.Max(0f, size.X - 34f - 236f), 16f),
            TextColor = MutedGrey,
            FontSize  = 12,
        };
        _status.AddTextFlags(TextFlags.Ellipsis);
        _status.AttachNode(this);

        _count = new TextNode
        {
            Position      = new Vector2(pos.X + size.X - 226f, pos.Y + (size.Y - 16f) / 2f),
            Size          = new Vector2(220f, 16f),
            TextColor     = BodyGrey,
            FontSize      = 12,
            AlignmentType = AlignmentType.Right,
        };
        _count.AttachNode(this);
    }

    private void OpenHelp()
    {
        _helpAddon ??= new TextHelpAddon
        {
            InternalName = "GtkGoblinSniperHelp",
            Title        = Tweak.Text("Help.Title"),
            Size         = new Vector2(860f, 600f),
            Pages        = [.. HelpTopics.Select(topic => (Tweak.Text($"Help.{topic}.Title"), Tweak.Text($"Help.{topic}.Text")))],
        };
        _helpAddon.Open();
    }

    // ── Data ──────────────────────────────────────────────────────────────────

    /// <summary>Localized item type shown under the name; items watched individually have their own label.</summary>
    private string TypeLabel(SniperDeal deal) => Tweak.Text(deal.Category is null ? "Category.Item" : $"Category.{deal.Category}");

    /// <summary>The user changed search, sort or a filter: rebuild the list from the top.</summary>
    private void FilterChanged()
    {
        _pendingFilter = true;
        _resetScroll   = true;
    }

    private void ClearSearch()
    {
        _searchText = string.Empty;
        if (_searchInput is not null)
            _searchInput.String = string.Empty;

        FilterChanged();
    }

    /// <summary>Back to every world and any data age. The sort order is not a filter and stays.</summary>
    private void ClearFilters()
    {
        _worldFilter = AllWorlds;
        _ageFilter   = AnyAge;
        if (_worldDropdown is not null) _worldDropdown.SelectedOption = _worldFilter;
        if (_ageDropdown is not null)   _ageDropdown.SelectedOption   = _ageFilter;

        FilterChanged();
    }

    /// <summary>Keeps the world filter options in step with the worlds that currently have a deal.</summary>
    private void UpdateWorlds(IReadOnlyList<SniperDeal> deals)
    {
        var worlds = deals.Select(deal => deal.World).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (worlds.SequenceEqual(_worlds)) return;

        _worlds = worlds;
        if (_worldFilter != AllWorlds && !worlds.Contains(_worldFilter))
            _worldFilter = AllWorlds;

        if (_worldDropdown is not null)
        {
            _worldDropdown.Options        = [AllWorlds, .. worlds];
            _worldDropdown.SelectedOption = _worldFilter;
        }
    }

    private void ApplyFilter()
    {
        _pendingFilter = false;
        _shownDeals    = Tweak.Deals;
        UpdateWorlds(_shownDeals);

        // Everything listed here has now been seen: the Server Info Bar tooltip stops calling it new.
        Tweak.MarkAllSeen();

        IEnumerable<SniperDeal> deals = _shownDeals;

        var term = _searchText.Trim();
        if (term.Length > 0)
            deals = deals.Where(deal => deal.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || deal.World.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (_worldFilter != AllWorlds)
            deals = deals.Where(deal => deal.World == _worldFilter);

        if (_ageFilter != AnyAge)
        {
            var oldest = DateTime.UtcNow.AddHours(-int.Parse(_ageFilter));
            deals = deals.Where(deal => deal.Reviewed >= oldest);
        }

        deals = _sortMode switch
        {
            SortProfit  => deals.OrderByDescending(deal => deal.Profit),
            SortResale  => deals.OrderByDescending(deal => deal.SalesPerDay).ThenByDescending(deal => deal.Discount),
            SortPrice   => deals.OrderBy(deal => deal.Price),
            SortAverage => deals.OrderByDescending(deal => deal.AveragePrice),
            SortLevel   => deals.OrderByDescending(deal => deal.ItemLevel).ThenBy(deal => deal.Name),
            SortName    => deals.OrderBy(deal => deal.Name, StringComparer.OrdinalIgnoreCase),
            SortType    => deals.OrderBy(TypeLabel, StringComparer.OrdinalIgnoreCase).ThenByDescending(deal => deal.Discount),
            SortWorld   => deals.OrderBy(deal => deal.World, StringComparer.OrdinalIgnoreCase).ThenByDescending(deal => deal.Discount),
            SortUpdated => deals.OrderByDescending(deal => deal.Reviewed).ThenByDescending(deal => deal.Discount),
            _           => deals.OrderByDescending(deal => deal.Discount).ThenByDescending(deal => deal.Profit),
        };

        // A scan adding deals must not throw the list back to the top; a change made by the user does.
        var filtered = deals.ToList();
        if (_list is not null)
        {
            _list.AutoResetScroll = _resetScroll;
            _list.OptionsList     = filtered;
        }

        _resetScroll = false;

        if (_count is not null)
            _count.String = string.Format(Tweak.Text("Window.Count"), filtered.Count, _shownDeals.Count);

        _collisionRebuildFrames = 3;
    }

    private void RequestScan()
    {
        // The button stays locked until this scan ends, so repeated clicks can't restart it over and over.
        if (Tweak.ScanPending) return;

        Tweak.RequestScan();
        _nextStatus = DateTime.MinValue;
    }

    private void CopyName(SniperDeal deal)
    {
        ImGui.SetClipboardText(deal.Name);
        _notice      = string.Format(Tweak.Text("Window.Copied"), deal.Name);
        _noticeUntil = DateTime.UtcNow.AddSeconds(4);
        _nextStatus  = DateTime.MinValue;
    }

    private void UpdateStatus()
    {
        var pending = Tweak.ScanPending;
        if (_refreshButton is not null)
        {
            _refreshButton.IsEnabled   = !pending;
            _refreshButton.TextTooltip = Tweak.Text(pending ? "Window.Refresh.Busy" : "Window.Refresh");
        }

        if (_status is null) return;

        if (DateTime.UtcNow < _noticeUntil)
        {
            _status.String    = _notice;
            _status.TextColor = TitleGold;
            return;
        }

        var status = Tweak.Status;
        var failed = status.Error != SniperError.None && !pending;
        _status.TextColor = failed ? ErrorRed : MutedGrey;

        if (status.Scanning && status.Total > 0)
            _status.String = string.Format(Tweak.Text("Window.Scanning"), status.Done, status.Total);
        else if (pending)
            _status.String = Tweak.Text("Window.Starting");
        else if (failed)
            _status.String = string.Format(Tweak.Text($"Window.Error.{status.Error}"), Math.Max(1, Minutes(Tweak.NextScan - DateTime.UtcNow)));
        else if (status.LastScan == DateTime.MinValue)
            _status.String = Tweak.Text("Window.Waiting");
        else
            _status.String = string.Format(Tweak.Text("Window.Scanned"),
                status.Total, Minutes(DateTime.UtcNow - status.LastScan), Minutes(Tweak.NextScan - DateTime.UtcNow));
    }

    private static int Minutes(TimeSpan span) => Math.Max(0, (int)Math.Round(span.TotalMinutes));
}
