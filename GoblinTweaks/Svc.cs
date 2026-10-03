using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace GoblinTweaks;

/// <summary>Dalamud services, injected once at startup.</summary>
internal sealed class Svc
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager Commands { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IDataManager Data { get; private set; } = null!;
    [PluginService] public static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] public static IGameGui     GameGui     { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IContextMenu   ContextMenu   { get; private set; } = null!;
    [PluginService] public static IPluginLog     Log           { get; private set; } = null!;
    [PluginService] public static IDtrBar        DtrBar        { get; private set; } = null!;
    [PluginService] public static IGameInventory GameInventory { get; private set; } = null!;

    public static void Initialize(IDalamudPluginInterface pluginInterface) => pluginInterface.Create<Svc>();

    /// <summary>Runs an action on the game's main thread and waits for it (runs inline if already there).</summary>
    public static void RunOnFramework(Action action) => Framework.RunOnFrameworkThread(action).Wait();
}
