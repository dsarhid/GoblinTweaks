using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// The name of an enemy at the left of their row of icons, when the rows are in a fixed place on screen instead of
/// over each enemy: there the name is the only way to tell whose row it is.
/// </summary>
internal sealed class EnemyNameNode : OverlayNode
{
    private const float LabelWidth = 220f;

    private static readonly Vector4 White   = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Outline = new(0.05f, 0.05f, 0.05f, 1f);

    private readonly TextNode _text;
    private string _shown = string.Empty;

    public EnemyNameNode()
    {
        Size      = new Vector2(LabelWidth, 32f);
        IsVisible = false;

        _text = new TextNode
        {
            Size             = new Vector2(LabelWidth, 32f),
            AlignmentType    = AlignmentType.Right,
            FontSize         = 16,
            TextColor        = White,
            TextOutlineColor = Outline,
        };
        _text.AddTextFlags(TextFlags.Edge);
        _text.AttachNode(this);
    }

    // On the same layer as the icons.
    public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;

    /// <summary>Shows <paramref name="name"/> with its right edge at <paramref name="right"/>, centred on a row <paramref name="height"/> tall.</summary>
    public void Show(string name, float right, float top, float height)
    {
        if (_shown != name)
        {
            _shown = name;
            _text.String = name;
        }

        var size = new Vector2(LabelWidth, height);
        if (Size != size)
        {
            Size = size;
            _text.Size = size;
        }

        Position  = new Vector2(right - LabelWidth, top);
        IsVisible = true;
    }

    protected override void OnUpdate()
    {
    }
}
