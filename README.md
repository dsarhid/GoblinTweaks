# GoblinTweaks

Small quality of life tweaks for FINAL FANTASY XIV, as a [Dalamud](https://github.com/goatcorp/Dalamud) plugin.
Every tweak is off by default and can be turned on with a switch in `/goblintweaks` (or `/gtweaks`).

## Tweaks

| Tweak | What it does |
|---|---|
| **Recipe List completion marks** | Shows the Crafting Log check mark next to every recipe in the *Recipes List* window that you have already crafted. |

## Install

1. In game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add `https://raw.githubusercontent.com/dsarhid/GoblinTweaks/main/repo.json`, enable it and save.
3. Install **GoblinTweaks** from `/xlplugins`. Updates are installed like any other plugin.

## Development

Requirements: Git, .NET SDK 10, and XIVLauncher with Dalamud (the build references its files).

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup.ps1   # first time only
dotnet build -c Release
```

Add the path of the built `GoblinTweaks.dll` (shown at the end of the build) in
`/xlsettings` → Experimental → Dev Plugin Locations.

| Folder | Contents |
|---|---|
| `GoblinTweaks/Core` | Tweak base classes, discovery, error isolation, configuration |
| `GoblinTweaks/UI` | Main window and reusable widgets |
| `GoblinTweaks/Native` | Helpers to add game-styled elements to native windows |
| `GoblinTweaks/Tweaks` | One file per tweak |
| `GoblinTweaks/Localization` | `en.json`, `es.json` (add a file to add a language) |

Guides: [adding a tweak](docs/ADDING_TWEAKS.md) · [maintenance and game updates](docs/MAINTENANCE.md) · [releasing](docs/RELEASING.md)

## License

[MIT](LICENSE). GoblinTweaks is not affiliated with Square Enix.
FINAL FANTASY XIV © SQUARE ENIX CO., LTD.
