using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.Core;

/// <summary>Helpers to find the game's windows and fire their callbacks the way the game itself does.</summary>
internal static unsafe class AddonCallback
{
    /// <summary>True when the window exists, is visible and finished loading.</summary>
    public static bool Ready(string name, out AtkUnitBase* addon)
    {
        addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded;
    }

    /// <summary>
    /// Fires a callback. Values keep the type the game uses: int, uint, or null for an undefined slot
    /// (the diagnostic log prints them as int:, uint: and Undefined).
    /// </summary>
    public static void Fire(AtkUnitBase* addon, params object?[] values)
    {
        var atk = stackalloc AtkValue[Math.Max(1, values.Length)];
        for (var i = 0; i < values.Length; i++)
        {
            atk[i] = default;
            switch (values[i])
            {
                case int n:  atk[i].Type = AtkValueType.Int;  atk[i].Int  = n; break;
                case uint n: atk[i].Type = AtkValueType.UInt; atk[i].UInt = n; break;
                default:     break; // undefined
            }
        }

        addon->FireCallback((uint)values.Length, atk, true);
    }

    /// <summary>The visible entries of an open SelectString menu, in order.</summary>
    public static List<string> MenuEntries(AtkUnitBase* menu)
    {
        // The entries are the run of strings starting at value 7.
        var entries = new List<string>();
        for (var i = 7; i < menu->AtkValuesCount; i++)
        {
            var v = menu->AtkValues[i];
            if (v.Type is not (AtkValueType.String or AtkValueType.String8 or AtkValueType.ManagedString)) break;
            entries.Add(v.GetValueAsString());
        }

        return entries;
    }
}
