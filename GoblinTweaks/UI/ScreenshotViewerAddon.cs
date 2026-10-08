using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>A window with one screenshot at a time, as big as the window allows, with buttons to go to the previous and next one.</summary>
internal sealed unsafe class ScreenshotViewerAddon : NativeAddon
{
    private const float BarH = 44f;

    private readonly List<WrapImageNode> _images = [];
    private TextNode? _counter;
    private ResNode? _stage;

    public required IReadOnlyList<string> Keys { get; init; }

    /// <summary>Top-left corner of the window on the screen.</summary>
    public Vector2 Origin { get; set; }

    /// <summary>Screenshot shown when the window opens.</summary>
    public int Index { get; set; }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c    = ContentStartPosition;
        var cs   = ContentSize;
        var area = new Vector2(cs.X, cs.Y - BarH);

        // A plain container: no clipping, since a clipping area left the game's clip state set for the window drawn after it.
        _stage = new ResNode { Position = c, Size = area };
        _stage.AttachNode(this);

        foreach (var key in Keys)
        {
            if (!ScreenshotStore.TryGet(key, out var texture)) continue;

            // As big as fits, never bigger than the picture itself.
            var scale = MathF.Min(1f, MathF.Min(area.X / texture.Width, area.Y / texture.Height));
            var size  = new Vector2(texture.Width * scale, texture.Height * scale);

            var image = new WrapImageNode(texture)
            {
                Size      = size,
                Position  = (area - size) / 2f,
                IsVisible = false,
            };
            image.AttachNode(_stage);
            _images.Add(image);
        }

        var y = c.Y + cs.Y - BarH + 8f;
        if (_images.Count > 1)
        {
            var previous = new TextButtonNode { String = "‹", Position = new Vector2(c.X + cs.X / 2f - 110f, y), Size = new Vector2(60f, 28f) };
            previous.OnClick = () => Show(Index - 1);
            previous.AttachNode(this);

            var next = new TextButtonNode { String = "›", Position = new Vector2(c.X + cs.X / 2f + 50f, y), Size = new Vector2(60f, 28f) };
            next.OnClick = () => Show(Index + 1);
            next.AttachNode(this);
        }

        var close = new TextButtonNode
        {
            String   = Loc.Get("Window.Close"),
            Position = new Vector2(c.X + cs.X - 120f - 8f, y),
            Size     = new Vector2(120f, 28f),
        };
        close.OnClick = Close;
        close.AttachNode(this);

        _counter = new TextNode
        {
            Position      = new Vector2(c.X + cs.X / 2f - 40f, y + 4f),
            Size          = new Vector2(80f, 20f),
            AlignmentType = AlignmentType.Center,
            FontSize      = 14,
            TextColor     = new Vector4(0.85f, 0.85f, 0.85f, 1f),
        };
        _counter.AttachNode(this);

        Show(Index);
    }

    private bool _placed;

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        // Moved on the first frame of the open window, through the library: before that the window is not there yet
        // and the position is ignored.
        if (_placed) return;

        _placed = true;
        SetWindowPosition(Origin);
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _images.Clear();
        _counter = null;
        _stage = null;
        base.OnFinalize(addon);
    }

    /// <summary>Shows one screenshot (wrapping around at both ends) and hides the others.</summary>
    public void Show(int index)
    {
        if (_images.Count == 0) return;

        Index = (index % _images.Count + _images.Count) % _images.Count;
        for (var i = 0; i < _images.Count; i++)
            _images[i].IsVisible = i == Index;

        if (_counter is not null)
            _counter.String = $"{Index + 1} / {_images.Count}";
    }
}
