using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// One line of Goblin Hunts: badge (S/A/F), players nearby, name, percent, zone, world, and the map and teleport buttons.
/// <paramref name="Fraction"/> is how much of the row the bar behind it covers.
/// </summary>
internal readonly record struct HuntRow(
    string Badge, Vector4 BadgeColor, string Players, string Name, string Percent, string Zone, string World,
    bool OtherWorld, float Fraction, Vector4 BarColor, bool Struck, bool CanTeleport);

/// <summary>
/// The table of Goblin Hunts, built like the damage meter's: a header with the title (<see cref="Handle"/>, which drags it),
/// the filter button, padlock, cog and X; one line per hunt under it. Plain nodes with hit areas, no button components
/// (see <see cref="DamageMeterNode"/>). Behind the game's windows.
/// </summary>
internal sealed class HuntTableNode : OverlayNode
{
    public const int MaxRows = 24;
    public const float PanelWidth = 420f;
    public const float HeaderH    = 26f;
    public const float RowH       = 22f;
    private const float ButtonSize = 18f;
    private const float ButtonGap  = 3f;
    private const float FilterW    = 42f;
    private const float ScrollBarW = 3f;

    // Columns of a line, from the approved sketch.
    private const float BadgeX = 4f, PlayersX = 26f, PlayersW = 32f, NameX = 58f, NameW = 118f, PctX = 176f, PctW = 42f;
    private const float ZoneX = 224f, ZoneW = 94f, WorldX = 320f, WorldW = 54f, MapX = 377f, TpX = 398f;

    public static readonly Vector4 BadgeS    = Hex(0xB58A2A);
    public static readonly Vector4 BadgeA    = Hex(0x3D6FB5);
    public static readonly Vector4 BadgeF    = Hex(0x7B3FB5);
    public static readonly Vector4 BarGreen  = Hex(0x2E8B3E, 0.55f);
    public static readonly Vector4 BarOrange = Hex(0xC7761A, 0.55f);
    public static readonly Vector4 BarRed    = Hex(0xA02828, 0.55f);
    public static readonly Vector4 BarPurple = Hex(0x7B3FB5, 0.55f);

    private static readonly Vector4 White      = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Dim        = Hex(0x999999);
    private static readonly Vector4 ZoneColor  = Hex(0xCCCCCC);
    private static readonly Vector4 WorldColor = Hex(0x99AABB);
    private static readonly Vector4 OtherWorldColor = Hex(0xF0C060);
    private static readonly Vector4 MapColor   = Hex(0x3A6A8A);
    private static readonly Vector4 TpColor    = Hex(0xC77A1A);
    private static readonly Vector4 OffColor   = Hex(0x444444);
    private static readonly Vector4 OffGlyph   = Hex(0x777777);
    private static readonly Vector4 FilterBg   = Hex(0x2C4A6A);
    private static readonly Vector4 FilterEdge = Hex(0x4A7AA8);
    private static readonly Vector4 Panel      = new(0.05f, 0.05f, 0.06f, 1f);
    private static readonly Vector4 TrackBg    = new(0f, 0f, 0f, 0.35f);
    private static readonly Vector4 Glyph      = new(0.92f, 0.86f, 0.68f, 1f);
    private static readonly Vector4 Cross      = new(1f, 0.45f, 0.40f, 1f);

    private sealed class Line
    {
        public required ColorImageNode Track, Bar, BadgeBg, MapBg, TpBg, NameStrike, ZoneStrike;
        public required TextNode Badge, Players, Name, Percent, Zone, World;
        public required ColorImageNode[] MapGlyph, TpGlyph;
        public required CollisionNode RowHit, MapHit, TpHit;

        public bool Visible
        {
            set
            {
                foreach (var n in new NodeBase[] { Track, Bar, BadgeBg, Badge, Players, Name, Percent, Zone, World, MapBg, TpBg, RowHit, MapHit, TpHit })
                    n.IsVisible = value;
                foreach (var g in MapGlyph) g.IsVisible = value;
                foreach (var g in TpGlyph) g.IsVisible = value;
                if (!value) NameStrike.IsVisible = ZoneStrike.IsVisible = false;
            }
        }
    }

    private readonly ColorImageNode _panel;
    private readonly ColorImageNode _buttonsBackground;
    private readonly CollisionNode _wheelArea;
    private readonly ColorImageNode _scrollTrack;
    private readonly ColorImageNode _scrollThumb;
    private readonly TextNode _filterText;
    private readonly ResNode _lockOpen;
    private readonly ResNode _lockClosed;
    private readonly List<CollisionNode> _headerHits = [];
    private readonly Line[] _lines = new Line[MaxRows];
    private float _opacity = 0.62f;
    private int _fontSize = 14;

    /// <summary>Clicked the filter button: All → S → A → FATE.</summary>
    public Action? OnToggleFilter { get; set; }
    public Action? OnToggleLock { get; set; }
    public Action? OnOpenSettings { get; set; }
    public Action? OnClose { get; set; }

    /// <summary>Clicked the map button of a line (its index among the lines shown).</summary>
    public Action<int>? OnMap { get; set; }

    /// <summary>Clicked the teleport button of a line (its index among the lines shown).</summary>
    public Action<int>? OnTeleport { get; set; }

    /// <summary>Turned the mouse wheel over the lines: +1 up, -1 down.</summary>
    public Action<int>? OnScroll { get; set; }

    /// <summary>The title, which is also what drags the table. Add it to the overlay next to this node.</summary>
    public DamageMeterHandleNode Handle { get; }

    public HuntTableNode()
    {
        var buttonsW = FilterW + 3 * ButtonSize + 4 * ButtonGap + 8f;
        var titleW   = PanelWidth - buttonsW;
        Handle = new DamageMeterHandleNode(titleW, HeaderH);

        _panel = new ColorImageNode { Color = Panel, Position = new Vector2(0f, HeaderH), Size = new Vector2(PanelWidth, 0f), IsVisible = false };
        _panel.AttachNode(this);

        _wheelArea = new CollisionNode { CollisionType = CollisionType.Hit, Position = new Vector2(0f, HeaderH), Size = new Vector2(PanelWidth, 0f), IsVisible = false };
        MeterRows.Wheel(_wheelArea, direction => OnScroll?.Invoke(direction));
        _wheelArea.AttachNode(this);

        _buttonsBackground = new ColorImageNode { Color = DamageMeterNode.HeaderColor, Position = new Vector2(titleW, 0f), Size = new Vector2(buttonsW, HeaderH) };
        _buttonsBackground.AttachNode(this);

        for (var i = 0; i < MaxRows; i++) _lines[i] = BuildLine(i);

        _scrollTrack = new ColorImageNode { Color = TrackBg, Position = new Vector2(PanelWidth - ScrollBarW - 1f, HeaderH + 2f), Size = new Vector2(ScrollBarW, 0f), IsVisible = false };
        _scrollTrack.AttachNode(this);
        _scrollThumb = new ColorImageNode { Color = Glyph, Position = new Vector2(PanelWidth - ScrollBarW - 1f, HeaderH + 2f), Size = new Vector2(ScrollBarW, 0f), IsVisible = false };
        _scrollThumb.AttachNode(this);

        // Header, left to right: filter, padlock, cog, X.
        var y0 = (HeaderH - ButtonSize) / 2f;
        var fx = titleW + 4f;
        Piece(this, fx, y0, FilterW, ButtonSize, FilterEdge);
        Piece(this, fx + 1f, y0 + 1f, FilterW - 2f, ButtonSize - 2f, FilterBg);
        _filterText = MeterRows.Text(new Vector2(fx, y0 + 1f), new Vector2(FilterW, ButtonSize - 2f), AlignmentType.Center, 12);
        _filterText.IsVisible = true;
        _filterText.AttachNode(this);
        float X(int index) => fx + FilterW + ButtonGap + index * (ButtonSize + ButtonGap);

        (_lockOpen, _lockClosed) = DrawPadlock(new Vector2(X(0), y0));
        DrawCog(new Vector2(X(1), y0));
        DrawCross(new Vector2(X(2), y0));

        AddHeaderHit(new Vector2(fx, y0), FilterW, () => OnToggleFilter?.Invoke());
        AddHeaderHit(new Vector2(X(0), y0), ButtonSize, () => OnToggleLock?.Invoke());
        AddHeaderHit(new Vector2(X(1), y0), ButtonSize, () => OnOpenSettings?.Invoke());
        AddHeaderHit(new Vector2(X(2), y0), ButtonSize, () => OnClose?.Invoke());

        Locked = false;
        Size = new Vector2(PanelWidth, HeaderH);
    }

    public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;

    public string Title { set => Handle.Title = value; }

    public string FilterLabel { set => _filterText.String = value; }

    public bool Locked
    {
        set
        {
            Handle.Movable = !value;
            _lockClosed.IsVisible = value;
            _lockOpen.IsVisible   = !value;
        }
    }

    /// <summary>How opaque the backgrounds are, from 0 (none) to 1.</summary>
    public float Opacity
    {
        set
        {
            _opacity = Math.Clamp(value, 0f, 1f);
            _panel.Color = Panel with { W = _opacity };
            _buttonsBackground.Color = DamageMeterNode.HeaderColor with { W = _opacity * DamageMeterNode.HeaderShare };
            Handle.Opacity = _opacity;
        }
    }

    /// <summary>Size of the names; the title is one point bigger and the other columns two smaller.</summary>
    public int FontSize
    {
        set
        {
            value = Math.Clamp(value, 8, 24);
            if (value == _fontSize) return;
            _fontSize = value;
            var small = (uint)Math.Max(8, value - 2);
            foreach (var l in _lines)
            {
                l.Name.FontSize = (uint)value;
                l.Percent.FontSize = (uint)Math.Max(8, value - 1);
                l.Players.FontSize = l.Zone.FontSize = l.World.FontSize = small;
            }
            Handle.FontSize = value + 1;
        }
    }

    /// <summary>Size of the whole table (1 = as designed).</summary>
    public float Zoom
    {
        set
        {
            var scale = new Vector2(Math.Clamp(value, 0.5f, 2f));
            Scale        = scale;
            Handle.Scale = scale;
        }
    }

    public bool Shown
    {
        set => IsVisible = Handle.IsVisible = value;
    }

    /// <summary>Tooltips of the header buttons and of the two buttons of every line.</summary>
    public void SetTooltips(string filter, string lockTip, string settings, string close, string map, string teleport)
    {
        string[] header = [filter, lockTip, settings, close];
        for (var i = 0; i < _headerHits.Count && i < header.Length; i++) _headerHits[i].TextTooltip = header[i];
        foreach (var l in _lines)
        {
            l.MapHit.TextTooltip = map;
            l.TpHit.TextTooltip  = teleport;
        }
    }

    /// <summary>Shows the lines; <paramref name="first"/> and <paramref name="total"/> place them in the whole list for the scroll bar.</summary>
    public void SetRows(IReadOnlyList<HuntRow> rows, int first, int total)
    {
        var hitsBefore = HitsShown();
        var count = Math.Min(rows.Count, MaxRows);
        for (var i = 0; i < MaxRows; i++)
        {
            var l = _lines[i];
            l.Visible = i < count;
            if (i < count) Fill(l, rows[i]);
        }
        PlaceScroll(first, total, count);

        // A hit area with a tooltip loses its collision flag while hidden (KamiToolKit), and the game only knows the
        // areas that had it when it last made its list. Make it again when the buttons shown change, or a button that
        // was hidden then (a dead hunt's teleport) stays dead: no click, no tooltip.
        if (HitsShown() != hitsBefore) RefreshCollisions();
    }

    /// <summary>Sizes the table to its lines and shows the scroll bar when there are more than fit.</summary>
    private void PlaceScroll(int first, int total, int count)
    {
        var height = HeaderH + (count == 0 ? 0f : 4f + count * RowH);
        _panel.IsVisible = count > 0;
        _panel.Size = new Vector2(PanelWidth, Math.Max(0f, height - HeaderH));
        Size = new Vector2(PanelWidth, height);

        var scrolls = count > 0 && total > count;
        _wheelArea.IsVisible   = scrolls;
        _wheelArea.Size        = new Vector2(PanelWidth, Math.Max(0f, height - HeaderH));
        _scrollTrack.IsVisible = _scrollThumb.IsVisible = scrolls;
        if (!scrolls) return;

        var trackH = count * RowH - 2f;
        var thumbH = Math.Max(6f, trackH * count / total);
        var room   = total - count;
        _scrollTrack.Height = trackH;
        _scrollThumb.Height = thumbH;
        _scrollThumb.Y      = HeaderH + 2f + (trackH - thumbH) * Math.Clamp(first, 0, room) / room;
    }

    protected override void OnUpdate()
    {
    }

    /// <summary>Which hit areas are shown now, one character each.</summary>
    private string HitsShown()
    {
        var shown = new char[_lines.Length * 3 + 1];
        for (var i = 0; i < _lines.Length; i++)
        {
            var l = _lines[i];
            shown[i * 3]     = l.RowHit.IsVisible ? '1' : '0';
            shown[i * 3 + 1] = l.MapHit.IsVisible ? '1' : '0';
            shown[i * 3 + 2] = l.TpHit.IsVisible ? '1' : '0';
        }
        shown[^1] = _wheelArea.IsVisible ? '1' : '0';
        return new string(shown);
    }

    /// <summary>Has the overlay window that holds this table make its list of collision nodes again.</summary>
    private unsafe void RefreshCollisions()
    {
        AtkResNode* root = this;
        if (root == null) return;
        while (root->ParentNode != null) root = root->ParentNode;

        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return;
        var list = &stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list->Count; i++)
        {
            var addon = list->Entries[i].Value;
            if (addon == null || addon->RootNode != root) continue;
            addon->UpdateCollisionNodeList(false);
            return;
        }
    }

    private static void Fill(Line l, HuntRow r)
    {
        l.Bar.Color = r.BarColor;
        l.Bar.Width = Math.Max(1f, (PanelWidth - 4f) * Math.Clamp(r.Fraction, 0f, 1f));
        l.BadgeBg.Color = r.BadgeColor;
        l.Badge.String = r.Badge;
        l.Players.String = r.Players;
        l.Players.IsVisible = r.Players.Length > 0;
        MeterRows.Fit(l.Name, r.Name);
        l.Percent.String = r.Percent;
        MeterRows.Fit(l.Zone, r.Zone);
        MeterRows.Fit(l.World, r.World);

        var text = r.Struck ? Dim : White;
        l.Name.TextColor = text;
        l.Zone.TextColor = r.Struck ? Dim : ZoneColor;
        l.World.TextColor = r.OtherWorld ? OtherWorldColor : WorldColor;
        Strike(l.NameStrike, l.Name, r.Struck);
        Strike(l.ZoneStrike, l.Zone, r.Struck);

        l.TpBg.Color = r.CanTeleport ? TpColor : OffColor;
        foreach (var g in l.TpGlyph) g.Color = r.CanTeleport ? White : OffGlyph;
        l.TpHit.IsVisible = r.CanTeleport;
    }

    /// <summary>A line through a text, as long as what it shows.</summary>
    private static void Strike(ColorImageNode line, TextNode text, bool on)
    {
        line.IsVisible = on;
        if (!on) return;
        line.Width = Math.Clamp(text.GetTextDrawSize(false).X, 1f, text.Width);
    }

    private Line BuildLine(int index)
    {
        var y = HeaderH + 2f + index * RowH;
        var h = RowH;
        var ty = y + 2f;
        var th = h - 4f;
        var by = y + (h - 2f - ButtonSize) / 2f;

        var track   = new ColorImageNode { Color = TrackBg, Position = new Vector2(2f, y), Size = new Vector2(PanelWidth - 4f, h - 2f) };
        var bar     = new ColorImageNode { Position = new Vector2(2f, y), Size = new Vector2(1f, h - 2f) };
        var badgeBg = new ColorImageNode { Position = new Vector2(BadgeX, by), Size = new Vector2(ButtonSize, ButtonSize) };
        var badge   = MeterRows.Text(new Vector2(BadgeX, by + 1f), new Vector2(ButtonSize, ButtonSize - 2f), AlignmentType.Center, 12);
        var players = MeterRows.Text(new Vector2(PlayersX, ty), new Vector2(PlayersW, th), AlignmentType.Left, 12);
        var name    = MeterRows.Text(new Vector2(NameX, ty), new Vector2(NameW, th), AlignmentType.Left, 14);
        var pct     = MeterRows.Text(new Vector2(PctX, ty), new Vector2(PctW, th), AlignmentType.Right, 13);
        var zone    = MeterRows.Text(new Vector2(ZoneX, ty), new Vector2(ZoneW, th), AlignmentType.Left, 12);
        var world   = MeterRows.Text(new Vector2(WorldX, ty), new Vector2(WorldW, th), AlignmentType.Left, 12);
        var strikeY = y + (h - 2f) / 2f;
        var nameStrike = new ColorImageNode { Color = Dim, Position = new Vector2(NameX, strikeY), Size = new Vector2(1f, 1f) };
        var zoneStrike = new ColorImageNode { Color = Dim, Position = new Vector2(ZoneX, strikeY), Size = new Vector2(1f, 1f) };
        var mapBg   = new ColorImageNode { Color = MapColor, Position = new Vector2(MapX, by), Size = new Vector2(ButtonSize, ButtonSize) };
        var tpBg    = new ColorImageNode { Color = TpColor, Position = new Vector2(TpX, by), Size = new Vector2(ButtonSize, ButtonSize) };

        // Attach order is draw order: backgrounds, then texts, then glyphs and hit areas on top.
        foreach (var n in new NodeBase[] { track, bar, badgeBg, badge, players, name, pct, zone, world, nameStrike, zoneStrike, mapBg, tpBg })
            n.AttachNode(this);

        // A folded map: three panels, the middle one lower.
        ColorImageNode[] mapGlyph =
        [
            Piece(this, MapX + 3f, by + 4f, 4f, 10f, White),
            Piece(this, MapX + 7f, by + 5f, 4f, 10f, Glyph),
            Piece(this, MapX + 11f, by + 4f, 4f, 10f, White),
        ];
        // A diamond with a light dot: the aetheryte.
        ColorImageNode[] tpGlyph =
        [
            Piece(this, TpX + 5f, by + 5f, 8f, 8f, White, 0.785f),
            Piece(this, TpX + 8f, by + 8f, 2f, 2f, TpColor),
        ];

        // The whole line listens to the wheel, like the lines of the damage meter: a hit area with a click (here doing
        // nothing) and the wheel, no tooltip. Under the two buttons, which are attached after it and so stay clickable.
        var rowHit = new CollisionNode { CollisionType = CollisionType.Hit, Position = new Vector2(2f, y), Size = new Vector2(PanelWidth - 4f, h - 2f) };
        MeterRows.Wheel(rowHit, direction => OnScroll?.Invoke(direction));
        MeterRows.Click(rowHit, this, () => { });

        var mapHit = new CollisionNode { CollisionType = CollisionType.Hit, Position = new Vector2(MapX, by), Size = new Vector2(ButtonSize, ButtonSize) };
        var tpHit  = new CollisionNode { CollisionType = CollisionType.Hit, Position = new Vector2(TpX, by), Size = new Vector2(ButtonSize, ButtonSize) };
        MeterRows.Wheel(mapHit, direction => OnScroll?.Invoke(direction));
        MeterRows.Wheel(tpHit, direction => OnScroll?.Invoke(direction));
        MeterRows.Click(mapHit, this, () => OnMap?.Invoke(index));
        MeterRows.Click(tpHit, this, () => OnTeleport?.Invoke(index));

        var line = new Line
        {
            Track = track, Bar = bar, BadgeBg = badgeBg, MapBg = mapBg, TpBg = tpBg, NameStrike = nameStrike, ZoneStrike = zoneStrike,
            Badge = badge, Players = players, Name = name, Percent = pct, Zone = zone, World = world,
            MapGlyph = mapGlyph, TpGlyph = tpGlyph, RowHit = rowHit, MapHit = mapHit, TpHit = tpHit,
        };
        line.Visible = false;
        return line;
    }

    private void AddHeaderHit(Vector2 position, float width, Action onClick)
    {
        var area = new CollisionNode { CollisionType = CollisionType.Hit, Position = position, Size = new Vector2(width, ButtonSize) };
        MeterRows.Click(area, this, onClick);
        _headerHits.Add(area);
    }

    private static ColorImageNode Piece(NodeBase parent, float x, float y, float w, float h, Vector4 color, float rotation = 0f)
    {
        var piece = new ColorImageNode { Color = color, Position = new Vector2(x, y), Size = new Vector2(w, h) };
        if (rotation != 0f)
        {
            piece.Origin   = new Vector2(w / 2f, h / 2f);
            piece.Rotation = rotation;
        }
        piece.AttachNode(parent);
        return piece;
    }

    private ResNode Glyphs(Vector2 position)
    {
        var host = new ResNode { Position = position, Size = new Vector2(ButtonSize, ButtonSize) };
        host.AttachNode(this);
        return host;
    }

    private (ResNode Open, ResNode Closed) DrawPadlock(Vector2 position)
    {
        ResNode Body(bool closed)
        {
            var host = Glyphs(position);
            Piece(host, 3f, 8f, 12f, 9f, Glyph);
            Piece(host, 8f, 11f, 2f, 3f, DamageMeterNode.HeaderColor);
            Piece(host, 5f, closed ? 2f : 0f, 2f, closed ? 6f : 7f, Glyph);
            Piece(host, 11f, 2f, 2f, closed ? 6f : 3f, Glyph);
            Piece(host, 5f, closed ? 2f : 0f, 8f, 2f, Glyph);
            return host;
        }
        return (Body(false), Body(true));
    }

    private void DrawCog(Vector2 position)
    {
        var host = Glyphs(position);
        Piece(host, 8f, 1f, 2f, 16f, Glyph);
        Piece(host, 1f, 8f, 16f, 2f, Glyph);
        Piece(host, 8f, 1f, 2f, 16f, Glyph, 0.785f);
        Piece(host, 1f, 8f, 16f, 2f, Glyph, 0.785f);
        Piece(host, 4f, 4f, 10f, 10f, Glyph);
        Piece(host, 7f, 7f, 4f, 4f, DamageMeterNode.HeaderColor);
    }

    private void DrawCross(Vector2 position)
    {
        var host = Glyphs(position);
        Piece(host, 8f, 2f, 3f, 14f, Cross, 0.785f);
        Piece(host, 8f, 2f, 3f, 14f, Cross, -0.785f);
    }

    private static Vector4 Hex(uint rgb, float alpha = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
}
