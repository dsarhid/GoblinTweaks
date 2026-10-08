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
[Tweak(TweakCategory.Interface)]
public sealed class MyTweak : Tweak<MyTweak.Options>
{
    public sealed class Options
    {
        public bool Compact { get; set; } = true;
    }

    // Simple on/off options appear in the tweak's panel in the main window (native UI).
    public override IReadOnlyList<TweakToggle> Toggles =>
    [
        new(T("Compact"), T("Compact.Help"), () => Settings.Compact, on =>
        {
            Settings.Compact = on;
            SaveSettings(); // saves and calls OnSettingsChanged()
        }),
    ];

    // Buttons (e.g. to open a native window of the tweak) go in `Buttons`, and a chat command in `CommandHint`.
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
