using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native window of the achievements tweak, laid out like the game's own: tabs on top (list, almost done, recent,
/// tracked), a column of categories on the left, the achievements as cards in the middle and a panel on the right that
/// sums up how much is done, shows the selected one and has the tools for the pinned list. The bar at the bottom says
/// what is loaded and how many are shown.
/// </summary>
internal unsafe class AchievementsAddon : NativeAddon
{
    private static readonly Vector4 TitleGold = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 BodyGrey  = new(204 / 255f, 204 / 255f, 204 / 255f, 1f);
    private static readonly Vector4 MutedGrey = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Vector4 DoneGreen = new(0.55f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 NearGold  = new(0.95f, 0.82f, 0.28f, 1f);
    private static readonly Vector4 WarnRed   = new(0.92f, 0.42f, 0.38f, 1f);
    private static readonly Vector4 BarTrack  = new(0f, 0f, 0f, 0.45f);
    private static readonly Vector4 BarBlue   = new(0.30f, 0.55f, 0.85f, 0.9f);
    private static readonly Vector4 CardMuted = new(0.45f, 0.38f, 0.30f, 1f);
    private static readonly Vector4 CardGold  = new(0.62f, 0.45f, 0.08f, 1f);
    private static readonly Vector4 InkName    = new(0.17f, 0.11f, 0.06f, 1f);
    private static readonly Vector4 InkDone    = new(0.12f, 0.38f, 0.14f, 1f);
    private static readonly Vector4 InkText    = new(0.33f, 0.26f, 0.18f, 1f);
    private static readonly Vector4 InkMuted   = new(0.45f, 0.38f, 0.30f, 1f);
    private static readonly Vector4 CardCream  = new(0.93f, 0.88f, 0.77f, 1f);
    private static readonly Vector4 CardBorder = new(0.27f, 0.19f, 0.11f, 1f);
    private static readonly Vector4 PanelCategories = new(0.20f, 0.14f, 0.08f, 0.55f);
    private static readonly Vector4 PanelCards      = new(0.12f, 0.09f, 0.05f, 0.45f);
    private static readonly Vector4 PanelDetail     = new(0.20f, 0.14f, 0.08f, 0.45f);
    private static readonly Vector4 TotalsTrack     = new(0f, 0f, 0f, 0.45f);
    private static readonly Vector4 TotalsFill      = new(0.92f, 0.76f, 0.30f, 0.9f);

    private const float TabsH      = 34f;
    private const float FilterH    = 38f;
    private const float BottomBarH = 34f;
    private const float CategoryW  = 250f;
    private const float DetailW    = 300f;
    private const float Gap        = 6f;

    // The tabs.
    private const string TabList    = "List";
    private const string TabNear    = "Near";
    private const string TabRecent  = "Recent";
    private const string TabTracked = "Tracked";

    // The state filter, apart from the tabs.
    private const string StateAll     = "All";
    private const string StateMissing = "Missing";
    private const string StateDone    = "Done";
    private const string StateRecent  = "Recent";

    private const string AllCategories = "";
    private const string GroupPrefix   = "k:";

    private const string SortGame     = "Game";
    private const string SortName     = "Name";
    private const string SortPoints   = "Points";
    private const string SortCategory = "Category";
    private const string SortClosest  = "Closest";
    private const string SortNewest   = "Newest";
    private const string SortLeft     = "Left";

    private const string RewardAny   = "Any";
    private const string RewardTitle = "Title";
    private const string RewardItem  = "Item";
    private const string RewardNone  = "None";

    private static readonly string[] Tabs = [TabList, TabNear, TabRecent, TabTracked];

    private static readonly Vector4[] TabColors =
    [
        new(0.30f, 0.62f, 0.58f, 1f),
        new(0.34f, 0.52f, 0.84f, 1f),
        new(0.88f, 0.48f, 0.28f, 1f),
        new(0.92f, 0.76f, 0.30f, 1f),
    ];

    private TextInputNode? _searchInput;
    private StringDropDownNode? _stateDropdown;
    private StringDropDownNode? _sortDropdown;
    private StringDropDownNode? _rewardDropdown;
    private StringDropDownNode? _pointsDropdown;
    private TextNode? _status;
    private TextNode? _count;
    private TextNode? _totals;
    private ColorImageNode? _totalsBar;
    private CircleButtonNode? _clearSearchButton;
    private CircleButtonNode? _clearFiltersButton;
    private CircleButtonNode? _loadButton;
    private CircleButtonNode? _progressButton;
    private CircleButtonNode? _settingsButton;
    private CircleButtonNode? _helpButton;
    private ListNode<AchievementCard, AchievementCardNode>? _list;
    private ListNode<AchievementCategoryEntry, AchievementCategoryRowNode>? _categories;
    private readonly ColorImageNode?[] _tabStrips = new ColorImageNode?[4];
    private readonly TextButtonNode?[] _tabButtons = new TextButtonNode?[4];

    // The panel on the right.
    private TextNode? _sumDone;
    private TextNode? _sumPoints;
    private TextNode? _sumSession;
    private ColorImageNode? _sumBar;
    private IconImageNode? _selIcon;
    private TextNode? _selName;
    private TextNode? _selCategory;
    private TextNode? _selDescription;
    private TextNode? _selReward;
    private TextNode? _selState;
    private TextNode? _selWhen;
    private ColorImageNode? _selBar;
    private TextButtonNode? _trackButton;
    private TextButtonNode? _openButton;
    private readonly TextNode?[] _slotLabels = new TextNode?[GoblinAchievements.ListSlots];
    private float _barWidth;
    private float _tabsX;
    private float _tabsY;

    private readonly HashSet<string> _expanded = [];
    private AchievementInfo? _selected;
    private string _tab = TabList;
    private string _searchText = string.Empty;
    private string _categoryFilter = AllCategories;
    private string _stateFilter = StateAll;
    private string _sortMode = SortGame;
    private string _rewardFilter = RewardAny;
    private string _pointsFilter = "0";
    private bool _pendingFilter;
    private bool _resetScroll;
    private int _collisionRebuildFrames;
    private int _shownVersion = -1;
    private bool _shownLoaded;
    private DateTime _nextStatus = DateTime.MinValue;

    public required GoblinAchievements Tweak { get; init; }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        AchievementCardNode.Labels = new AchievementCardLabels(Tweak.Text("Row.Reward"), Tweak.Text("Row.DoneShort"), Tweak.Text("Row.Pending"),
            Tweak.Text("Row.TitleKind"), Tweak.Text("Row.ItemKind"));

        _expanded.Clear();
        foreach (var group in Tweak.Config.ExpandedGroups) _expanded.Add(group);

        var c  = ContentStartPosition;
        var cs = ContentSize;

        BuildTabs(c, cs.X);
        BuildFilterBar(new Vector2(c.X, c.Y + TabsH), new Vector2(cs.X, FilterH));

        var bodyY = c.Y + TabsH + FilterH;
        var bodyH = cs.Y - TabsH - FilterH - BottomBarH;
        var cardsW = cs.X - CategoryW - DetailW - Gap * 2f;

        // Warm panels behind the three columns, drawn first so everything else sits on top.
        Panel(new Vector2(c.X, bodyY), new Vector2(CategoryW, bodyH), PanelCategories);
        Panel(new Vector2(c.X + CategoryW + Gap, bodyY), new Vector2(cardsW, bodyH), PanelCards);
        Panel(new Vector2(c.X + CategoryW + cardsW + Gap * 2f, bodyY), new Vector2(DetailW, bodyH), PanelDetail);

        _categories = new ListNode<AchievementCategoryEntry, AchievementCategoryRowNode>
        {
            Position                 = new Vector2(c.X, bodyY),
            Size                     = new Vector2(CategoryW, bodyH),
            ItemSpacing              = 2f,
            ShowNoResultsPlaceholder = false,
            AllowMultipleSelection   = false,
            AutoResetScroll          = false,
            OptionsList              = [],
        };
        _categories.OnItemSelected = OnCategoryClicked;
        _categories.AttachNode(this);

        _list = new ListNode<AchievementCard, AchievementCardNode>
        {
            Position                 = new Vector2(c.X + CategoryW + Gap, bodyY),
            Size                     = new Vector2(cardsW, bodyH),
            ItemSpacing              = 4f,
            ShowNoResultsPlaceholder = true,
            AllowMultipleSelection   = false,
            OptionsList              = [],
        };
        _list.OnItemSelected = card =>
        {
            if (card is null) return;
            _selected = card.Info;
            Tweak.RequestProgress(card.Info.Id);
            UpdateDetail();
            _pendingFilter = true;
        };
        _list.AttachNode(this);

        BuildDetail(new Vector2(c.X + CategoryW + cardsW + Gap * 2f, bodyY), new Vector2(DetailW, bodyH));
        BuildBottomBar(new Vector2(c.X, c.Y + cs.Y - BottomBarH), new Vector2(cs.X, BottomBarH));

        _shownVersion  = -1;
        _pendingFilter = true;
        UpdateTabs();
        UpdateDetail();
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        // What is tracked, or what the game knows, changed: draw the cards again.
        if (_shownVersion != Tweak.Version || _shownLoaded != Tweak.IsLoaded)
            _pendingFilter = true;

        if (_pendingFilter) ApplyFilter();

        _list?.Update();
        _categories?.Update();

        // The list is long: keep the scrollbar thumb big enough to find and grab.
        if (_list is not null)       EnforceScrollbarMinThumb(_list.ScrollBarNode);
        if (_categories is not null) EnforceScrollbarMinThumb(_categories.ScrollBarNode);

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
            UpdateDetail();
        }
    }

    private const float MinScrollThumbH = 24f;

    // The ListNode recalculates the thumb every Update(); re-apply a visual minimum after it.
    private static void EnforceScrollbarMinThumb(ScrollBarNode scrollBar)
    {
        var thumb = scrollBar.ForegroundButtonNode;
        if (thumb.Height >= MinScrollThumbH) return;

        var scrollMax = (float)scrollBar.ScrollMaxPosition;
        thumb.Height = MinScrollThumbH;
        if (scrollMax > 0f)
            thumb.Y = scrollBar.ScrollPosition / scrollMax * (scrollBar.Height - MinScrollThumbH);
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _searchInput = null;
        _stateDropdown = null;
        _sortDropdown = null;
        _rewardDropdown = null;
        _pointsDropdown = null;
        _status = null;
        _count = null;
        _totals = null;
        _totalsBar = null;
        _clearSearchButton = null;
        _clearFiltersButton = null;
        _loadButton = null;
        _progressButton = null;
        _settingsButton = null;
        _helpButton = null;
        _list = null;
        _categories = null;
        Array.Clear(_tabStrips);
        Array.Clear(_tabButtons);

        _sumDone = _sumPoints = _sumSession = null;
        _sumBar = _selBar = null;
        _selIcon = null;
        _selName = _selCategory = _selDescription = _selReward = _selState = _selWhen = null;
        _trackButton = _openButton = null;
        Array.Clear(_slotLabels);

        base.OnFinalize(addon);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    /// <summary>The tabs on top, each with its colour on the line under it, and the totals on the right.</summary>
    private void BuildTabs(Vector2 pos, float width)
    {
        const float tabW = 150f;
        _tabsX = pos.X;
        _tabsY = pos.Y;

        for (var i = 0; i < Tabs.Length; i++)
        {
            var tab = Tabs[i];
            var button = new TextButtonNode
            {
                String   = Tweak.Text(tab == TabList ? "Tab.List" : $"Filter.{tab}"),
                Position = new Vector2(pos.X + 2f + i * (tabW + 6f), pos.Y + 2f),
                Size     = new Vector2(tabW, 26f),
            };
            button.OnClick = () => { _tab = tab; UpdateTabs(); FilterChanged(); };
            button.AttachNode(this);
            _tabButtons[i] = button;
        }

        // Every tab keeps its colour in a strip under it, the chosen one brighter and thicker.
        for (var i = 0; i < Tabs.Length; i++)
        {
            _tabStrips[i] = new ColorImageNode { Color = TabColors[i], Size = new Vector2(tabW, 3f), Position = new Vector2(pos.X + 2f + i * (tabW + 6f), pos.Y + 29f) };
            _tabStrips[i]!.AttachNode(this);
        }

        _totals = new TextNode
        {
            Position      = new Vector2(pos.X + width - 420f, pos.Y + 7f),
            Size          = new Vector2(412f, 18f),
            TextColor     = TitleGold,
            FontSize      = 14,
            AlignmentType = AlignmentType.Right,
        };
        _totals.AddTextFlags(TextFlags.Ellipsis);
        _totals.AttachNode(this);

        // How much of everything is done, as a thin bar under the totals.
        new ColorImageNode { Color = TotalsTrack, Position = new Vector2(pos.X + width - 420f, pos.Y + 27f), Size = new Vector2(412f, 4f) }.AttachNode(this);
        _totalsBar = new ColorImageNode { Color = TotalsFill, Position = new Vector2(pos.X + width - 420f, pos.Y + 27f), Size = new Vector2(1f, 4f) };
        _totalsBar.AttachNode(this);
    }

    private void UpdateTabs()
    {
        for (var i = 0; i < Tabs.Length; i++)
        {
            if (_tabStrips[i] is not { } strip) continue;

            var chosen = Tabs[i] == _tab;
            strip.Position = new Vector2(_tabsX + 2f + i * 156f, _tabsY + (chosen ? 27f : 29f));
            strip.Size     = new Vector2(150f, chosen ? 5f : 3f);
            strip.Color    = TabColors[i] with { W = chosen ? 1f : 0.45f };
        }
    }

    private void BuildFilterBar(Vector2 pos, Vector2 size)
    {
        var cy = pos.Y + (size.Y - 26f) / 2f;

        _searchInput = new TextInputNode
        {
            Position          = new Vector2(pos.X + 4f, cy - 1f),
            Size              = new Vector2(200f, 28f),
            PlaceholderString = Tweak.Text("Window.Search"),
            MaxCharacters     = 64,
        };
        _searchInput.OnInputReceived = str => { _searchText = str.ToString(); FilterChanged(); };
        _searchInput.OnInputComplete = str => { _searchText = str.ToString(); FilterChanged(); };
        _searchInput.AttachNode(this);

        _clearSearchButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.CrossSmall,
            Position    = new Vector2(pos.X + 206f, cy + 2f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Window.ClearSearch"),
        };
        _clearSearchButton.OnClick = ClearSearch;
        _clearSearchButton.AttachNode(this);

        var x = pos.X + 236f;
        _stateDropdown = Dropdown(ref x, cy, 120f, 4, [StateAll, StateMissing, StateDone, StateRecent], _stateFilter, key => Tweak.Text($"Filter.{key}"), key => _stateFilter = key);
        _sortDropdown = Dropdown(ref x, cy, 150f, 7, [SortGame, SortName, SortPoints, SortCategory, SortClosest, SortNewest, SortLeft], _sortMode, key => Tweak.Text($"Sort.{key}"), key => _sortMode = key);
        _rewardDropdown = Dropdown(ref x, cy, 140f, 4, [RewardAny, RewardTitle, RewardItem, RewardNone], _rewardFilter, key => Tweak.Text($"Reward.{key}"), key => _rewardFilter = key);
        _pointsDropdown = Dropdown(ref x, cy, 110f, 5, ["0", "5", "10", "25", "50"], _pointsFilter,
            key => key == "0" ? Tweak.Text("Filter.PointsAny") : $"{key}+ {Tweak.Text("Filter.Points")}", key => _pointsFilter = key);

        _clearFiltersButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.Undo,
            Position    = new Vector2(x + 2f, cy + 2f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Window.ClearFilters"),
        };
        _clearFiltersButton.OnClick = ClearFilters;
        _clearFiltersButton.AttachNode(this);

        _settingsButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = new Vector2(pos.X + size.X - 32f, cy),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Tweak.Text("Window.Settings"),
        };
        _settingsButton.OnClick = Tweak.OpenSettings;
        _settingsButton.AttachNode(this);

        _progressButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.Refresh,
            Position    = new Vector2(pos.X + size.X - 64f, cy),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Tweak.Text("Window.RefreshProgress"),
        };
        _progressButton.OnClick = () => { Tweak.RefreshProgress(); _nextStatus = DateTime.MinValue; };
        _progressButton.AttachNode(this);

        _loadButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.Update,
            Position    = new Vector2(pos.X + size.X - 96f, cy),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Tweak.Text("Window.Load"),
        };
        _loadButton.OnClick = () => { Tweak.LoadFromGame(); _nextStatus = DateTime.MinValue; };
        _loadButton.AttachNode(this);
    }

    private StringDropDownNode Dropdown(ref float x, float cy, float width, int rows, string[] options, string selected,
        Func<string, Lumina.Text.ReadOnly.ReadOnlySeString> label, Action<string> onSelected)
    {
        var dropdown = new StringDropDownNode
        {
            GetLabelFunction = label,
            Position         = new Vector2(x, cy + 1f),
            Size             = new Vector2(width, 24f),
            MaxListOptions   = rows,
            Options          = [.. options],
            SelectedOption   = selected,
        };
        dropdown.OnOptionSelected = key => { onSelected(key); FilterChanged(); };
        dropdown.AttachNode(this);
        x += width + 4f;
        return dropdown;
    }

    private TextNode Label(Vector2 pos, float width, float height, uint fontSize, Vector4 color,
        AlignmentType alignment = AlignmentType.Left, bool wrap = false)
    {
        var node = new TextNode
        {
            Position      = pos,
            Size          = new Vector2(width, height),
            TextColor     = color,
            FontSize      = fontSize,
            AlignmentType = alignment,
        };
        node.AddTextFlags(wrap ? TextFlags.WordWrap | TextFlags.MultiLine : TextFlags.Ellipsis);
        node.AttachNode(this);
        return node;
    }

    private TextButtonNode Button(string label, string tip, Vector2 pos, float width, Action onClick)
    {
        var button = new TextButtonNode
        {
            String      = label,
            Position    = pos,
            Size        = new Vector2(width, 26f),
            TextTooltip = tip,
        };
        button.OnClick = onClick;
        button.AttachNode(this);
        return button;
    }

    private void Panel(Vector2 pos, Vector2 size, Vector4 color)
    {
        new ColorImageNode { Color = color, Position = pos, Size = size }.AttachNode(this);
    }

    private void Line(Vector2 pos, float width)
    {
        new HorizontalLineNode { Position = pos, Size = new Vector2(width, 2f) }.AttachNode(this);
    }

    /// <summary>The panel on the right: the summary of everything, the selected achievement and the pinned list tools.</summary>
    private void BuildDetail(Vector2 pos, Vector2 size)
    {
        var x = pos.X + 4f;
        var w = size.X - 8f;
        _barWidth = w - 8f;

        // Summary.
        Label(new Vector2(x, pos.Y + 2f), w, 16f, 13, TitleGold).String = Tweak.Text("Detail.Summary");
        _sumDone    = Label(new Vector2(x, pos.Y + 20f), w, 15f, 12, BodyGrey);
        _sumPoints  = Label(new Vector2(x, pos.Y + 36f), w, 15f, 12, BodyGrey);
        new ColorImageNode { Color = BarTrack, Position = new Vector2(x, pos.Y + 54f), Size = new Vector2(w, 8f) }.AttachNode(this);
        _sumBar     = new ColorImageNode { Color = DoneGreen with { W = 0.85f }, Position = new Vector2(x, pos.Y + 54f), Size = new Vector2(1f, 8f) };
        _sumBar.AttachNode(this);
        _sumSession = Label(new Vector2(x, pos.Y + 66f), w, 15f, 12, MutedGrey);

        Line(new Vector2(pos.X, pos.Y + 86f), size.X);

        // Selected achievement, on a cream card like the ones of the list.
        Panel(new Vector2(pos.X + 1f, pos.Y + 90f), new Vector2(size.X - 2f, 180f), CardBorder);
        Panel(new Vector2(pos.X + 3f, pos.Y + 92f), new Vector2(size.X - 6f, 176f), CardCream);
        _selIcon = new IconImageNode { FitTexture = true, Position = new Vector2(x + 4f, pos.Y + 98f), Size = new Vector2(40f, 40f) };
        _selIcon.AttachNode(this);
        _selName     = Label(new Vector2(x + 50f, pos.Y + 98f), w - 50f, 18f, 14, InkName);
        _selCategory = Label(new Vector2(x + 50f, pos.Y + 120f), w - 50f, 15f, 11, InkMuted);
        _selDescription = Label(new Vector2(x + 4f, pos.Y + 144f), w - 8f, 58f, 12, InkText, wrap: true);
        _selReward   = Label(new Vector2(x + 4f, pos.Y + 206f), w - 8f, 15f, 12, CardGold);
        _selState    = Label(new Vector2(x + 4f, pos.Y + 222f), w - 8f, 15f, 12, InkText);
        new ColorImageNode { Color = new Vector4(0.20f, 0.14f, 0.08f, 0.30f), Position = new Vector2(x + 4f, pos.Y + 240f), Size = new Vector2(w - 8f, 8f) }.AttachNode(this);
        _selBar      = new ColorImageNode { Color = BarBlue, Position = new Vector2(x + 4f, pos.Y + 240f), Size = new Vector2(1f, 8f) };
        _selBar.AttachNode(this);
        _selWhen     = Label(new Vector2(x + 4f, pos.Y + 251f), w - 8f, 15f, 11, InkMuted);

        var half = (w - 6f) / 2f;
        _trackButton = Button(Tweak.Text("Detail.Track"), Tweak.Text("Detail.Track.Help"), new Vector2(x, pos.Y + 272f), half, ToggleSelected);
        _openButton  = Button(Tweak.Text("Detail.Open"), Tweak.Text("Detail.Open.Help"), new Vector2(x + half + 6f, pos.Y + 272f), half,
            () => { if (_selected is not null) Tweak.OpenInGame(_selected.Id); });

        Line(new Vector2(pos.X, pos.Y + 308f), size.X);

        // The pinned list.
        Label(new Vector2(x, pos.Y + 314f), w, 16f, 13, TitleGold).String = Tweak.Text("Tools.Title");
        Button(Tweak.Text("Tools.Closest"), Tweak.Text("Tools.Closest.Help"), new Vector2(x, pos.Y + 334f), half, () => Tweak.TrackClosest(5));
        Button(Tweak.Text("Tools.Clear"), Tweak.Text("Tools.Clear.Help"), new Vector2(x + half + 6f, pos.Y + 334f), half, Tweak.ClearTracked);
        Button(Tweak.Text("Tools.Copy"), Tweak.Text("Tools.Copy.Help"), new Vector2(x, pos.Y + 364f), half, Tweak.CopyList);
        Button(Tweak.Text("Tools.Paste"), Tweak.Text("Tools.Paste.Help"), new Vector2(x + half + 6f, pos.Y + 364f), half, Tweak.PasteList);

        for (var i = 0; i < GoblinAchievements.ListSlots; i++)
        {
            var slot = i;
            var y = pos.Y + 398f + i * 30f;
            _slotLabels[i] = Label(new Vector2(x, y + 5f), w - 150f, 16f, 12, BodyGrey);
            Button(Tweak.Text("Tools.Save"), Tweak.Text("Tools.Save.Help"), new Vector2(x + w - 146f, y), 72f, () => Tweak.SaveList(slot));
            Button(Tweak.Text("Tools.Load"), Tweak.Text("Tools.Load.Help"), new Vector2(x + w - 72f, y), 72f, () => Tweak.LoadList(slot));
        }
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
        _helpButton.OnClick = Tweak.OpenHelp;
        _helpButton.AttachNode(this);

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

    // ── Data ──────────────────────────────────────────────────────────────────

    private AchievementStatusText StatusOf(AchievementInfo info)
    {
        if (!Tweak.IsLoaded)
            return new AchievementStatusText("?", CardMuted, string.Empty);
        if (Tweak.IsComplete(info.Id))
            return new AchievementStatusText(Tweak.Text("Status.Done"), DoneGreen, string.Empty, 1f);

        if (Tweak.TryGetProgress(info.Id, out var progress) && progress.Max > 0)
            return new AchievementStatusText($"{progress.Current:N0} / {progress.Max:N0}", CardGold, string.Empty, Math.Clamp((float)progress.Current / progress.Max, 0f, 1f));
        if (Tweak.IsNear(info.Id))
            return new AchievementStatusText(Tweak.Text("Status.Near"), CardGold, string.Empty);

        return new AchievementStatusText(Tweak.Text("Status.Missing"), CardMuted, string.Empty);
    }

    private AchievementCard CardOf(AchievementInfo info)
    {
        var status = StatusOf(info);
        var tip = info.Description;
        if (info.Reward.Length > 0) tip += $"\n{Tweak.Text("Row.Reward")}: {info.Reward}";
        tip += $"\n\n{Tweak.Text("Row.ClickDetail")}";

        var done = Tweak.IsLoaded && Tweak.IsComplete(info.Id);
        return new AchievementCard(info, status.Text, status.Color, status.Fraction,
            done, Tweak.IsTracked(info.Id), _selected?.Id == info.Id, Tweak.RewardOf(info, done), tip);
    }

    private void ToggleSelected()
    {
        if (_selected is null) return;
        Tweak.ToggleTracked(_selected.Id);
        UpdateDetail();
    }

    /// <summary>The panel on the right: the summary, the selected achievement and the saved lists.</summary>
    private void UpdateDetail()
    {
        if (_sumDone is null) return;

        var loaded = Tweak.IsLoaded;
        var stats  = Tweak.Stats;

        if (loaded)
        {
            var percent = stats.Total == 0 ? 0 : stats.Done * 100f / stats.Total;
            _sumDone!.String   = string.Format(Tweak.Text("Detail.Done"), stats.Done.ToString("N0"), stats.Total.ToString("N0"), percent.ToString("0.#"));
            _sumPoints!.String = string.Format(Tweak.Text("Detail.Points"), stats.PointsDone.ToString("N0"), stats.PointsTotal.ToString("N0"));
            _sumBar!.Width     = Math.Max(1f, (_barWidth + 8f) * (stats.PointsTotal == 0 ? 0f : (float)stats.PointsDone / stats.PointsTotal));
            if (_totals is not null)
                _totals.String = string.Format(Tweak.Text("Header.Totals"), stats.PointsDone.ToString("N0"), stats.Done.ToString("N0"), stats.Total.ToString("N0"));
            if (_totalsBar is not null)
                _totalsBar.Width = Math.Max(1f, 412f * (stats.Total == 0 ? 0f : (float)stats.Done / stats.Total));
        }
        else
        {
            _sumDone!.String   = Tweak.Text("Detail.NotLoaded");
            _sumPoints!.String = string.Empty;
            _sumBar!.Width     = 1f;
            if (_totals is not null) _totals.String = string.Empty;
            if (_totalsBar is not null) _totalsBar.Width = 1f;
        }

        _sumSession!.String = string.Format(Tweak.Text("Detail.Session"), Tweak.SessionDone, Tweak.SessionPoints);

        for (var i = 0; i < _slotLabels.Length; i++)
            if (_slotLabels[i] is { } label)
                label.String = string.Format(Tweak.Text("Tools.Slot"), i + 1, Tweak.ListSize(i));

        if (_selected is not { } info)
        {
            _selIcon!.IsVisible     = false;
            _selName!.String        = string.Empty;
            _selName.TextColor      = InkMuted;
            _selCategory!.String    = string.Empty;
            _selDescription!.String = Tweak.Text("Detail.None");
            _selReward!.String      = string.Empty;
            _selState!.String       = string.Empty;
            _selWhen!.String        = string.Empty;
            _selBar!.Width          = 1f;
            _trackButton!.IsEnabled = false;
            _openButton!.IsEnabled  = false;
            return;
        }

        var done     = loaded && Tweak.IsComplete(info.Id);
        var progress = Tweak.TryGetProgress(info.Id, out var value) && value.Max > 0 ? value : (AchievementProgress?)null;

        _selIcon!.IsVisible     = info.IconId != 0;
        _selIcon.IconId         = info.IconId;
        _selName!.String        = info.Name;
        _selName.TextColor      = done ? InkDone : InkName;
        _selCategory!.String    = info.Kind.Length > 0 ? $"{info.Kind} - {info.Category}" : info.Category;
        _selDescription!.String = info.Description;
        _selReward!.String      = info.Reward.Length > 0 ? $"{Tweak.Text("Row.Reward")}: {info.Reward}" : Tweak.Text("Detail.NoReward");

        if (!loaded)
        {
            _selState!.String = string.Format(Tweak.Text("Detail.PointsOf"), info.Points);
            _selBar!.Width    = 1f;
        }
        else if (done)
        {
            _selState!.String = $"{string.Format(Tweak.Text("Detail.PointsOf"), info.Points)}  -  {Tweak.Text("Status.Done")}";
            _selBar!.Width    = _barWidth;
        }
        else if (progress is { } p)
        {
            _selState!.String = $"{string.Format(Tweak.Text("Detail.PointsOf"), info.Points)}  -  {p.Current:N0} / {p.Max:N0}";
            _selBar!.Width    = Math.Max(1f, _barWidth * Math.Clamp((float)p.Current / p.Max, 0f, 1f));
        }
        else
        {
            _selState!.String = $"{string.Format(Tweak.Text("Detail.PointsOf"), info.Points)}  -  {Tweak.Text(Tweak.IsNear(info.Id) ? "Status.Near" : "Status.Missing")}";
            _selBar!.Width    = 1f;
        }

        _selWhen!.String = Tweak.CompletedAt(info.Id) is { } when
            ? string.Format(Tweak.Text("Detail.Completed"), when.ToString("g"))
            : !done && progress is not null && Tweak.ProgressAge(info.Id) is { } age
                ? string.Format(Tweak.Text("Detail.Age"), AgeText(age))
                : string.Empty;

        _trackButton!.IsEnabled = true;
        _trackButton.String     = Tweak.Text(Tweak.IsTracked(info.Id) ? "Detail.Untrack" : "Detail.Track");
        _openButton!.IsEnabled  = true;
    }

    /// <summary>"5 min ago", "2 h ago"...: how old the number the server gave is.</summary>
    private string AgeText(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return Tweak.Text("Age.Now");
        if (age < TimeSpan.FromHours(1)) return string.Format(Tweak.Text("Age.Minutes"), (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1)) return string.Format(Tweak.Text("Age.Hours"), (int)age.TotalHours);
        return string.Format(Tweak.Text("Age.Days"), (int)age.TotalDays);
    }

    // ── Categories ────────────────────────────────────────────────────────────

    /// <summary>A click on the category column: a group folds out (and picks all of it), a category picks itself.</summary>
    private void OnCategoryClicked(AchievementCategoryEntry? entry)
    {
        if (entry is null) return;

        if (entry.Group && !_expanded.Remove(entry.Key))
            _expanded.Add(entry.Key);

        Tweak.Config.ExpandedGroups = [.. _expanded];
        Tweak.Changed();

        _categoryFilter = entry.Key;
        FilterChanged();
    }

    /// <summary>The column: everything, then each group of the game with, when open, its categories.</summary>
    private List<AchievementCategoryEntry> BuildCategoryEntries()
    {
        var loaded = Tweak.IsLoaded;
        var stats  = loaded ? Tweak.Stats : null;
        var kinds  = new List<string>();
        foreach (var info in Tweak.Catalog)
            if (!kinds.Contains(info.Kind)) kinds.Add(info.Kind);

        var entries = new List<AchievementCategoryEntry>
        {
            new(AllCategories, Tweak.Text("Filter.AllCategories"), false, false,
                stats?.Done ?? 0, Tweak.Catalog.Count, _categoryFilter == AllCategories, loaded),
        };

        foreach (var kind in kinds)
        {
            var key        = GroupPrefix + kind;
            var categories = Tweak.Catalog.Where(info => info.Kind == kind).Select(info => info.CategoryId).Distinct().ToList();
            var total      = Tweak.Catalog.Count(info => info.Kind == kind);
            var done       = 0;
            if (stats is not null)
                foreach (var id in categories)
                    if (stats.Categories.TryGetValue(id, out var entry)) done += entry.Done;

            var open = _expanded.Contains(key);
            entries.Add(new AchievementCategoryEntry(key, kind.Length > 0 ? kind : Tweak.Text("Filter.NoKind"), true, open, done, total, _categoryFilter == key, loaded));
            if (!open) continue;

            foreach (var id in categories)
            {
                var name = Tweak.Catalog.First(info => info.CategoryId == id).Category;
                (int Done, int Total) counts = stats is not null && stats.Categories.TryGetValue(id, out var found) ? found : (0, Tweak.Catalog.Count(info => info.CategoryId == id));
                entries.Add(new AchievementCategoryEntry(id.ToString(), name, false, false, counts.Done, counts.Total, _categoryFilter == id.ToString(), loaded));
            }
        }

        return entries;
    }

    private void RefreshCategories()
    {
        if (_categories is null) return;

        var entries = BuildCategoryEntries();
        _categories.SelectedItems.Clear();
        if (entries.FirstOrDefault(entry => entry.Active) is { } active)
            _categories.SelectedItems.Add(active);

        _categories.AutoResetScroll = false;
        _categories.OptionsList     = entries;
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    /// <summary>Goes to the pinned achievements with this one selected (a click on the tracker).</summary>
    internal void ShowAchievement(AchievementInfo info)
    {
        _tab      = TabTracked;
        _selected = info;
        Tweak.RequestProgress(info.Id);
        UpdateTabs();
        FilterChanged();
        if (IsOpen) UpdateDetail();
    }

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

    /// <summary>Back to every category, state, reward and points. The tab and the sort order are not filters and stay.</summary>
    private void ClearFilters()
    {
        _categoryFilter = AllCategories;
        _stateFilter    = StateAll;
        _rewardFilter   = RewardAny;
        _pointsFilter   = "0";
        if (_stateDropdown is not null)  _stateDropdown.SelectedOption  = _stateFilter;
        if (_rewardDropdown is not null) _rewardDropdown.SelectedOption = _rewardFilter;
        if (_pointsDropdown is not null) _pointsDropdown.SelectedOption = _pointsFilter;

        FilterChanged();
    }

    private bool InCategory(AchievementInfo info)
    {
        if (_categoryFilter == AllCategories) return true;
        return _categoryFilter.StartsWith(GroupPrefix, StringComparison.Ordinal)
            ? info.Kind == _categoryFilter[GroupPrefix.Length..]
            : info.CategoryId.ToString() == _categoryFilter;
    }

    private void ApplyFilter()
    {
        _pendingFilter = false;
        _shownVersion  = Tweak.Version;
        _shownLoaded   = Tweak.IsLoaded;

        IEnumerable<AchievementInfo> items = Tweak.Catalog;

        var term = _searchText.Trim();
        if (term.Length > 0)
            items = items.Where(info => info.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                                        || info.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
                                        || info.Category.Contains(term, StringComparison.OrdinalIgnoreCase));

        items = items.Where(InCategory);

        items = _rewardFilter switch
        {
            RewardTitle => items.Where(info => info.HasTitle),
            RewardItem  => items.Where(info => info.HasItem && !info.HasTitle),
            RewardNone  => items.Where(info => !info.HasTitle && !info.HasItem),
            _           => items,
        };

        if (int.TryParse(_pointsFilter, out var minimum) && minimum > 0)
            items = items.Where(info => info.Points >= minimum);

        items = _stateFilter switch
        {
            StateMissing => items.Where(info => !Tweak.IsComplete(info.Id)),
            StateDone    => items.Where(info => Tweak.IsComplete(info.Id)),
            StateRecent  => RecentFirst(items),
            _            => items,
        };

        items = _tab switch
        {
            TabNear    => items.Where(info => !Tweak.IsComplete(info.Id) && Tweak.IsNear(info.Id)),
            TabTracked => items.Where(info => Tweak.IsTracked(info.Id)),
            TabRecent  => RecentFirst(items),
            _          => items,
        };

        items = _sortMode switch
        {
            SortName     => items.OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase),
            SortPoints   => items.OrderByDescending(info => info.Points).ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase),
            SortCategory => items.OrderBy(info => info.Category, StringComparer.OrdinalIgnoreCase).ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase),
            SortClosest  => items.OrderBy(info => Tweak.IsComplete(info.Id) ? 1 : 0).ThenByDescending(info => Tweak.Closeness(info.Id)),
            SortNewest   => items.OrderByDescending(info => info.Id),
            SortLeft     => items.OrderBy(info => Tweak.IsComplete(info.Id) ? 1 : 0).ThenByDescending(info => info.Points).ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase),
            _            => items,
        };

        // A change the game made must not throw the list back to the top; one made by the user does.
        var filtered = items.ToList();
        if (_list is not null)
        {
            _list.AutoResetScroll = _resetScroll;
            _list.OptionsList     = [.. filtered.Select(CardOf)];
        }

        _resetScroll = false;
        RefreshCategories();

        if (_count is not null)
            _count.String = string.Format(Tweak.Text("Window.Count"), filtered.Count, Tweak.Catalog.Count);

        _collisionRebuildFrames = 3;
    }

    /// <summary>What the user completed with the tweak on, the latest first. With no sort chosen the list keeps that order.</summary>
    private IEnumerable<AchievementInfo> RecentFirst(IEnumerable<AchievementInfo> items)
    {
        var order = new Dictionary<uint, int>();
        foreach (var entry in Tweak.History) order.TryAdd(entry.Id, order.Count);

        var recent = items.Where(info => order.ContainsKey(info.Id));
        return _sortMode == SortGame ? recent.OrderBy(info => order[info.Id]) : recent;
    }

    private void UpdateStatus()
    {
        if (_progressButton is not null)
            _progressButton.IsEnabled = Tweak.IsLoaded && Tweak.QueuedProgress == 0;

        if (_status is null) return;

        if (!Tweak.IsLoaded)
        {
            _status.String    = Tweak.Text("Window.NotLoaded");
            _status.TextColor = WarnRed;
            return;
        }

        _status.TextColor = MutedGrey;
        _status.String = Tweak.QueuedProgress > 0
            ? string.Format(Tweak.Text("Window.Asking"), Tweak.QueuedProgress)
            : string.Format(Tweak.Text("Window.Loaded"), Tweak.TrackedCount, Tweak.Text(Tweak.NearLoaded ? "Window.NearYes" : "Window.NearNo"));
    }
}
