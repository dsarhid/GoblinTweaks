using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// The handle of a scroll area: a box with the name of the area, shown while the settings window is
/// open, that the user drags to move the area as in the game's HUD layout.
/// </summary>
internal sealed class BattleTextHandleNode : OverlayNode
{
    private static readonly Vector4 White   = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Outline = new(0.05f, 0.05f, 0.05f, 1f);

    private readonly TextNode _label;

    public BattleTextHandleNode()
    {
        Size      = new Vector2(150f, 30f);
        IsVisible = false;

        _label = new TextNode
        {
            Size             = Size,
            AlignmentType    = AlignmentType.Center,
            FontSize         = 14,
            TextColor        = White,
            TextOutlineColor = Outline,
        };
        _label.AddTextFlags(TextFlags.Edge);
        _label.AttachNode(this);
    }

    public string Label
    {
        set => _label.String = value;
    }

    /// <summary>On screen and draggable. Off, the handle does not listen to the mouse.</summary>
    public bool Shown
    {
        get;
        set
        {
            if (field == value) return;

            field        = value;
            IsVisible    = value;
            EnableMoving = value;
        }
    }

    // Over the interface: a handle under a window could be dragged without being seen.
    public override OverlayLayer OverlayLayer => OverlayLayer.AboveUserInterface;

    protected override void OnUpdate()
    {
    }
}
