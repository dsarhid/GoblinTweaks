using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;

namespace GoblinTweaks;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/goblintweaks";
    private const string ShortCommand = "/gtweaks";

    private readonly WindowSystem _windows = new("GoblinTweaks");
    private readonly ConfigStore _config;
    private readonly TweakManager _tweaks;
    private readonly MainWindow _mainWindow;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Svc.Initialize(pluginInterface);
        Loc.Initialize();

        _config = new ConfigStore();
        _config.Load();
        _tweaks = new TweakManager(_config);

        _mainWindow = new MainWindow(_tweaks);
        _windows.AddWindow(_mainWindow);

        var commandInfo = new CommandInfo((_, _) => _mainWindow.Toggle()) { HelpMessage = Loc.Get("Plugin.Command.Help") };
        Svc.Commands.AddHandler(Command, commandInfo);
        Svc.Commands.AddHandler(ShortCommand, new CommandInfo((_, _) => _mainWindow.Toggle()) { ShowInHelp = false });

        pluginInterface.UiBuilder.Draw += _windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += _mainWindow.Toggle;
        pluginInterface.UiBuilder.OpenConfigUi += _mainWindow.Toggle;

        _tweaks.EnableSaved();
    }

    public void Dispose()
    {
        var ui = Svc.PluginInterface.UiBuilder;
        ui.Draw -= _windows.Draw;
        ui.OpenMainUi -= _mainWindow.Toggle;
        ui.OpenConfigUi -= _mainWindow.Toggle;

        Svc.Commands.RemoveHandler(Command);
        Svc.Commands.RemoveHandler(ShortCommand);

        _windows.RemoveAllWindows();
        _tweaks.Dispose();
        Loc.Dispose();
    }
}
