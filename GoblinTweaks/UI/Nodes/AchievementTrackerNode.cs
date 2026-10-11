using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>One line of the achievement tracker: what it is, how far along, and how much of it is done.</summary>
internal readonly record struct TrackerRow(uint IconId, string Name, string Progress, float Fraction, bool Done, bool Near = false);

/// <summary>
/// The tracker of pinned achievements: a header with a title and one bar per achievement with its progress. It sits on the
/// overlay layer and is dragged by any part of it when unlocked. It has no buttons: what is tracked is chosen in the window.
/// </summary>
internal sealed class AchievementTrackerNode : OverlayNode
{
    public const int MaxRows = 12;

    public const float PanelWidth = 340f;
    public const float HeaderH    = 26f;
    public const float RowH       = 24f;
    public const float IconSize   = 22f;

    private static readonly Vector4 White    = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Outline  = new(0.05f, 0.05f, 0.05f, 1f);
    private static readonly Vector4 Panel    = new(0.05f, 0.05f, 0.06f, 1f);
    private static readonly Vector4 Header   = new(0.05f, 0.05f, 0.06f, 1f);
    private static readonly Vector4 TrackBg  = new(0f, 0f, 0f, 0.35f);
    private static readonly Vector4 Bar      = new(0.30f, 0.55f, 0.85f, 0.85f);
    private static readonly Vector4 BarNear  = new(0.90f, 0.72f, 0.20f, 0.85f);
    private static readonly Vector4 BarDone  = new(0.28f, 0.62f, 0.34f, 0.85f);

    private sealed class Row
    {
        public required ColorImageNode Track { get; init; }
        public required ColorImageNode Bar { get; init; }
        public required IconImageNode Icon { get; init; }
        public required TextNode Name { get; init; }
        public required TextNode Progress { get; init; }

        public bool Visible
        {
            set => Track.IsVisible = Bar.IsVisible = Icon.IsVisible = Name.IsVisible = Progress.IsVisible = value;
        }
    }

    private readonly ColorImageNode _panel;
    private readonly ColorImageNode _header;
    private readonly TextNode _title;
    private readonly Row[] _rows = new Row[MaxRows];
    private float _opacity = 0.62f;
    private int _fontSize;
    private bool _shown;
    private bool _visibilityApplied;
    private bool _movable;
    private string _titleText = string.Empty;

    public AchievementTrackerNode()
    {
        _panel = new ColorImageNode { Color = Panel, Position = new Vector2(0f, HeaderH), Size = new Vector2(PanelWidth, 0f), IsVisible = false };
        _panel.AttachNode(this);

        _header = new ColorImageNode { Color = Header, Size = new Vector2(PanelWidth, HeaderH), IsVisible = false };
        _header.AttachNode(this);

        _title = Text(new Vector2(8f, 3f), new Vector2(PanelWidth - 16f, HeaderH - 4f), AlignmentType.Left, 15);
        _title.AttachNode(this);

        for (var i = 0; i < MaxRows; i++)
        {
            var y = HeaderH + 2f + i * RowH;
            var row = new Row
            {
                Track    = new ColorImageNode { Color = TrackBg, Position = new Vector2(2f, y), Size = new Vector2(PanelWidth - 4f, RowH - 2f), IsVisible = false },
                Bar      = new ColorImageNode { Position = new Vector2(2f, y), Size = new Vector2(1f, RowH - 2f), IsVisible = false },
                Icon     = new IconImageNode { FitTexture = true, Position = new Vector2(4f, y), Size = new Vector2(IconSize, IconSize), IsVisible = false },
                Name     = Text(new Vector2(IconSize + 10f, y + 3f), new Vector2(PanelWidth * 0.58f - IconSize - 10f, RowH - 5f), AlignmentType.Left, 13),
                Progress = Text(new Vector2(PanelWidth * 0.58f, y + 3f), new Vector2(PanelWidth * 0.42f - 8f, RowH - 5f), AlignmentType.Right, 13),
            };

            // Attach order is draw order: the bar sits behind the texts.
            row.Track.AttachNode(this);
            row.Bar.AttachNode(this);
            row.Icon.AttachNode(this);
            row.Name.AttachNode(this);
            row.Progress.AttachNode(this);
            row.Visible = false;
            _rows[i] = row;
        }

        Size = new Vector2(PanelWidth, HeaderH);
    }

    public override OverlayLayer OverlayLayer => OverlayLayer.AboveUserInterface;

    public string Title
    {
        set
        {
            if (value == _titleText) return;
            _titleText = value;
            _title.String = value;
            _title.IsVisible = _shown;
        }
    }

    /// <summary>Whether the user can drag it. It only changes when the value changes, and never while the tracker is hidden.</summary>
    public bool Movable
    {
        set
        {
            value &= _shown;
            if (value == _movable) return;
            _movable = value;
            EnableMoving = value;
        }
    }

    /// <summary>
    /// Shows or hides the tracker. The node itself always stays visible, so the game's input never meets a hidden node
    /// that can be moved: what hides is its content, and it cannot be dragged while hidden.
    /// </summary>
    public bool Shown
    {
        set
        {
            if (value == _shown && _visibilityApplied) return;
            _shown = value;
            _visibilityApplied = true;
            _header.IsVisible = _title.IsVisible = value;
            if (!value)
            {
                Movable = false;
                _panel.IsVisible = false;
                foreach (var row in _rows) row.Visible = false;
                Size = new Vector2(1f, 1f);
            }
        }
    }

    public float Opacity
    {
        set
        {
            value = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(value - _opacity) < 0.001f) return;

            _opacity = value;
            _panel.Color  = Panel with { W = _opacity };
            _header.Color = Header with { W = _opacity * 0.5f };
        }
    }

    public int FontSize
    {
        set
        {
            if (value == _fontSize) return;
            _fontSize = value;
            _title.FontSize = (uint)(value + 1);
            foreach (var row in _rows)
            {
                row.Name.FontSize     = (uint)value;
                row.Progress.FontSize = (uint)Math.Max(8, value - 1);
            }
        }
    }

    public void SetRows(IReadOnlyList<TrackerRow> rows)
    {
        if (!_shown) return;

        var count = Math.Min(rows.Count, MaxRows);
        for (var i = 0; i < MaxRows; i++)
        {
            var row = _rows[i];
            row.Visible = i < count;
            if (i >= count) continue;

            var data = rows[i];
            row.Icon.IconId     = data.IconId;
            row.Name.String     = data.Name;
            row.Progress.String = data.Progress;
            row.Bar.Color       = data.Done ? BarDone : data.Near ? BarNear : Bar;
            row.Bar.Width       = Math.Max(1f, (PanelWidth - 4f) * Math.Clamp(data.Fraction, 0f, 1f));
        }

        var height = HeaderH + (count == 0 ? 0f : 4f + count * RowH);
        _panel.IsVisible = count > 0;
        _panel.Size      = new Vector2(PanelWidth, Math.Max(0f, height - HeaderH));
        Size             = new Vector2(PanelWidth, height);
    }

    protected override void OnUpdate()
    {
    }

    private static TextNode Text(Vector2 position, Vector2 size, AlignmentType alignment, uint fontSize)
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
        text.AddTextFlags(TextFlags.Edge | TextFlags.Ellipsis);
        return text;
    }
}
