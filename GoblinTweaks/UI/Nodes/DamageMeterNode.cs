using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>One line of the damage meter: who, what job, how much, and how much of the largest.</summary>
internal readonly record struct DamageMeterRow(string Name, uint JobId, string Value, float Fraction, Vector4 Color, string[]? Cells = null, uint IconId = 0);

/// <summary>
/// The part of the table that is dragged: the title, on its own overlay node so that only the title moves the table
/// (when it is unlocked) and the buttons and bars next to it stay clickable.
/// </summary>
internal sealed class DamageMeterHandleNode : OverlayNode
{
    private readonly ColorImageNode _background;
    private readonly TextNode _title;
    private string _full = string.Empty;
    private bool _truncated;

    public DamageMeterHandleNode(float width, float height)
    {
        _background = new ColorImageNode { Color = DamageMeterNode.HeaderColor, Size = new Vector2(width, height) };
        _background.AttachNode(this);

        _title = new TextNode
        {
            Position         = new Vector2(8f, 3f),
            Size             = new Vector2(width - 12f, height - 4f),
            AlignmentType    = AlignmentType.Left,
            FontSize         = 15,
            TextColor        = new Vector4(1f, 1f, 1f, 1f),
            TextOutlineColor = new Vector4(0.05f, 0.05f, 0.05f, 1f),
        };
        _title.AddTextFlags(TextFlags.Edge);
        _title.AttachNode(this);

        Size = new Vector2(width, height);
    }

    public override OverlayLayer OverlayLayer => OverlayLayer.AboveUserInterface;

    /// <summary>The whole title, even if what shows had to be cut with "...".</summary>
    public string FullTitle => _full;

    /// <summary>The title; if it does not fit it is cut with "...".</summary>
    public string Title
    {
        set
        {
            if (value == _full) return;
            _full = value;
            Refit();
        }
    }

    public bool Movable
    {
        set => EnableMoving = value;
    }

    public int FontSize
    {
        set
        {
            if (_title.FontSize == (uint)value) return;
            _title.FontSize = (uint)value;
            Refit();
        }
    }

    public float Opacity
    {
        set => _background.Color = DamageMeterNode.HeaderColor with { W = value * DamageMeterNode.HeaderShare };
    }

    private void Refit() => _truncated = MeterRows.Fit(_title, _full);

    protected override void OnUpdate()
    {
    }
}

/// <summary>
/// The table of the damage meter: a header with the title and four buttons, and one bar per player, the longest for
/// whoever did the most. It sits on the overlay layer, over the game's windows. The title (<see cref="Handle"/>) is what
/// drags it, when unlocked. The buttons are plain nodes with a hit area, not button components: a component belonging
/// to no window of the game made the game's input focus crash.
/// </summary>
internal sealed class DamageMeterNode : OverlayNode
{
    public const int MaxRows = 24;

    public const float PanelWidth = 340f;
    public const float HeaderH    = 26f;
    public const float RowH       = 22f;
    public const float IconSize   = 20f;
    public const float ButtonSize = 18f;
    private const float ButtonGap = 3f;
    private const int Buttons     = 7;
    public const float SideGap    = 4f;

    /// <summary>The header is this much of the opacity of the rest of the table.</summary>
    public const float HeaderShare = 0.5f;

    public static readonly Vector4 HeaderColor = new(0.05f, 0.05f, 0.06f, 1f);

    private static readonly Vector4 White   = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Outline = new(0.05f, 0.05f, 0.05f, 1f);
    private static readonly Vector4 Panel   = new(0.05f, 0.05f, 0.06f, 1f);
    private static readonly Vector4 TrackBg = new(0f, 0f, 0f, 0.35f);
    private static readonly Vector4 Glyph   = new(0.92f, 0.86f, 0.68f, 1f);
    private static readonly Vector4 Leaf    = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Cross   = new(1f, 0.45f, 0.40f, 1f);

    private readonly ColorImageNode _panel;
    private readonly ColorImageNode _buttonsBackground;
    private readonly CollisionNode _titleTip;
    private readonly float _titleWidth;
    private bool _locked;
    private string _titleTooltip = string.Empty;
    private readonly ResNode _lockOpen;
    private readonly ResNode _lockClosed;
    private readonly MeterRow[] _rows = new MeterRow[MaxRows];
    private readonly List<CollisionNode> _hitAreas = [];
    private float _opacity = 0.62f;
    private int _fontSize = 14;

    /// <summary>Clicked the leaf: switches the table between damage and healing.</summary>
    public Action? OnToggleMode { get; set; }

    /// <summary>Clicked the padlock: locks or unlocks the table.</summary>
    public Action? OnToggleLock { get; set; }

    /// <summary>Clicked the cog: opens the settings.</summary>
    public Action? OnOpenSettings { get; set; }

    /// <summary>Clicked the X: clears the table.</summary>
    public Action? OnClear { get; set; }

    /// <summary>Clicked the plus: opens or closes the extra table on the right (deaths, dispels).</summary>
    public Action? OnToggleExtra { get; set; }

    /// <summary>Clicked the list: opens or closes the history of fights.</summary>
    public Action? OnToggleHistory { get; set; }

    /// <summary>Clicked the two sheets: copies the table.</summary>
    public Action? OnCopy { get; set; }

    /// <summary>Clicked a line of the table (its index, from the top).</summary>
    public Action<int>? OnRow { get; set; }

    /// <summary>Whether the lines of the table can be clicked.</summary>
    public bool RowsClickable { get; set; }

    /// <summary>The title, which is also what drags the table. Add it to the overlay next to this node.</summary>
    public DamageMeterHandleNode Handle { get; }

    public DamageMeterNode()
    {
        var buttonsW = Buttons * ButtonSize + (Buttons - 1) * ButtonGap + 8f;
        var titleW   = PanelWidth - buttonsW;
        Handle = new DamageMeterHandleNode(titleW, HeaderH);
        _titleWidth = titleW;

        // The rows' backdrop starts under the header; the header has its own, lighter one.
        _panel = new ColorImageNode { Color = Panel, Position = new Vector2(0f, HeaderH), Size = new Vector2(PanelWidth, 0f), IsVisible = false };
        _panel.AttachNode(this);

        _buttonsBackground = new ColorImageNode { Color = HeaderColor, Position = new Vector2(titleW, 0f), Size = new Vector2(buttonsW, HeaderH) };
        _buttonsBackground.AttachNode(this);

        for (var i = 0; i < MaxRows; i++)
        {
            var index = i;
            _rows[i] = MeterRows.Build(this, PanelWidth, i, () => OnRow?.Invoke(index));
        }

        // Header buttons, left to right: leaf, plus, list, padlock, cog, sheets, X.
        var y0 = (HeaderH - ButtonSize) / 2f;
        float X(int index) => titleW + 4f + index * (ButtonSize + ButtonGap);

        DrawLeaf(new Vector2(X(0), y0));
        DrawPlus(new Vector2(X(1), y0));
        DrawList(new Vector2(X(2), y0));
        (_lockOpen, _lockClosed) = DrawPadlock(new Vector2(X(3), y0));
        DrawCog(new Vector2(X(4), y0));
        DrawCopy(new Vector2(X(5), y0));
        DrawCross(new Vector2(X(6), y0));

        AddHitArea(new Vector2(X(0), y0), () => OnToggleMode?.Invoke());
        AddHitArea(new Vector2(X(1), y0), () => OnToggleExtra?.Invoke());
        AddHitArea(new Vector2(X(2), y0), () => OnToggleHistory?.Invoke());
        AddHitArea(new Vector2(X(3), y0), () => OnToggleLock?.Invoke());
        AddHitArea(new Vector2(X(4), y0), () => OnOpenSettings?.Invoke());
        AddHitArea(new Vector2(X(5), y0), () => OnCopy?.Invoke());
        AddHitArea(new Vector2(X(6), y0), () => OnClear?.Invoke());

        // Over the end of the title, where it is cut: it shows the title whole. It is on this node, not on the one that drags,
        // and when the table can be dragged it leaves the start of the title free for it.
        _titleTip = new CollisionNode { CollisionType = CollisionType.Hit, IsVisible = false };
        MeterRows.Click(_titleTip, this, () => { });   // an area only gets tooltips if it listens to something

        Locked = false;
        Size = new Vector2(PanelWidth, HeaderH);
    }

    public override OverlayLayer OverlayLayer => OverlayLayer.AboveUserInterface;

    public string Title
    {
        set
        {
            Handle.Title = value;
            PlaceTitleTip();
        }
    }

    /// <summary>What the tooltip of the title says (the enemy the table is about); empty for no tooltip.</summary>
    public string TitleTooltip
    {
        set
        {
            _titleTooltip = value;
            PlaceTitleTip();
        }
    }

    private void PlaceTitleTip()
    {
        var cut   = _titleTooltip;
        var start = _locked ? 0f : _titleWidth * 0.55f;
        _titleTip.Position    = new Vector2(start, 0f);
        _titleTip.Size        = new Vector2(_titleWidth - start, HeaderH);
        _titleTip.TextTooltip = cut;
        _titleTip.IsVisible   = cut.Length > 0;
    }

    /// <summary>A locked table cannot be dragged, so its buttons and bars never start a drag by mistake.</summary>
    public bool Locked
    {
        set
        {
            _locked = value;
            Handle.Movable = !value;
            PlaceTitleTip();
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
            _buttonsBackground.Color = HeaderColor with { W = _opacity * HeaderShare };
            Handle.Opacity = _opacity;
        }
    }

    /// <summary>The tooltips of the buttons, in order: leaf, plus, list, padlock, cog, sheets, X.</summary>
    public void SetTooltips(IReadOnlyList<string> texts)
    {
        for (var i = 0; i < _hitAreas.Count && i < texts.Count; i++)
            _hitAreas[i].TextTooltip = texts[i];
    }

    /// <summary>Size of the letters of the bars; the title is one point bigger and the amounts one smaller.</summary>
    public int FontSize
    {
        set
        {
            value = Math.Clamp(value, 8, 24);
            if (value == _fontSize) return;

            _fontSize = value;
            foreach (var row in _rows)
            {
                row.Name.FontSize  = (uint)value;
                row.Value.FontSize = (uint)Math.Max(8, value - 1);
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

    /// <summary>Shows the rows: the table is as tall as they are.</summary>
    public void SetRows(IReadOnlyList<DamageMeterRow> rows)
    {
        var count = Math.Min(rows.Count, MaxRows);
        for (var i = 0; i < MaxRows; i++)
        {
            var row = _rows[i];
            row.Visible = i < count;
            if (row.Hit is not null) row.Hit.IsVisible = i < count && RowsClickable;
            if (i >= count) continue;

            var data = rows[i];
            MeterRows.Fill(row, data, PanelWidth, true);
        }

        var height = HeaderH + (count == 0 ? 0f : 4f + count * RowH);
        _panel.IsVisible = count > 0;
        _panel.Size = new Vector2(PanelWidth, Math.Max(0f, height - HeaderH));
        Size = new Vector2(PanelWidth, height);
    }

    protected override void OnUpdate()
    {
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private unsafe void AddHitArea(Vector2 position, Action onClick)
    {
        var area = new CollisionNode
        {
            CollisionType = CollisionType.Hit,
            Position      = position,
            Size          = new Vector2(ButtonSize, ButtonSize),
        };
        MeterRows.Click(area, this, onClick);
        _hitAreas.Add(area);
    }

    private ColorImageNode Piece(ResNode parent, float x, float y, float w, float h, Vector4 color, float rotation = 0f)
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

    /// <summary>A leaf: a long diamond leaning to the right, with its stem.</summary>
    private void DrawLeaf(Vector2 position)
    {
        var host = Glyphs(position);
        Piece(host, 8f, 2f, 5f, 12f, Leaf, 0.7f);
        Piece(host, 3f, 14f, 2f, 3f, Glyph, 0.7f);
    }

    /// <summary>A padlock, in two states: closed, and open with the shackle lifted on one side.</summary>
    private (ResNode Open, ResNode Closed) DrawPadlock(Vector2 position)
    {
        ResNode Body(bool closed)
        {
            var host = Glyphs(position);
            Piece(host, 3f, 8f, 12f, 9f, Glyph);                  // body
            Piece(host, 8f, 11f, 2f, 3f, HeaderColor);             // keyhole
            Piece(host, 5f, closed ? 2f : 0f, 2f, closed ? 6f : 7f, Glyph);     // left leg
            Piece(host, 11f, 2f, 2f, closed ? 6f : 3f, Glyph);                  // right leg
            Piece(host, 5f, closed ? 2f : 0f, 8f, 2f, Glyph);                   // top bar
            return host;
        }
        return (Body(false), Body(true));
    }

    private void DrawPlus(Vector2 position)
    {
        var host = Glyphs(position);
        Piece(host, 8f, 2f, 3f, 14f, Glyph);
        Piece(host, 2f, 8f, 14f, 3f, Glyph);
    }

    /// <summary>A list: three lines with a dot before each.</summary>
    private void DrawList(Vector2 position)
    {
        var host = Glyphs(position);
        for (var i = 0; i < 3; i++)
        {
            Piece(host, 2f, 3f + i * 5f, 3f, 3f, Glyph);
            Piece(host, 7f, 3.5f + i * 5f, 9f, 2f, Glyph);
        }
    }

    /// <summary>A cog: a ring with teeth.</summary>
    private void DrawCog(Vector2 position)
    {
        var host = Glyphs(position);
        Piece(host, 8f, 1f, 2f, 16f, Glyph);                // vertical teeth
        Piece(host, 1f, 8f, 16f, 2f, Glyph);                // horizontal teeth
        Piece(host, 8f, 1f, 2f, 16f, Glyph, 0.785f);        // diagonal teeth
        Piece(host, 1f, 8f, 16f, 2f, Glyph, 0.785f);
        Piece(host, 4f, 4f, 10f, 10f, Glyph);               // ring
        Piece(host, 7f, 7f, 4f, 4f, HeaderColor);           // hole
    }

    /// <summary>Two sheets, one over the other: copy.</summary>
    private void DrawCopy(Vector2 position)
    {
        var host = Glyphs(position);
        Piece(host, 2f, 2f, 10f, 11f, Glyph);           // the sheet behind
        Piece(host, 3.5f, 3.5f, 7f, 8f, HeaderColor);
        Piece(host, 6f, 5f, 10f, 11f, Glyph);           // the sheet in front
        Piece(host, 7.5f, 6.5f, 7f, 8f, HeaderColor);
    }

    private void DrawCross(Vector2 position)
    {
        var host = Glyphs(position);
        Piece(host, 8f, 2f, 3f, 14f, Cross, 0.785f);
        Piece(host, 8f, 2f, 3f, 14f, Cross, -0.785f);
    }

    private TextNode MakeText(Vector2 position, Vector2 size, AlignmentType alignment, uint fontSize)
    {
        var text = new TextNode
        {
            Position         = position,
            Size             = size,
            AlignmentType    = alignment,
            FontSize         = fontSize,
            TextColor        = White,
            TextOutlineColor = Outline,
            IsVisible        = false,
        };
        text.AddTextFlags(TextFlags.Edge);
        return text;
    }
}
