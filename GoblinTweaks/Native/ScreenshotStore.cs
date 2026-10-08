using System.Reflection;
using Dalamud.Interface.Textures.TextureWraps;

namespace GoblinTweaks.Native;

/// <summary>
/// The screenshots shown in the "Screenshots" section of a tweak: PNG files embedded in the plugin
/// (<c>Assets/Screenshots/&lt;name&gt;.png</c>), loaded once into textures when the plugin starts.
/// </summary>
internal static class ScreenshotStore
{
    private const string Prefix = "Screenshots.";

    private static readonly Dictionary<string, IDalamudTextureWrap> Textures = [];
    private static readonly object Gate = new();
    private static bool _disposed;

    /// <summary>Starts loading every embedded screenshot in the background.</summary>
    public static void Preload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            var key = Path.GetFileNameWithoutExtension(resource[Prefix.Length..]);
            var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;

            Svc.Textures.CreateFromImageAsync(stream, leaveOpen: false).ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully)
                {
                    Svc.Log.Warning(task.Exception, "Could not load screenshot {key}", key);
                    return;
                }

                lock (Gate)
                {
                    if (_disposed) task.Result.Dispose();
                    else Textures[key] = task.Result;
                }
            });
        }
    }

    /// <summary>The texture of a screenshot, or false while it is not loaded (or does not exist).</summary>
    public static bool TryGet(string key, out IDalamudTextureWrap texture)
    {
        lock (Gate)
            return Textures.TryGetValue(key, out texture!);
    }

    public static void Dispose()
    {
        lock (Gate)
        {
            _disposed = true;
            foreach (var texture in Textures.Values) texture.Dispose();
            Textures.Clear();
        }
    }
}
