using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// One action icon over an enemy (Guard, or another action followed): bright while it is ready, and with the
/// game's clock hand and the seconds left while it is on cooldown, as in the hotbars.
/// </summary>
/// <remarks>
/// Built from plain image and text nodes, laid out like the game's action icon: the game's own hotbar slot is
/// a drag-and-drop component that takes the mouse, and over a head it would steal the clicks that target them.
/// </remarks>
internal sealed class EnemyGuardNode : OverlayNode
{
    // The layout of the game's action icon, at its own size; the node is scaled to the size wanted.
    private const float FullSize = 48f;
    private const int LastRecastFrame = 80;

    private static readonly Vector4 White   = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Outline = new(0.05f, 0.05f, 0.05f, 1f);

    private readonly IconImageNode _icon;
    private readonly ImageNode _recast;
    private readonly TextNode _seconds;
    private int _shownSeconds = -1;

    public EnemyGuardNode(uint iconId, float size)
    {
        Size      = new Vector2(FullSize, FullSize);
        Scale     = new Vector2(size / FullSize);
        IsVisible = false;

        _icon = new IconImageNode
        {
            IconId     = iconId,
            FitTexture = true,
            Position   = new Vector2(4f, 4f),
            Size       = new Vector2(40f, 40f),
        };
        _icon.AttachNode(this);

        // The sweep of the hotbars: 81 frames of a shadow that shrinks clockwise.
        _recast = new ImageNode
        {
            Position  = new Vector2(2f, 2f),
            Size      = new Vector2(44f, 46f),
            WrapMode  = WrapMode.Tile,
            IsVisible = false,
        };
        IconNodeTextureHelper.LoadIconARecastTexture(_recast);
        _recast.AttachNode(this);

        _seconds = new TextNode
        {
            Position         = new Vector2(0f, 12f),
            Size             = new Vector2(FullSize, 24f),
            AlignmentType    = AlignmentType.Center,
            FontSize         = 20,
            TextColor        = White,
            TextOutlineColor = Outline,
            IsVisible        = false,
        };
        _seconds.AddTextFlags(TextFlags.Edge);
        _seconds.AttachNode(this);
    }

    // Under the windows, over the name plates.
    public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;

    /// <summary>The action icon shown: nodes are reused from one action to another.</summary>
    public void SetIcon(uint iconId)
    {
        if (_icon.IconId != iconId) _icon.IconId = iconId;
    }

    /// <summary>The size the icon is drawn at, in the overlay's pixels.</summary>
    public void SetSize(float size)
    {
        var scale = new Vector2(size / FullSize);
        if (Scale != scale) Scale = scale;
    }

    /// <summary>Guard ready: the icon alone.</summary>
    public void ShowReady()
    {
        _recast.IsVisible = false;
        _seconds.IsVisible = false;
        _shownSeconds = -1;
    }

    /// <summary>Guard on cooldown: <paramref name="done"/> is how much of it has passed, 0 to 1.</summary>
    public void ShowCooldown(float done, float secondsLeft)
    {
        _recast.PartId = (uint)Math.Clamp((int)(done * LastRecastFrame), 0, LastRecastFrame);
        _recast.IsVisible = true;

        // Written only when the number changes: once a second.
        var seconds = (int)MathF.Ceiling(secondsLeft);
        if (seconds != _shownSeconds)
        {
            _shownSeconds = seconds;
            _seconds.String = seconds.ToString();
        }

        _seconds.IsVisible = true;
    }

    protected override void OnUpdate()
    {
    }
}
