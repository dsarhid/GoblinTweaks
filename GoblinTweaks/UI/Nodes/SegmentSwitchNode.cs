using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Nodes;
using KamiToolKit.Nodes.Simplified;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// The game's own switch between a few views of one window, as in Character → Classes/Jobs
/// (DoW/DoM | DoH/DoL): a framed bar of segments, the selected one lit in gold.
/// </summary>
/// <remarks>
/// The game has no component for it: its windows build it from the pieces of
/// <c>ui/uld/ToggleButton.tex</c>, and so does this node. The pieces and their sizes are those of the
/// CharacterClass window, plus the middle piece the texture has for switches of more than two segments.
/// </remarks>
internal sealed unsafe class SegmentSwitchNode : ResNode
{
    private const string Texture = "ui/uld/ToggleButton.tex";

    /// <summary>Height of the switch: that of its frame in the game's windows.</summary>
    public const float SwitchHeight = 29f;

    private const float SegmentY = 3f;
    private const float SegmentH = 24f;
    private const float PieceH   = 32f;
    private const float LitV     = 32f;   // the lit end pieces are right under the unlit ones

    private sealed record Segment(SimpleNineGridNode Unlit, SimpleNineGridNode Lit);

    private readonly List<Segment> _segments = [];

    /// <param name="labels">The text of each segment, left to right; at least two.</param>
    public SegmentSwitchNode(IReadOnlyList<string> labels, float width)
    {
        Size = new Vector2(width, SwitchHeight);

        var segmentW = (width - 6f) / labels.Count;
        for (var i = 0; i < labels.Count; i++)
        {
            var index    = i;
            var position = new Vector2(2f + i * segmentW, SegmentY);
            var size     = new Vector2(segmentW, SegmentH);

            // Left end, right end, or one of the middle ones: each has its own piece, unlit and lit.
            Vector2 unlit, lit;
            float pieceW, left, right;
            if (i == 0)                     (unlit, lit, pieceW, left, right) = (new(0f, 0f),    new(0f, LitV),    25f, 16f, 8f);
            else if (i == labels.Count - 1) (unlit, lit, pieceW, left, right) = (new(25f, 0f),   new(25f, LitV),   25f, 8f, 16f);
            else                            (unlit, lit, pieceW, left, right) = (new(162f, 40f), new(150f, 40f),   10f, 3f, 3f);

            var segment = new Segment(MakePiece(unlit, pieceW, left, right, position, size), MakePiece(lit, pieceW, left, right, position, size));
            _segments.Add(segment);

            var label = new TextNode
            {
                String        = labels[i],
                Position      = new Vector2(position.X, 6f),
                Size          = new Vector2(segmentW, 18f),
                AlignmentType = AlignmentType.Center,
                FontType      = FontType.Axis,
                FontSize      = 12,
                TextColor     = ColorHelper.GetColor(50),
            };
            label.AddTextFlags(TextFlags.Emboss);
            label.AttachNode(this);

            // Visible, though it draws nothing: a hidden node gets no mouse events.
            var hit = new CollisionNode
            {
                CollisionType       = CollisionType.Hit,
                Position            = position,
                Size                = size,
                ShowClickableCursor = true,
            };
            hit.AddEvent(AtkEventType.MouseDown, (_, _, _, _, eventData) =>
            {
                if (eventData is null || eventData->MouseData.ButtonId != 0 || index == Selected) return;

                Selected = index;
                OnSelected?.Invoke(index);
            });
            hit.AttachNode(this);
        }

        // Last, so the frame is drawn over the edges of the segments.
        var frame = new SimpleNineGridNode
        {
            TexturePath        = Texture,
            TextureCoordinates = new Vector2(124f, 40f),
            TextureSize        = new Vector2(24f, 26f),
            TopOffset          = 8f,
            BottomOffset       = 8f,
            LeftOffset         = 8f,
            RightOffset        = 8f,
            Size               = new Vector2(width, SwitchHeight),
        };
        frame.AttachNode(this);

        Selected = 0;
    }

    /// <summary>Called with the position of the segment the user clicked, when it was not the selected one.</summary>
    public Action<int>? OnSelected { get; set; }

    /// <summary>The lit segment. Setting it does not call <see cref="OnSelected"/>.</summary>
    public int Selected
    {
        get;
        set
        {
            field = Math.Clamp(value, 0, Math.Max(0, _segments.Count - 1));
            for (var i = 0; i < _segments.Count; i++)
            {
                _segments[i].Lit.IsVisible   = i == field;
                _segments[i].Unlit.IsVisible = i != field;
            }
        }
    }

    private SimpleNineGridNode MakePiece(Vector2 coordinates, float pieceW, float left, float right, Vector2 position, Vector2 size)
    {
        var piece = new SimpleNineGridNode
        {
            TexturePath        = Texture,
            TextureCoordinates = coordinates,
            TextureSize        = new Vector2(pieceW, PieceH),
            TopOffset          = 4f,
            BottomOffset       = 6f,
            LeftOffset         = left,
            RightOffset        = right,
            Position           = position,
            Size               = size,
        };
        piece.AttachNode(this);
        return piece;
    }
}
