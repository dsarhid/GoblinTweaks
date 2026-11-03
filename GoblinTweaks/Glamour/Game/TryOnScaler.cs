using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.Glamour.Game;

/// <summary>
/// Resizes the game's Try On window. Its character preview is drawn about three times larger than the window
/// shows it, so it stays sharp when scaled up. Every time it opens (and when the factor changes) its scale is set to
/// the game's UI scale times the chosen factor and kept on screen; it is set back to the UI scale when the window
/// closes and when this is disposed, so what the game remembers for the window is always its normal size.
/// Framework thread only.
/// </summary>
/// <remarks>
/// The size is always worked out from the game's UI scale, never from the scale the window opens with: the game
/// remembers a window's scale, so a Try On already enlarged opened enlarged next time, and multiplying that again made
/// it grow every time it opened.
/// </remarks>
internal sealed unsafe class TryOnScaler : IDisposable
{
    private const string Addon = "Tryon";

    private float _factor = 1f;
    private bool _changed;   // a Try On window was given a size of ours since the last restore

    private readonly Action<Exception> _fail;

    /// <param name="fail">Called with an unexpected error, to turn the tweak off safely.</param>
    public TryOnScaler(Action<Exception> fail)
    {
        _fail = fail;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Addon, OnSetup);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, Addon, OnClosing);
    }

    /// <summary>The size of the Try On window, as a factor of its normal size (1 = normal, 0.25 to 3). Applies at once if it is open.</summary>
    public void SetFactor(float factor)
    {
        _factor = Math.Clamp(factor, 0.25f, 3f);
        Apply(Current());
    }

    public void Dispose()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, Addon, OnSetup);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, Addon, OnClosing);
        Restore(Current());
    }

    private static AtkUnitBase* Current() => (AtkUnitBase*)Svc.GameGui.GetAddonByName(Addon).Address;

    /// <summary>The scale every window has at its normal size: the game's UI scale (1 when it cannot be read).</summary>
    private static float NormalScale()
    {
        var scale = AtkUnitBase.GetGlobalUIScale();
        return scale > 0f ? scale : 1f;
    }

    private void OnSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            Apply((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            _fail(ex);
        }
    }

    private void OnClosing(AddonEvent type, AddonArgs args)
    {
        try
        {
            Restore((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            _fail(ex);
        }
    }

    private void Apply(AtkUnitBase* addon)
    {
        if (addon == null || addon->RootNode == null)
            return;
        addon->SetScale(NormalScale() * _factor, true);
        _changed = true;
        KeepOnScreen(addon);
    }

    private void Restore(AtkUnitBase* addon)
    {
        if (addon == null || !_changed)
            return;
        addon->SetScale(NormalScale(), true);
        _changed = false;
    }

    /// <summary>Moves the window back inside the screen if growing pushed it past the right or bottom edge.</summary>
    private static void KeepOnScreen(AtkUnitBase* addon)
    {
        var device = Device.Instance();
        if (device == null)
            return;
        var width = addon->RootNode->Width * addon->Scale;
        var height = addon->RootNode->Height * addon->Scale;
        var x = Math.Max(0f, Math.Min(addon->X, device->Width - width));
        var y = Math.Max(0f, Math.Min(addon->Y, device->Height - height));
        if ((short)x != addon->X || (short)y != addon->Y)
            addon->SetPosition((short)x, (short)y);
    }
}
