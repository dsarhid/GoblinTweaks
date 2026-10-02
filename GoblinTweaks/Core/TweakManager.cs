using System.Reflection;

namespace GoblinTweaks.Core;

/// <summary>Discovers tweaks, applies the saved enabled state and isolates their failures.</summary>
public sealed class TweakManager : IDisposable
{
    public TweakManager(ConfigStore store)
    {
        Store = store;

        Tweaks = typeof(Tweak).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(Tweak).IsAssignableFrom(type) && type.GetCustomAttribute<TweakAttribute>() != null)
            .Select(CreateTweak)
            .OfType<Tweak>()
            .ToArray();

        Svc.Log.Information("Loaded {count} tweaks", Tweaks.Count);
    }

    public ConfigStore Store { get; }

    public IReadOnlyList<Tweak> Tweaks { get; }

    public int EnabledCount => Tweaks.Count(tweak => tweak.State == TweakState.Enabled);

    private Tweak? CreateTweak(Type type)
    {
        try
        {
            var tweak = (Tweak)Activator.CreateInstance(type)!;
            tweak.Manager = this;
            tweak.LoadSettings(Store);
            return tweak;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Could not create tweak {type}", type.Name);
            return null;
        }
    }

    /// <summary>Enables the tweaks that were enabled last session (on the next framework tick).</summary>
    public void EnableSaved()
    {
        Svc.Framework.RunOnFrameworkThread(() =>
        {
            foreach (var tweak in Tweaks.Where(tweak => Store.Data.EnabledTweaks.Contains(tweak.Id)))
                TryEnable(tweak);
        });
    }

    /// <summary>Turns a tweak on or off from the UI and remembers the choice.</summary>
    public void SetEnabled(Tweak tweak, bool enabled)
    {
        Svc.RunOnFramework(() =>
        {
            if (enabled)
                TryEnable(tweak);
            else
                TryDisable(tweak);
        });

        var changed = enabled ? Store.Data.EnabledTweaks.Add(tweak.Id) : Store.Data.EnabledTweaks.Remove(tweak.Id);
        if (changed)
            Store.Save();
    }

    internal void ReportFailure(Tweak tweak, Exception exception)
    {
        Svc.Log.Error(exception, "Tweak {tweak} failed and was disabled", tweak.Id);

        // Disable later, outside the event handler that is currently running.
        Svc.Framework.RunOnTick(() =>
        {
            if (tweak.State == TweakState.Enabled)
                SafeDisable(tweak);

            tweak.State = TweakState.Error;
            tweak.ErrorMessage = exception.Message;
        });
    }

    private void TryEnable(Tweak tweak)
    {
        if (tweak.State == TweakState.Enabled)
            return;

        try
        {
            tweak.Enable();
            tweak.State = TweakState.Enabled;
            tweak.ErrorMessage = null;
            Svc.Log.Debug("Enabled {tweak}", tweak.Id);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Could not enable {tweak}", tweak.Id);
            SafeDisable(tweak);
            tweak.State = TweakState.Error;
            tweak.ErrorMessage = ex.Message;
        }
    }

    private static void TryDisable(Tweak tweak)
    {
        if (tweak.State == TweakState.Enabled)
            SafeDisable(tweak);

        tweak.State = TweakState.Disabled;
        tweak.ErrorMessage = null;
    }

    private static void SafeDisable(Tweak tweak)
    {
        try
        {
            tweak.Disable();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Error while disabling {tweak}", tweak.Id);
        }
    }

    public void Dispose()
    {
        Svc.RunOnFramework(() =>
        {
            foreach (var tweak in Tweaks.Where(tweak => tweak.State == TweakState.Enabled))
                SafeDisable(tweak);
        });
    }
}
