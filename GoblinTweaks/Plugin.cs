using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using KamiToolKit;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;

namespace GoblinTweaks;

public sealed class Plugin(IDalamudPluginInterface dalamud) : IAsyncDalamudPlugin
{
    private const string Command      = "/goblintweaks";
    private const string ShortCommand = "/gtweaks";

    private WindowSystem?   _windows;
    private ConfigStore?    _config;
    private TweakManager?   _tweaks;
    private MainWindow?     _mainWindow;
    private SettingsWindow? _settingsWindow;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await KamiToolKitLibrary.InitializeAsync(dalamud);
        Svc.Initialize(dalamud);

        _config = new ConfigStore();
        _config.Load();
        Loc.Initialize(_config.Data.Language);

        _tweaks = new TweakManager(_config);

        _windows      = new WindowSystem("GoblinTweaks");
        _settingsWindow = new SettingsWindow(_config);
        _mainWindow     = new MainWindow(_tweaks, OpenSettings);
        _windows.AddWindow(_mainWindow);
        _windows.AddWindow(_settingsWindow);

        Svc.Commands.AddHandler(Command,      new CommandInfo(OnCommand) { HelpMessage = Loc.Get("Plugin.Command.Help") });
        Svc.Commands.AddHandler(ShortCommand, new CommandInfo(OnCommand) { ShowInHelp  = false });

        dalamud.UiBuilder.Draw        += _windows.Draw;
        dalamud.UiBuilder.OpenMainUi  += ToggleMainWindow;
        dalamud.UiBuilder.OpenConfigUi += OpenSettings;

        _tweaks.EnableSaved();
    }

    public ValueTask DisposeAsync()
    {
        if (_windows is not null) dalamud.UiBuilder.Draw -= _windows.Draw;
        dalamud.UiBuilder.OpenMainUi   -= ToggleMainWindow;
        dalamud.UiBuilder.OpenConfigUi -= OpenSettings;

        Svc.Commands.RemoveHandler(Command);
        Svc.Commands.RemoveHandler(ShortCommand);

        _windows?.RemoveAllWindows();
        _tweaks?.Dispose();

        // The native UI library must be disposed on the game (framework) thread.
        Svc.RunOnFramework(KamiToolKitLibrary.Dispose);
        return ValueTask.CompletedTask;
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim() is "settings" or "config" or "ajustes")
            OpenSettings();
        else
            _mainWindow?.Toggle();
    }

    private void ToggleMainWindow() => _mainWindow?.Toggle();
    private void OpenSettings()     => _settingsWindow!.IsOpen = true;
}
