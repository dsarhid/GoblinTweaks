using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native settings window of GoblinSniper: language of the names, notification, scan rules, watched item types and
/// the individual items to watch (a search box and the list of what is watched).
/// </summary>
internal sealed unsafe class GoblinSniperSettingsAddon : SettingsPanelAddon
{
    public GoblinSniper? Tweak { get; init; }

    private readonly Dictionary<string, (CheckboxNode Box, string Label)> _categoryBoxes = [];
    private readonly List<NodeBase> _rows = [];
    private TextNode?      _loading;
    private TextInputNode? _search;
    private string         _term = string.Empty;
    private float          _itemsTop;
    private float          _rowsTop;
    private bool           _itemsBuilt;
    private bool           _rowsDirty;

    protected override float HeaderHeight => 86f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;
        var options = Tweak.Current;

        LanguageSelect(origin, width, Tweak.Text("Language"), Tweak.Text("Language.Help"), Tweak.LanguageChoices,
            () => options.DataLanguage,
            code => Tweak.SetDataLanguage(code));
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var options = Tweak.Current;

        Toggle(Tweak.Text("Notify"), Tweak.Text("Notify.Help"), options.Notify,
            on => { options.Notify = on; Tweak.SaveCurrent(); });
        Toggle(Tweak.Text("WholeDataCenter"), Tweak.Text("WholeDataCenter.Help"), options.WholeDataCenter,
            on => { options.WholeDataCenter = on; Tweak.SaveCurrent(); });

        Section(Tweak.Text("Rules"));
        Number(Tweak.Text("Discount"), Tweak.Text("Discount.Help"), 10, 95, 1, options.DiscountPercent,
            value => { options.DiscountPercent = value; Tweak.SaveCurrent(); });
        Number(Tweak.Text("MinPrice"), Tweak.Text("MinPrice.Help"), 0, 999_999_999, 10_000, options.MinAveragePrice,
            value => { options.MinAveragePrice = value; Tweak.SaveCurrent(); });
        Number(Tweak.Text("ScanMinutes"), null, 5, 60, 1, options.ScanMinutes,
            value => { options.ScanMinutes = value; Tweak.SaveCurrent(); });
        Number(Tweak.Text("MaxAge"), Tweak.Text("MaxAge.Help"), 1, 72, 1, options.MaxAgeHours,
            value => { options.MaxAgeHours = value; Tweak.SaveCurrent(); });
        Number(Tweak.Text("MaxDays"), Tweak.Text("MaxDays.Help"), 7, 180, 1, options.MaxDaysWithoutSale,
            value => { options.MaxDaysWithoutSale = value; Tweak.SaveCurrent(); });

        Section(Tweak.Text("Types"));
        Note(Tweak.Text("Types.Help"), Pad, ContentW, Muted);
        Space(6f);
        foreach (var (key, _) in GoblinSniper.Categories)
        {
            var label = Tweak.Text($"Category.{key}");
            var box = Toggle(label, null, options.Categories.Contains(key), on =>
            {
                if (on) options.Categories.Add(key); else options.Categories.Remove(key);
                Tweak.SaveCurrent();
            });
            _categoryBoxes[key] = (box, label);
        }

        Section(Tweak.Text("Items"));
        Note(Tweak.Text("Items.Help"), Pad, ContentW, Muted);
        Space(6f);

        // The item names load in the background; the search appears when they are there.
        _itemsTop = Y;
        _loading  = Note(Tweak.Text("Loading"), Pad, ContentW, Muted);
        _rowsTop  = Y;
        TryBuildItems();
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        if (!_itemsBuilt) TryBuildItems();

        if (_rowsDirty)
        {
            _rowsDirty = false;
            RebuildRows();
        }
    }

    protected override void OnClosed()
    {
        _categoryBoxes.Clear();
        _rows.Clear();
        _loading = null;
        _search  = null;
        _itemsBuilt = false;
        _rowsDirty  = false;
        _term = string.Empty;
    }

    /// <summary>Once the catalog of items exists: counts next to the types, the search box and the watched items.</summary>
    private void TryBuildItems()
    {
        if (Tweak is null || !Tweak.CatalogReady()) return;

        _itemsBuilt = true;

        foreach (var (key, (box, label)) in _categoryBoxes)
            box.String = $"{label} ({Tweak.CategoryCount(key)})";

        _loading?.Dispose();
        _loading = null;

        // The "loading" line is gone, so the search starts where it was.
        Y = _itemsTop;
        _search = new TextInputNode
        {
            Position          = new Vector2(Pad, Y),
            Size              = new Vector2(ContentW, 28f),
            PlaceholderString = Tweak.Text("Items.Search"),
            MaxCharacters     = 64,
        };
        _search.OnInputReceived = text =>
        {
            _term      = text.ToString().Trim();
            _rowsDirty = true;
        };
        _search.AttachNode(Host);
        Y += 36f;
        _rowsTop = Y;

        RebuildRows();
    }

    /// <summary>Redraws the search results and the watched items under the search box.</summary>
    private void RebuildRows()
    {
        if (Tweak is null) return;

        foreach (var row in _rows) row.Dispose();
        _rows.Clear();

        var options = Tweak.Current;
        Y = _rowsTop;

        if (_term.Length >= 2)
        {
            var found = Tweak.SearchItems(_term).Where(item => !options.Items.Contains(item.Id)).ToList();
            foreach (var (id, name) in found)
            {
                var add = new TextButtonNode
                {
                    String   = $"+ {name}",
                    Position = new Vector2(Pad, Y),
                    Size     = new Vector2(ContentW, 24f),
                };
                var itemId = id;
                add.OnClick = () =>
                {
                    options.Items.Add(itemId);
                    Tweak.SaveCurrent();
                    _rowsDirty = true; // redrawn next frame, not while this button is handling its click
                };
                add.AttachNode(Host);
                _rows.Add(add);
                Y += 26f;
            }

            if (found.Count == 0)
                _rows.Add(Note(Tweak.Text("Items.NoResults"), Pad, ContentW, Muted));

            Space(10f);
        }

        if (options.Items.Count == 0)
        {
            _rows.Add(Note(Tweak.Text("Items.Empty"), Pad, ContentW, Muted));
        }
        else
        {
            foreach (var id in options.Items.ToList())
            {
                var remove = new TextButtonNode
                {
                    String   = "x",
                    Position = new Vector2(Pad, Y),
                    Size     = new Vector2(30f, 24f),
                };
                var itemId = id;
                remove.OnClick = () =>
                {
                    options.Items.Remove(itemId);
                    Tweak.SaveCurrent();
                    _rowsDirty = true;
                };
                remove.AttachNode(Host);
                _rows.Add(remove);

                var name = new TextNode
                {
                    String    = Tweak.ItemName(id),
                    Position  = new Vector2(Pad + 38f, Y + 3f),
                    Size      = new Vector2(ContentW - 38f, 18f),
                    TextColor = White,
                    FontSize  = 14,
                };
                name.AttachNode(Host);
                _rows.Add(name);

                Y += 28f;
            }
        }

        FinishLayout();
    }
}
