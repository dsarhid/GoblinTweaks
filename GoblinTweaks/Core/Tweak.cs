using System.Reflection;
using GoblinTweaks.Localization;

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

    /// <summary>Name and description in every language, so search works whatever language is selected.</summary>
    public IEnumerable<string> SearchTexts
        => Loc.GetInAllLanguages($"Tweaks.{Id}.Name").Concat(Loc.GetInAllLanguages($"Tweaks.{Id}.Description")).Append(Id);

    public virtual bool HasSettings => false;

    internal TweakManager Manager { get; set; } = null!;

    protected internal abstract void Enable();

    protected internal abstract void Disable();

    /// <summary>Draws the options of this tweak inside its card. Only called when <see cref="HasSettings"/> is true.</summary>
    public virtual void DrawSettings() { }

    internal virtual void LoadSettings(ConfigStore store) { }

    /// <summary>
    /// Call from event handlers when something unexpected happens: the tweak is disabled safely,
    /// marked as Error and the problem is logged, instead of affecting the game.
    /// </summary>
    protected void ReportFailure(Exception exception) => Manager.ReportFailure(this, exception);

    protected string T(string key) => Loc.Get($"Tweaks.{Id}.{key}");
}

/// <summary>A tweak with user options. <typeparamref name="TSettings"/> is stored as JSON in the plugin config.</summary>
public abstract class Tweak<TSettings> : Tweak where TSettings : class, new()
{
    protected TSettings Settings { get; private set; } = new();

    public override bool HasSettings => true;

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
