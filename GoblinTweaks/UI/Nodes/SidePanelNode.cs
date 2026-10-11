using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>An invisible overlay node over the title of a table: when it can move, dragging it moves the table.</summary>
internal sealed class DragHandleNode : OverlayNode
{
    private readonly ColorImageNode _area;

    public DragHandleNode()
    {
        _area = new ColorImageNode { Color = new Vector4(0f, 0f, 0f, 0.01f) };
        _area.AttachNode(this);
    }

    public override OverlayLayer OverlayLayer => OverlayLayer.AboveUserInterface;

    public bool Movable
    {
        set => EnableMoving = value;
    }

    public void Resize(Vector2 size)
    {
        _area.Size = size;
        Size = size;
    }

    protected override void OnUpdate()
    {
    }
}

/// <summary>
/// A table next to the main one: a header with a title, and lines with a bar, job and two texts. It shows the extra numbers,
/// the history of fights and the details of one. It can stay docked to the main table, or (with a pin button) be set
/// free and dragged by its title.
/// </summary>
internal sealed class SidePanelNode : OverlayNode
{
    private static readonly Vector4 Panel   = new(0.05f, 0.05f, 0.06f, 1f);
    private static readonly Vector4 Glyph   = new(0.92f, 0.86f, 0.68f, 1f);

    private readonly ColorImageNode _panel;
    private readonly ColorImageNode _header;
    private readonly TextNode _title;
    private readonly CollisionNode _back;
    private readonly CollisionNode? _pinArea;
    private readonly ResNode? _pinGlyph;
    private readonly MeterRow[] _rows = new MeterRow[DamageMeterNode.MaxRows];
    private readonly int _columns;
    private readonly TextNode[] _heads = [];
    private float _width;
    private bool _names = true;
    private bool _open;
    private float _opacity = 0.62f;
    private float _height = DamageMeterNode.HeaderH;

    /// <summary>Clicked a line, by its position.</summary>
    public Action<int>? OnRow { get; set; }

    /// <summary>Clicked the header (when it was made clickable).</summary>
    public Action? OnHeader { get; set; }

    /// <summary>Clicked the pin button.</summary>
    public Action? OnPin { get; set; }

    /// <summary>The overlay node that drags the table when it is free. Add it to the overlay next to this node.</summary>
    public DragHandleNode? Handle { get; }

    public SidePanelNode(float width, bool pin, int columns = 0)
    {
        _width = width;
        _columns = columns;
        Handle = pin ? new DragHandleNode() : null;

        _panel = new ColorImageNode { Color = Panel, Position = new Vector2(0f, DamageMeterNode.HeaderH), Size = new Vector2(width, 0f), IsVisible = false };
        _panel.AttachNode(this);
        _header = new ColorImageNode { Color = DamageMeterNode.HeaderColor, Size = new Vector2(width, DamageMeterNode.HeaderH), IsVisible = false };
        _header.AttachNode(this);
        _title = MeterRows.Text(new Vector2(8f, 3f), new Vector2(width - 16f - (pin ? DamageMeterNode.ButtonSize + 4f : 0f), DamageMeterNode.HeaderH - 4f), AlignmentType.Left, 15);
        _title.AttachNode(this);

        for (var i = 0; i < _rows.Length; i++)
        {
            var index = i;
            _rows[i] = MeterRows.Build(this, width, i, () => OnRow?.Invoke(index), columns);
        }

        if (columns > 0)
        {
            _heads = [.. Enumerable.Range(0, columns).Select(_ => MeterRows.Text(new Vector2(0f, 3f), new Vector2(40f, DamageMeterNode.HeaderH - 4f), AlignmentType.Center, 15))];
            foreach (var head in _heads) head.AttachNode(this);
        }

        _back = new CollisionNode { CollisionType = CollisionType.Hit, Size = new Vector2(width, DamageMeterNode.HeaderH), IsVisible = false };
        MeterRows.Click(_back, this, () => OnHeader?.Invoke());

        if (pin)
        {
            var size = DamageMeterNode.ButtonSize;
            var at   = new Vector2(width - size - 4f, (DamageMeterNode.HeaderH - size) / 2f);
            _pinGlyph = new ResNode { Position = at, Size = new Vector2(size, size), IsVisible = false };
            _pinGlyph.AttachNode(this);
            Piece(_pinGlyph, 2f, 6f, 10f, 10f, Glyph);                         // a box
            Piece(_pinGlyph, 4f, 8f, 6f, 6f, DamageMeterNode.HeaderColor);
            Piece(_pinGlyph, 9f, 2f, 7f, 7f, Glyph);                           // and what comes out of it
            Piece(_pinGlyph, 11f, 4f, 3f, 3f, DamageMeterNode.HeaderColor);

            _pinArea = new CollisionNode { CollisionType = CollisionType.Hit, Position = at, Size = new Vector2(size, size), IsVisible = false };
            MeterRows.Click(_pinArea, this, () => OnPin?.Invoke());
        }

        Relayout();
        Size = new Vector2(width, DamageMeterNode.HeaderH);
        Handle?.Resize(new Vector2(width - (pin ? DamageMeterNode.ButtonSize + 8f : 0f), DamageMeterNode.HeaderH));
        IsVisible = false;
        if (Handle is not null) Handle.IsVisible = false;
    }

    public override OverlayLayer OverlayLayer => OverlayLayer.AboveUserInterface;

    /// <summary>Whether the table is on screen.</summary>
    public bool Open
    {
        set
        {
            _open = value;
            IsVisible = value;
            _header.IsVisible = value;
            _title.IsVisible  = value && _columns == 0;
            foreach (var head in _heads) head.IsVisible = value;
            if (_pinGlyph is not null) _pinGlyph.IsVisible = value;
            if (_pinArea is not null) _pinArea.IsVisible = value;
            if (!value)
            {
                _panel.IsVisible = false;
                _back.IsVisible  = false;
                foreach (var row in _rows) row.Visible = false;
                if (Handle is not null) Handle.IsVisible = false;
            }
        }
    }

    /// <summary>Free of the main table: its title drags it. Docked, it does not.</summary>
    public bool Free
    {
        set
        {
            if (Handle is null) return;
            Handle.Movable   = value;
            Handle.IsVisible = value && _open;
        }
    }

    /// <summary>How wide the table is, and whether its lines show the job and name.</summary>
    public void SetLayout(float width, bool names)
    {
        if (Math.Abs(width - _width) < 0.5f && names == _names) return;

        _width = width;
        _names = names;
        _panel.Width  = width;
        _header.Width = width;
        _back.Width   = width;
        foreach (var row in _rows) MeterRows.Place(row, width, names);
        Relayout();
        Resize();
    }

    /// <summary>The labels over the columns of a table with cells.</summary>
    public void SetColumns(IReadOnlyList<string> labels)
    {
        for (var i = 0; i < _heads.Length && i < labels.Count; i++)
            _heads[i].String = labels[i];
    }

    /// <summary>Puts the labels and the cells of every line in their columns, so each value sits under its label.</summary>
    private void Relayout()
    {
        if (_columns == 0) return;

        var slots = MeterRows.Slots(_columns, _width, _names, _pinArea is null ? 0f : DamageMeterNode.ButtonSize + 8f);
        for (var i = 0; i < _columns; i++)
        {
            _heads[i].X = slots[i].X;
            _heads[i].Width = slots[i].W;
            foreach (var row in _rows)
            {
                row.Cells[i].X = slots[i].X;
                row.Cells[i].Width = slots[i].W;
            }
        }
    }

    public void SetOpacity(float opacity)
    {
        _opacity = Math.Clamp(opacity, 0f, 1f);
        _panel.Color  = Panel with { W = _opacity };
        _header.Color = DamageMeterNode.HeaderColor with { W = _opacity * DamageMeterNode.HeaderShare };
    }

    public void SetFontSize(int size)
    {
        foreach (var row in _rows)
        {
            row.Name.FontSize  = (uint)size;
            row.Value.FontSize = (uint)Math.Max(8, size - 1);
        }
        _title.FontSize = (uint)(size + 1);
    }

    public void SetZoom(float zoom)
    {
        var scale = new Vector2(Math.Clamp(zoom, 0.5f, 2f));
        Scale        = scale;
        if (Handle is not null) Handle.Scale = scale;
    }

    public void SetPinTooltip(string text)
    {
        if (_pinArea is not null) _pinArea.TextTooltip = text;
    }

    /// <summary>The title, and whether clicking it does something (a way back).</summary>
    public void SetHeader(string title, bool clickable)
    {
        MeterRows.Fit(_title, title);
        _back.IsVisible = _open && clickable;
    }

    /// <summary>Fills the table. Its lines can be clicked when <paramref name="clickable"/>.</summary>
    public void SetRows(IReadOnlyList<DamageMeterRow> rows, bool clickable)
    {
        var count = _open ? Math.Min(rows.Count, DamageMeterNode.MaxRows) : 0;
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            row.Visible = i < count;
            if (row.Hit is not null) row.Hit.IsVisible = i < count && clickable;
            if (i >= count) continue;

            MeterRows.Fill(row, rows[i], _width, _names);
        }

        _panel.IsVisible = count > 0;
        _height = DamageMeterNode.HeaderH + (count == 0 ? 0f : 4f + count * DamageMeterNode.RowH);
        _panel.Size = new Vector2(_width, Math.Max(0f, _height - DamageMeterNode.HeaderH));
        Resize();
    }

    private void Resize()
    {
        Size = new Vector2(_width, _height);
        Handle?.Resize(new Vector2(_width - (_pinArea is null ? 0f : DamageMeterNode.ButtonSize + 8f), DamageMeterNode.HeaderH));
        _title.Width = _width - 16f - (_pinArea is null ? 0f : DamageMeterNode.ButtonSize + 4f);
        if (_pinGlyph is not null && _pinArea is not null)
        {
            var at = new Vector2(_width - DamageMeterNode.ButtonSize - 4f, _pinGlyph.Y);
            _pinGlyph.Position = at;
            _pinArea.Position  = at;
        }
    }

    private static void Piece(ResNode parent, float x, float y, float w, float h, Vector4 color)
        => new ColorImageNode { Color = color, Position = new Vector2(x, y), Size = new Vector2(w, h) }.AttachNode(parent);

    protected override void OnUpdate()
    {
    }
}
