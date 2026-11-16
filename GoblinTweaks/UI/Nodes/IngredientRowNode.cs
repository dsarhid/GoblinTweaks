using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;
using GoblinTweaks.Crafting;
using GoblinTweaks.Services;
using GoblinTweaks.Tweaks;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// One ingredient in the detail tree. <see cref="Depth"/> indents nested sub-ingredients;
/// <see cref="Craftable"/> rows can be expanded to reveal their own sub-recipe (<see cref="NodeId"/>
/// identifies the tree node so the addon can toggle it).
/// </summary>
/// <see cref="Price"/> (My List totals only) adds a price column; <see cref="PriceTip"/> is added to the location tooltip.
/// My List "By source" only: <see cref="Header"/> makes the row a group title (<see cref="HeaderRight"/> on its right,
/// open or folded by <see cref="Expanded"/>); <see cref="QtyText"/> replaces "have/need" (a "Craft first" row, shown in
/// gold); <see cref="Place"/> is shown
/// instead of "Not found" when the item is missing and owned nowhere; <see cref="ExtraTip"/> is added to the tooltip.
/// <see cref="TravelColumn"/> keeps room for the map and teleport buttons right of the price, shown when <see cref="Travel"/> is set.
internal readonly record struct IngredientRow(
    IngredientCheck Check, string PrimaryLocation,
    IReadOnlyList<(string Location, int Total, int Hq, DateTime Seen)> AllLocations,
    int Depth, bool Craftable, bool Expanded, int NodeId,
    string? Price = null, string? PriceTip = null,
    string? Header = null, string? HeaderRight = null, string? QtyText = null, string? Place = null, string? ExtraTip = null,
    bool TravelColumn = false, GatherTravelTarget? Travel = null);

/// <summary>A material of a zone group "By source": what its map and teleport buttons act on.</summary>
internal sealed record GatherTravelTarget(uint ItemId, string Name, uint ZoneId, string ZoneName, GatherClass Class);

/// <summary>
/// One row in the ingredient detail list: indent + expand caret + game icon + name
/// (colored by availability) + qty + primary inventory location.
/// </summary>
internal sealed class IngredientRowNode : ListItemNode<IngredientRow>, IListItemNode
{
    public static float ItemHeight => 34f;

    /// <summary>Set by the addon; invoked with the ingredient item id on right-click.</summary>
    internal static Action<uint>? OnItemRightClick;

    /// <summary>Set by the addon: the map and teleport buttons of a "By source" zone material.</summary>
    internal static Action<GatherTravelTarget>? OnTravelMap, OnTravelTeleport;

    /// <summary>
    /// Set by the addon: a row showed or hid its buttons. KTK takes the collision flag off a hidden hit area with a
    /// tooltip and the game only takes it back when the window's collision list is rebuilt.
    /// </summary>
    internal static Action? OnHitAreasChanged;

    private static readonly Vector4 ColorFulfilled = new(0.30f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 ColorElsewhere = new(0.95f, 0.80f, 0.20f, 1f);
    private static readonly Vector4 ColorMissing   = new(0.85f, 0.35f, 0.30f, 1f);
    private static readonly Vector4 ColorSubtitle  = new(157 / 255f, 131 / 255f,  91 / 255f, 1f);
    private static readonly Vector4 ColorCaret     = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 ColorPrice     = new(0.60f, 0.75f, 0.60f, 1f);
    // The map and teleport buttons of Goblin Hunts.
    private static readonly Vector4 MapColor       = new(0x3A / 255f, 0x6A / 255f, 0x8A / 255f, 1f);
    private static readonly Vector4 TpColor        = new(0xC7 / 255f, 0x7A / 255f, 0x1A / 255f, 1f);
    private static readonly Vector4 GlyphWhite     = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 GlyphCream     = new(0.92f, 0.86f, 0.68f, 1f);
    private const float ButtonSz  = 18f;
    private const float ButtonGap = 3f;

    private const float IndentW = 16f;
    private const float CaretW  = 14f;
    private const float IconSz  = 26f;
    private const float Gap     = 6f;
    // Column widths, also used by the addon to line its column headers up with the rows.
    internal const float QtyW   = 56f;
    internal const float LocW   = 130f;
    internal const float PriceW = 70f;
    /// <summary>Room for the map and teleport buttons ("By source"), right of the price.</summary>
    internal const float TravelW = 6f + ButtonSz + ButtonGap + ButtonSz;
    /// <summary>Where the name starts in a row at depth 0.</summary>
    internal const float NameX0 = 4f + CaretW + 2f + IconSz + Gap;

    private readonly TextNode      _caret;
    private readonly IconImageNode _icon;
    private readonly TextNode      _name;
    private readonly TextNode      _qty;
    private readonly TextNode      _location;
    private readonly TextNode      _price;
    private readonly TextNode      _headerRight;
    private readonly ResNode       _mapButton;
    private readonly ResNode       _tpButton;
    private readonly CollisionNode _mapHit;
    private readonly CollisionNode _tpHit;

    // The material the buttons act on (null: no buttons) and whether the row keeps room for them.
    private GatherTravelTarget? _travel;
    private bool _travelColumn;
    private string? _mapTip, _tpTip;

    // Current row depth and whether it has a price. Stored separately because the list calls SetNodeData *before*
    // updating ItemData, so reading ItemData during layout would use the stale value.
    private int  _depth;
    private bool _hasPrice;
    private bool _craftable;
    // A group title of My List "By source": caret, gold name and the text on its right; no icon, qty, place or price.
    private bool _isHeader;

    public unsafe IngredientRowNode()
    {
        _caret = new TextNode { FontSize = 13, TextColor = ColorCaret, AlignmentType = AlignmentType.Center };
        _caret.AttachNode(this);

        _icon = new IconImageNode { FitTexture = true, Size = new Vector2(IconSz, IconSz) };
        _icon.AttachNode(this);

        _name = new TextNode { FontSize = 12 };
        _name.AddTextFlags(TextFlags.Ellipsis);
        _name.AttachNode(this);

        _qty = new TextNode { FontSize = 11, TextColor = ColorSubtitle };
        _qty.AttachNode(this);

        _location = new TextNode { FontSize = 10, TextColor = ColorSubtitle };
        _location.AttachNode(this);

        _price = new TextNode { FontSize = 11, TextColor = ColorPrice, AlignmentType = AlignmentType.Right, IsVisible = false };
        _price.AttachNode(this);

        _headerRight = new TextNode { FontSize = 11, TextColor = ColorSubtitle, AlignmentType = AlignmentType.Right, IsVisible = false };
        _headerRight.AttachNode(this);

        // The buttons of Goblin Hunts: a folded map (three panels, the middle one lower) and an aetheryte (a diamond
        // with a light dot). Each is a container moved by Layout; its hit area is attached last, so it is on top.
        (_mapButton, _mapHit) = Button(MapColor,
        [
            (3f, 4f, 4f, 10f, GlyphWhite, 0f),
            (7f, 5f, 4f, 10f, GlyphCream, 0f),
            (11f, 4f, 4f, 10f, GlyphWhite, 0f),
        ], () => { if (_travel is { } t) OnTravelMap?.Invoke(t); });
        (_tpButton, _tpHit) = Button(TpColor,
        [
            (5f, 5f, 8f, 8f, GlyphWhite, 0.785f),
            (8f, 8f, 2f, 2f, TpColor, 0f),
        ], () => { if (_travel is { } t) OnTravelTeleport?.Invoke(t); });

        // MouseDown (not MouseClick) reliably fires for the right button here. A group title is no item: no menu.
        AddEvent(AtkEventType.MouseDown, (_, _, _, _, eventData) =>
        {
            if (eventData is not null && eventData->MouseData.ButtonId == 1 && ItemData.Check is { ItemId: not 0 } check)
                OnItemRightClick?.Invoke(check.ItemId);
        });
    }

    private unsafe (ResNode Box, CollisionNode Hit) Button(Vector4 color, (float X, float Y, float W, float H, Vector4 Color, float Rotation)[] glyph, Action onClick)
    {
        var box = new ResNode { Size = new Vector2(ButtonSz, ButtonSz), IsVisible = false };
        box.AttachNode(this);

        new ColorImageNode { Color = color, Size = new Vector2(ButtonSz, ButtonSz) }.AttachNode(box);
        foreach (var (x, y, w, h, c, rotation) in glyph)
        {
            var piece = new ColorImageNode { Color = c, Position = new Vector2(x, y), Size = new Vector2(w, h) };
            if (rotation != 0f)
            {
                piece.Origin   = new Vector2(w / 2f, h / 2f);
                piece.Rotation = rotation;
            }
            piece.AttachNode(box);
        }

        var hit = new CollisionNode { CollisionType = CollisionType.Hit, Size = new Vector2(ButtonSz, ButtonSz), IsVisible = false };
        hit.AddEvent(AtkEventType.MouseClick, (_, _, _, _, _) => onClick());
        hit.AttachNode(box);
        return (box, hit);
    }

    /// <summary>Shows or hides the buttons; tells the addon when they change, so the game takes their hit areas back.</summary>
    private void ShowButtons(bool show)
    {
        if (_mapButton.IsVisible == show) return;
        _mapButton.IsVisible = _tpButton.IsVisible = show;
        _mapHit.IsVisible    = _tpHit.IsVisible    = show;
        OnHitAreasChanged?.Invoke();
    }

    // The list calls this every frame for the rows it shows: only a craftable ingredient (or a group title) has a caret.
    public override void Update()
    {
        base.Update();
        var caret = _craftable || _isHeader;
        if (_caret.IsVisible != caret)           _caret.IsVisible       = caret;
        if (_price.IsVisible != _hasPrice)       _price.IsVisible       = _hasPrice;
        if (_icon.IsVisible == _isHeader)        _icon.IsVisible        = !_isHeader;
        if (_qty.IsVisible == _isHeader)         _qty.IsVisible         = !_isHeader;
        if (_location.IsVisible == _isHeader)    _location.IsVisible    = !_isHeader;
        if (_headerRight.IsVisible != _isHeader) _headerRight.IsVisible = _isHeader;
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged(); // keeps the native hover/select highlight sized correctly
        Layout();
    }

    private void Layout()
    {
        var indent = _depth * IndentW;
        var caretX = 4f + indent;
        var iconX  = caretX + CaretW + 2f;
        // A group title has no icon: its name starts where the icon would be.
        var nameX  = _isHeader ? iconX : iconX + IconSz + Gap;

        var iconY  = Math.Max(0f, (Height - IconSz) / 2f);
        var textY  = Math.Max(0f, (Height - 14f) / 2f);
        var priceW  = _hasPrice ? PriceW : 0f;
        var travelW = _travelColumn ? TravelW : 0f;
        var nameW   = Math.Max(0f, Width - nameX - QtyW - LocW - priceW - travelW - 4f);

        _caret.Position = new Vector2(caretX, textY);
        _caret.Size     = new Vector2(CaretW, 14f);

        _icon.Position  = new Vector2(iconX, iconY);

        _name.Position  = new Vector2(nameX, textY);
        _name.Size      = new Vector2(nameW, 14f);

        _qty.Position   = new Vector2(nameX + nameW, textY + 1f);
        _qty.Size       = new Vector2(QtyW, 13f);

        _location.Position = new Vector2(nameX + nameW + QtyW, textY + 1f);
        _location.Size     = new Vector2(LocW, 13f);

        _price.Position = new Vector2(nameX + nameW + QtyW + LocW, textY + 1f);
        _price.Size     = new Vector2(PriceW, 13f);

        _headerRight.Position = new Vector2(nameX + nameW, textY + 1f);
        _headerRight.Size     = new Vector2(QtyW + LocW, 13f);

        var buttonX = nameX + nameW + QtyW + LocW + priceW + 6f;
        var buttonY = Math.Max(0f, (Height - ButtonSz) / 2f);
        _mapButton.Position = new Vector2(buttonX, buttonY);
        _tpButton.Position  = new Vector2(buttonX + ButtonSz + ButtonGap, buttonY);
    }

    protected override void SetNodeData(IngredientRow data)
    {
        var ing = data.Check;

        _depth        = data.Depth;
        _craftable    = data.Craftable;
        _isHeader     = data.Header is not null;
        _travelColumn = data.TravelColumn;
        _travel       = _isHeader ? null : data.Travel;
        if (_travel is { } travel)
        {
            // Checked on every fill (the window language or the class may have changed), written only when it did.
            var mapTip = CraftLoc.Get("travel.map");
            var tpTip  = CraftLoc.Fmt("travel.tp", GatherTravel.ClassName(travel.Class));
            if (_mapTip != mapTip) _mapHit.TextTooltip = _mapTip = mapTip;
            if (_tpTip != tpTip)   _tpHit.TextTooltip  = _tpTip  = tpTip;
        }
        ShowButtons(_travel is not null);
        // Text first, then visibility: the first time a row is filled, its caret showed "+" on every ingredient,
        // crystals included (Update puts it right if anything shows it again).
        _caret.String    = data.Expanded ? "-" : "+";
        _caret.IsVisible = data.Craftable || _isHeader;

        _icon.IsVisible        = !_isHeader;
        _qty.IsVisible         = !_isHeader;
        _location.IsVisible    = !_isHeader;
        _headerRight.IsVisible = _isHeader;

        if (_isHeader)
        {
            // A group title: gold name, what is left to get on its right, no tooltip and no price.
            _name.String        = data.Header!;
            _name.TextColor     = ColorCaret;
            _headerRight.String = data.HeaderRight ?? string.Empty;
            TextTooltip         = string.Empty;
            ItemTooltip         = 0;
            _hasPrice           = false;
            _price.String       = string.Empty;
            _price.IsVisible    = false;
            Layout();
            return;
        }

        _icon.IconId = ing.IconId;
        _name.String = ing.Name;
        _qty.String  = data.QtyText ?? $"{ing.Available}/{ing.Required}";

        Vector4 statusColor;
        string  locText;

        if (ing.Missing > 0)
        {
            statusColor = ColorMissing;
            locText     = string.IsNullOrEmpty(data.PrimaryLocation) ? data.Place ?? CraftLoc.Get("loc.notfound") : data.PrimaryLocation;
            TextTooltip = string.Empty;
            ItemTooltip = ing.ItemId;
        }
        else if (ing.InPlayer >= ing.Required)
        {
            statusColor = ColorFulfilled;
            locText     = CraftLoc.Get("loc.mainbags");

            // The main bags are enough, but there is more elsewhere (retainers, saddlebag, FC chest): say so, and where.
            var locs = data.AllLocations;
            if (locs.Count > 1)
            {
                locText     += " +";
                TextTooltip = string.Join("\n", locs.Select(FormatLocation));
                ItemTooltip = 0;
            }
            else
            {
                TextTooltip = string.Empty;
                ItemTooltip = ing.ItemId;
            }
        }
        else
        {
            // Yellow: item exists but some or all is in other inventories.
            statusColor = ColorElsewhere;
            var locs = data.AllLocations;

            if (locs.Count > 1)
            {
                // Primary location + "+" marker to indicate the item is spread across multiple inventories.
                var primary = data.PrimaryLocation;
                locText     = (string.IsNullOrEmpty(primary) ? CraftLoc.Get("loc.otherinv") : primary) + " +";
                // Tooltip lists every source with quantity, HQ breakdown and how old that data is.
                TextTooltip = string.Join("\n", locs.Select(FormatLocation));
                ItemTooltip = 0;
            }
            else
            {
                locText     = string.IsNullOrEmpty(data.PrimaryLocation) ? CraftLoc.Get("loc.otherinv") : data.PrimaryLocation;
                TextTooltip = locs.Count == 1 ? FormatLocation(locs[0]) : null;
                ItemTooltip = ing.ItemId;
            }
        }

        // A "Craft first" row (the only one with its own qty text) is something to do, not something missing: gold.
        // Set on every fill, so a recycled row gets its own color back.
        if (data.QtyText is not null) statusColor = ColorCaret;

        _name.TextColor     = statusColor;
        _qty.TextColor      = statusColor;
        _location.String    = locText;
        _location.TextColor = ColorSubtitle;

        // My List totals: the price column, and the unit price under the locations when the row shows them.
        _hasPrice        = data.Price is not null;
        _price.String    = data.Price ?? string.Empty;
        _price.IsVisible = _hasPrice;
        if (!string.IsNullOrEmpty(data.ExtraTip))
        {
            // By source: where it is gathered, and so on, under the locations and over the unit price.
            var lines = data.AllLocations.Select(FormatLocation).Append(data.ExtraTip);
            if (!string.IsNullOrEmpty(data.PriceTip)) lines = lines.Append(data.PriceTip);
            TextTooltip = string.Join("\n", lines);
            ItemTooltip = 0;
        }
        else if (!string.IsNullOrEmpty(data.PriceTip) && ItemTooltip == 0)
            TextTooltip = string.Join("\n", data.AllLocations.Select(FormatLocation)) + "\n" + data.PriceTip;

        // Re-run layout since the indent depends on the row's data.
        Layout();
    }

    // "Retainer - Bags: 5 (2 HQ)  ·  3 h ago" (the age only when that place was read a while ago)
    private static string FormatLocation((string Location, int Total, int Hq, DateTime Seen) l)
    {
        var age = CraftLoc.Age(l.Seen);
        return $"{l.Location}: {FormatQty(l.Total, l.Hq)}" + (age.Length > 0 ? $"  ·  {age}" : string.Empty);
    }

    // "5"  →  all NQ
    // "3 HQ"  →  all HQ
    // "5 (2 HQ)"  →  mixed
    private static string FormatQty(int total, int hq) => hq switch
    {
        0                  => $"{total}",
        _ when hq == total => $"{total} HQ",
        _                  => $"{total} ({hq} HQ)",
    };
}
