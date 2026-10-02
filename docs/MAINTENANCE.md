# Maintenance and game updates

GoblinTweaks uses the FFXIVClientStructs and Lumina that **Dalamud ships**, and Dalamud's
AddonLifecycle instead of its own hooks. When the game patches, the Dalamud team updates those,
so most patches need **no change at all** in GoblinTweaks.

## When to act

| Situation | Sign | Action |
|---|---|---|
| Hotfix / small patch | Everything works | Nothing |
| Dalamud raises its **API level** (usually with x.0 / x.y patches) | GoblinTweaks shows as outdated in `/xlplugins` | Update the SDK version (below) |
| Something in the game changed for a tweak | The tweak shows **Error** in `/goblintweaks`, details in `/xllog` | Fix that tweak |

Don't update anything "just because": if it works, leave it.

## New Dalamud API level

1. Change the number in `GoblinTweaks/GoblinTweaks.csproj`: `Dalamud.NET.Sdk/15.0.0` → the new one
   (announced in the Dalamud Discord / goatcorp GitHub; usually API N → N+1).
2. `dotnet build -c Release`. Fix any compile errors (usually renamed members, the error tells which).
3. Test in game, bump `<Version>`, publish a release (see RELEASING.md).

## A tweak shows Error

1. Read the message in the tweak card and the full error in `/xllog`.
2. Usually a window changed: check its node ids with `/xldata` → Addon Inspector and update
   the constants at the top of the tweak.
3. Build, test, release. The other tweaks keep working meanwhile.
