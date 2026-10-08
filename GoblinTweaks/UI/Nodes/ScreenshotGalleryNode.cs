using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Native;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// A row of screenshot thumbnails, all of them as tall as each other and as wide in total as the panel allows.
/// Clicking one calls <see cref="OnOpen"/> with its position.
/// </summary>
/// <remarks>
/// It does not scroll sideways: a clipping (scrolling) area inside the panel's own scrolling area left the game's
/// clip state set, and the window drawn after the main one (the big picture) was cut to the panel's area or not drawn.
/// </remarks>
internal sealed unsafe class ScreenshotGalleryNode : ResNode
{
    private const float MaxThumbHeight = 130f;
    private const float Gap = 16f;

    /// <summary>Total height of the row.</summary>
    public float TotalHeight { get; }

    public Action<int>? OnOpen { get; set; }

    public ScreenshotGalleryNode(IReadOnlyList<string> keys, float width)
    {
        var textures = keys.Select(key => ScreenshotStore.TryGet(key, out var t) ? t : null).Where(t => t is not null).ToList();
        var ratios   = textures.Select(t => (float)t!.Width / t.Height).ToList();

        // As tall as allowed, unless the row would not fit: then every thumbnail shrinks by the same amount.
        var gaps   = Gap * Math.Max(0, ratios.Count - 1);
        var height = MathF.Min(MaxThumbHeight, (width - gaps) / MathF.Max(0.1f, ratios.Sum()));

        TotalHeight = height;
        Size        = new Vector2(width, height);

        var x = 0f;
        for (var i = 0; i < textures.Count; i++)
        {
            var thumbW = MathF.Round(height * ratios[i]);

            var image = new WrapImageNode(textures[i]!) { Position = new Vector2(x, 0f), Size = new Vector2(thumbW, height) };
            image.AttachNode(this);

            var index = i;
            var hit = new CollisionNode
            {
                CollisionType       = CollisionType.Hit,
                Position            = new Vector2(x, 0f),
                Size                = new Vector2(thumbW, height),
                ShowClickableCursor = true,
            };
            hit.AddEvent(AtkEventType.MouseClick, (_, _, _, _, eventData) =>
            {
                if (eventData is null || eventData->MouseData.ButtonId != 0) return;
                OnOpen?.Invoke(index);
            });
            hit.AttachNode(this);

            x += thumbW + Gap;
        }
    }
}
