# Adding a tweak

A tweak is one class in `GoblinTweaks/Tweaks/` with the `[Tweak]` attribute. It is found automatically.

```csharp
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using GoblinTweaks.Core;

namespace GoblinTweaks.Tweaks;

[Tweak(TweakCategory.Interface)]
public sealed class MyTweak : Tweak
{
    protected internal override void Enable()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "AddonName", OnPostSetup);
    }

    protected internal override void Disable()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "AddonName", OnPostSetup);
        // undo every change made to the game here
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            // ...
        }
        catch (Exception ex)
        {
            ReportFailure(ex); // disables the tweak safely and shows the error in the window
        }
    }
}
```

Then add its texts to **every** file in `Localization/` (English is the fallback):

```json
"Tweaks.MyTweak.Name": "My tweak",
"Tweaks.MyTweak.Description": "What it does, in one or two sentences."
```

## Options

Inherit from `Tweak<TOptions>`; the options class is saved as JSON automatically.
New fields with default values never need a migration.

```csharp
using GoblinTweaks.UI;

[Tweak(TweakCategory.Interface)]
public sealed class MyTweak : Tweak<MyTweak.Options>
{
    public sealed class Options
    {
        public bool Compact { get; set; } = true;
    }

    public override void DrawSettings()
    {
        var compact = Settings.Compact;
        if (Widgets.SettingToggle(T("Compact"), T("Compact.Help"), ref compact))
        {
            Settings.Compact = compact;
            SaveSettings(); // saves and calls OnSettingsChanged()
        }
    }
    // Enable / Disable as above
}
```

`T("Compact")` reads `Tweaks.MyTweak.Compact` from the localization files.

## Rules that keep the plugin update-proof

- `Enable`/`Disable` run on the game thread; `Disable` must undo everything `Enable` did.
- Prefer **AddonLifecycle events** over hooks. Avoid signatures and hardcoded offsets.
- Validate what you read from the UI (node exists, expected type) and bail out quietly if not.
- Wrap event handlers in `try/catch` and call `ReportFailure`, so a game change never crashes the game.
- Free anything you create in native memory (see `Native/NativeImageNode.cs`).
