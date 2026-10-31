using KamiToolKit.UiOverlay;

namespace GoblinTweaks.Core;

/// <summary>The one HUD overlay of the plugin, shared by every tweak that draws on it.</summary>
/// <remarks>
/// It is created the first time a tweak needs it and freed only when the plugin unloads. Freeing an
/// <see cref="OverlayController"/> (KamiToolKit 2.2.38) also stops the game's notices to every other overlay of the
/// plugin, which then keep nodes the game has already freed and crash the game ("CLR error") when they touch them:
/// turning a tweak off must never free one. Tweaks take their own nodes off with <see cref="OverlayNodes"/> instead.
/// </remarks>
internal static class SharedOverlay
{
    private static OverlayController? _controller;

    /// <summary>Must be first used on the game (framework) thread, as an overlay is built there.</summary>
    public static OverlayController Controller => _controller ??= new OverlayController();

    /// <summary>On plugin unload, on the game thread.</summary>
    public static void Dispose()
    {
        _controller?.Dispose();
        _controller = null;
    }
}

/// <summary>The nodes one tweak put on the <see cref="SharedOverlay"/>; disposing it takes them all off again.</summary>
internal sealed class OverlayNodes : IDisposable
{
    private readonly List<OverlayNode> _nodes = [];

    public void AddNode(OverlayNode node)
    {
        SharedOverlay.Controller.AddNode(node);
        _nodes.Add(node);
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
            SharedOverlay.Controller.RemoveNode(node);
        _nodes.Clear();
    }
}
