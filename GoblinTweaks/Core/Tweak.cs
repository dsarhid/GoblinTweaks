using System.Numerics;
using System.Reflection;
using Dalamud.Game.Command;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using GoblinTweaks.UI;

namespace GoblinTweaks.Core;

/// <summary>
/// Base class for every tweak. A tweak must be a non-abstract class with a parameterless
/// constructor and a <see cref="TweakAttribute"/>; nothing else needs to be registered.
/// </summary>
/// <remarks>
/// <see cref="Enable"/> and <see cref="Disable"/> always run on the framework (game) thread,
/// so tweaks may safely touch native UI there. <see cref="Disable"/> must undo everything
/// <see cref="Enable"/> did, because it is also called when the plugin unloads.
/// </remarks>
public abstract class Tweak
{
    protected Tweak()
    {
        Id = GetType().Name;
        Category = GetType().GetCustomAttribute<TweakAttribute>()?.Category ?? TweakCategory.Other;
    }

    /// <summary>Stable identifier (class name). Used for config and localization keys.</summary>
    public string Id { get; }

    public TweakCategory Category { get; }

    public TweakState State { get; internal set; } = TweakState.Disabled;

    /// <summary>Last error message when <see cref="State"/> is <see cref="TweakState.Error"/>.</summary>
    public string? ErrorMessage { get; internal set; }

    /// <summary>Approximate managed memory held by this tweak, in bytes. Negative until first measured.</summary>
    public long MemoryBytes { get; internal set; } = -1;

    public string Name => Loc.Get($"Tweaks.{Id}.Name", Id);

    public string Description => Loc.Get($"Tweaks.{Id}.Description", string.Empty);

    /// <summary>The name in every language (and the class name), so search finds a tweak whatever language is selected.</summary>
    public IEnumerable<string> SearchTexts => Loc.GetInAllLanguages($"Tweaks.{Id}.Name").Append(Id);

    /// <summary>Button next to the on/off button of the main window that opens the window of this tweak. Shown while the tweak is enabled.</summary>
    public virtual TweakButton? OpenButton => null;

    /// <summary>Button in the "Settings" section that opens the help of this tweak.</summary>
    public virtual TweakButton? HelpButton => null;

    /// <summary>Other buttons of the "Settings" section (open the settings window...).</summary>
    public virtual IReadOnlyList<TweakButton> Buttons => [];

    /// <summary>Simple on/off options shown in the "Settings" section of the detail panel.</summary>
    public virtual IReadOnlyList<TweakToggle> Toggles => [];

    /// <summary>Chat commands of this tweak (the short one first). Registered while the tweak is enabled, all of them run <see cref="OnCommand"/>.</summary>
    protected virtual IReadOnlyList<string> CommandNames => [];

    /// <summary>What the chat commands do. Runs on the game thread.</summary>
    protected virtual void OnCommand() { }

    /// <summary>The commands with their explanation (<c>Command.Help</c> of the tweak), for the "Commands" section of the detail panel.</summary>
    public IReadOnlyList<TweakCommand> Commands
        => [.. CommandNames.Select(name => new TweakCommand(name, TMain("Command.Help")))];

    private TextHelpAddon? _helpWindow;

    /// <summary>The short chat command without the slash (e.g. "gwp"): the name the icon and the screenshots of the tweak go by.</summary>
    private string? ShortCode => CommandNames.Count > 0 ? CommandNames[0].TrimStart('/') : null;

    /// <summary>Name of the icon (<c>Assets/IconTweaks/&lt;short command&gt;Icon.png</c>), or null when the tweak has no command.</summary>
    public string? IconKey => ShortCode is { } code ? code + "Icon" : null;

    /// <summary>The screenshots (<c>Assets/Screenshots/&lt;short command&gt;1.png</c> to <c>3</c>) that exist, for the "Screenshots" section.</summary>
    public IReadOnlyList<string> Screenshots
        => ShortCode is { } code
            ? [.. Enumerable.Range(1, 3).Select(index => code + index).Where(key => ScreenshotStore.TryGet(key, out _))]
            : [];

    public bool HasSettings => HelpButton is not null || Buttons.Count > 0 || Toggles.Count > 0;

    internal TweakManager Manager { get; set; } = null!;

    internal void RegisterCommands()
    {
        foreach (var name in CommandNames)
        {
            Svc.Commands.AddHandler(name, new CommandInfo((_, _) =>
            {
                try   { OnCommand(); }
                catch (Exception ex) { ReportFailure(ex); }
            })
            { HelpMessage = TMain("Command.Help") });
        }
    }

    /// <summary>Removes what the base class added around <see cref="Enable"/>: the chat commands and the help window.</summary>
    internal void Cleanup()
    {
        foreach (var name in CommandNames)
            Svc.Commands.RemoveHandler(name);

        _helpWindow?.Dispose();
        _helpWindow = null;
    }

    /// <summary>Opens, or closes if open, the one-page help window of a tweak without windows of its own (<c>Help.Title</c> / <c>Help.Text</c>).</summary>
    protected void ToggleHelpWindow()
    {
        // Built when opened so its text follows the language selected now.
        if (_helpWindow is { IsOpen: true }) { _helpWindow.Close(); return; }
        _helpWindow?.Dispose();

        _helpWindow = new TextHelpAddon
        {
            InternalName    = $"Gtk{Id}Help",
            Title           = T("Help.Title"),
            Size            = new Vector2(560f, 400f),
            RespectCloseAll = false,
            Pages           = [(T("Help.Title"), T("Help.Text"))],
        };
        _helpWindow.Open();
    }

    protected internal abstract void Enable();

    protected internal abstract void Disable();

    internal virtual void LoadSettings(ConfigStore store) { }

    /// <summary>
    /// Call from event handlers when something unexpected happens: the tweak is disabled safely,
    /// marked as Error and the problem is logged, instead of affecting the game.
    /// </summary>
    protected void ReportFailure(Exception exception) => Manager.ReportFailure(this, exception);

    /// <summary>Language chosen for the windows of this tweak ("" = the same as GoblinTweaks).</summary>
    public string Language => Manager.Store.Data.TweakLanguages.GetValueOrDefault(Id, string.Empty);

    /// <summary>The language the windows of this tweak are written in.</summary>
    internal string UiLanguage => Language.Length == 0 ? Loc.CurrentLanguage : Language;

    /// <summary>Text of this tweak for its own windows: in the language chosen for the tweak.</summary>
    protected string T(string key) => Loc.GetIn(UiLanguage, $"Tweaks.{Id}.{key}");

    /// <summary>Text of this tweak for the main GoblinTweaks window (buttons, tooltips): in the GoblinTweaks language.</summary>
    protected string TMain(string key) => Loc.Get($"Tweaks.{Id}.{key}");

    /// <summary>Changes the language of the windows of this tweak, remembers it and lets the tweak rebuild them.</summary>
    internal void SetLanguage(string code)
    {
        var languages = Manager.Store.Data.TweakLanguages;
        if (code.Length == 0) languages.Remove(Id);
        else languages[Id] = code;
        Manager.Store.Save();

        // Later: the window that asked for this is one of the windows that get rebuilt.
        Svc.Framework.RunOnTick(OnLanguageChanged, delayTicks: 2);
    }

    /// <summary>The language of the windows changed: build them again. Runs on the game thread, outside the click that asked for it.</summary>
    protected virtual void OnLanguageChanged() { }

    /// <summary>
    /// Builds a window again (it writes its texts once, when it is built), and opens it again if it was open.
    /// </summary>
    protected static void Rebuild<TWindow>(ref TWindow? window, Func<TWindow> create) where TWindow : KamiToolKit.BaseTypes.NativeAddon
    {
        var wasOpen = window is { IsOpen: true };
        if (wasOpen) window!.Close();
        window?.Dispose();

        var fresh = create();
        window = fresh;
        if (wasOpen) Svc.Framework.RunOnTick(() => fresh.Open(), delayTicks: 6);
    }
}

/// <summary>A chat command of a tweak and what it does.</summary>
public readonly record struct TweakCommand(string Command, string Description);

/// <summary>A button of a tweak in the main window. <see cref="Click"/> runs on the game thread.</summary>
public readonly record struct TweakButton(string Label, string? Tooltip, Action Click);

/// <summary>An on/off option of a tweak in the main window.</summary>
public readonly record struct TweakToggle(string Label, string? Help, Func<bool> Get, Action<bool> Set);

/// <summary>A tweak with user options. <typeparamref name="TSettings"/> is stored as JSON in the plugin config.</summary>
public abstract class Tweak<TSettings> : Tweak where TSettings : class, new()
{
    protected TSettings Settings { get; private set; } = new();

    internal override void LoadSettings(ConfigStore store) => Settings = store.GetSettings<TSettings>(Id);

    /// <summary>Persists <see cref="Settings"/> and notifies the tweak.</summary>
    protected void SaveSettings()
    {
        Manager.Store.SetSettings(Id, Settings);

        if (State == TweakState.Enabled)
            OnSettingsChanged();
    }

    protected virtual void OnSettingsChanged() { }
}
