using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using KamiToolKit;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using GoblinTweaks.UI;

namespace GoblinTweaks;

public sealed class Plugin(IDalamudPluginInterface dalamud) : IAsyncDalamudPlugin
{
    private const string Command      = "/goblintweaks";
    private const string ShortCommand = "/gtweaks";

    private ConfigStore?          _config;
    private TweakManager?         _tweaks;
    private MainAddon?            _mainWindow;
    private PluginSettingsAddon?  _settingsWindow;
    private ChangelogAddon?       _changelogWindow;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await KamiToolKitLibrary.InitializeAsync(dalamud);
        Svc.Initialize(dalamud);

        _config = new ConfigStore();
        _config.Load();
        Loc.Initialize(_config.Data.Language);
        UiFont.Set(_config.Data.FontFamily, notify: false);

        ScreenshotStore.Preload();
        _tweaks = new TweakManager(_config);

        HandleVersionChange(_config);

        Svc.Commands.AddHandler(Command,      new CommandInfo(OnCommand) { HelpMessage = Loc.Get("Plugin.Command.Help") });
        Svc.Commands.AddHandler(ShortCommand, new CommandInfo(OnCommand) { ShowInHelp  = false });

        dalamud.UiBuilder.OpenMainUi   += ToggleMainWindow;
        dalamud.UiBuilder.OpenConfigUi += OpenSettings;
        Loc.LanguageChanged            += OnLanguageChanged;
        UiFont.Changed                 += OnLanguageChanged;

        _tweaks.EnableSaved();
    }

    public ValueTask DisposeAsync()
    {
        dalamud.UiBuilder.OpenMainUi   -= ToggleMainWindow;
        dalamud.UiBuilder.OpenConfigUi -= OpenSettings;
        Loc.LanguageChanged            -= OnLanguageChanged;
        UiFont.Changed                 -= OnLanguageChanged;

        Svc.Commands.RemoveHandler(Command);
        Svc.Commands.RemoveHandler(ShortCommand);

        _tweaks?.Dispose();

        // Native windows and the native UI library must be disposed on the game (framework) thread.
        Svc.RunOnFramework(() =>
        {
            _mainWindow?.DisposeViewer();
            _mainWindow?.Dispose();
            _settingsWindow?.Dispose();
            _changelogWindow?.Dispose();
            KamiToolKitLibrary.Dispose();
            ScreenshotStore.Dispose();
        });
        return ValueTask.CompletedTask;
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim() is "settings" or "config" or "ajustes")
            OpenSettings();
        else
            ToggleMainWindow();
    }

    private void ToggleMainWindow()
    {
        Svc.RunOnFramework(() =>
        {
            _mainWindow ??= NewMainWindow();
            _mainWindow.Toggle();
        });
    }

    private void OpenSettings()
    {
        Svc.RunOnFramework(() =>
        {
            _settingsWindow ??= NewSettingsWindow();
            _settingsWindow.Toggle();
        });
    }

    private void OpenChangelog()
    {
        Svc.RunOnFramework(() =>
        {
            _changelogWindow ??= NewChangelogWindow();
            _changelogWindow.Toggle();
        });
    }

    private MainAddon NewMainWindow() => new()
    {
        InternalName  = "GoblinTweaksMain",
        Title         = "GoblinTweaks",
        Size          = new Vector2(1196f, 780f),
        Manager       = _tweaks!,
        OpenSettings  = OpenSettings,
        OpenChangelog = OpenChangelog,
    };

    private PluginSettingsAddon NewSettingsWindow() => new()
    {
        InternalName = "GoblinTweaksSettings",
        Title        = Loc.Get("Settings.Title"),
        Size         = new Vector2(560f, 620f),
        Config       = _config!,
    };

    private static ChangelogAddon NewChangelogWindow() => new()
    {
        InternalName = "GoblinTweaksChangelog",
        Title        = "GoblinTweaks — Changelog",
        Size         = new Vector2(620f, 500f),
    };

    /// <summary>The windows were built with the old language: close them and open them again, a moment later.</summary>
    private void OnLanguageChanged()
    {
        Svc.RunOnFramework(() => Svc.Framework.RunOnTick(() =>
        {
            Reopen(ref _mainWindow,      NewMainWindow);
            Reopen(ref _settingsWindow,  NewSettingsWindow);
            Reopen(ref _changelogWindow, NewChangelogWindow);
        }, delayTicks: 2));
    }

    private static void Reopen<T>(ref T? window, Func<T> create) where T : KamiToolKit.BaseTypes.NativeAddon
    {
        if (window is not { IsOpen: true } old)
        {
            // Closed windows are simply rebuilt next time they are opened.
            window?.Dispose();
            window = null;
            return;
        }

        old.Close();
        old.Dispose();
        var fresh = create();
        window = fresh;
        Svc.Framework.RunOnTick(() => fresh.Open(), delayTicks: 6);
    }

    private static void HandleVersionChange(ConfigStore config)
    {
        var currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? string.Empty;
        var lastSeen       = config.Data.LastSeenVersion;

        if (lastSeen == null)
        {
            // Fresh install — record the current version so the banner never appears for this install.
            config.Data.LastSeenVersion = currentVersion;
            config.Save();
        }
        // If lastSeen != currentVersion, the What's New card shows in the main window
        // and updates LastSeenVersion when the user dismisses it.
    }
}
