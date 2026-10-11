using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>The Limit Break gauge of your party, as the game keeps it.</summary>
internal static unsafe class LimitBreak
{
    /// <summary>How many bars of the gauge are full: 0 while no Limit Break can be used. -1 when it cannot be read (PvP, no gauge).</summary>
    public static int Level()
    {
        var gauge = LimitBreakController.Instance();
        if (gauge == null || gauge->IsPvP || gauge->BarUnits == 0) return -1;

        return Math.Min(gauge->CurrentUnits / gauge->BarUnits, (int)gauge->BarCount);
    }
}

/// <summary>
/// Writes "LB Lv 2" over the Limit Break gauge for as long as a Limit Break can be used. The text is a native node
/// added to the gauge window, removed before the window closes; with an unexpected layout it just shows nothing.
/// </summary>
internal sealed unsafe class LimitBreakDisplay(Func<bool> enabled, Func<Vector4?> outline, Func<int, string> text, Action<Exception> fail)
{
    private const string AddonName = "_LimitBreak";

    private static readonly Vector4 Gold    = new(1f, 0.85f, 0.2f, 1f);

    private TextNode? _node;

    public void Start()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnPreDraw);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
    }

    public void Stop()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, AddonName, OnPreDraw);
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
        Dispose();
    }

    private void OnPreFinalize(AddonEvent type, AddonArgs args) => Dispose();

    private void OnPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            Update((AtkUnitBase*)args.Addon.Address);
        }
        catch (Exception ex)
        {
            fail(ex);
        }
    }

    private void Update(AtkUnitBase* addon)
    {
        var level = enabled() ? LimitBreak.Level() : 0;
        if (addon == null || addon->RootNode == null || level < 1)
        {
            if (_node != null) _node.IsVisible = false;
            return;
        }

        if (_node == null)
        {
            _node = new TextNode
            {
                TextColor     = Gold,
                FontSize      = 18,
                AlignmentType = AlignmentType.Center,
            };
            _node.AttachNode(addon);
        }

        // The outline of the messages of this tweak, when it is on.
        if (outline() is { } edge)
        {
            _node.TextOutlineColor = edge;
            _node.AddTextFlags(TextFlags.Edge);
        }
        else
        {
            _node.RemoveTextFlags(TextFlags.Edge);
        }

        // Centered over the gauge.
        var root = addon->RootNode;
        _node.IsVisible = true;
        _node.String    = text(level);
        _node.Size      = new Vector2(Math.Max(root->Width, 120f), 24f);
        _node.Position  = new Vector2(root->Width / 2f - _node.Size.X / 2f, -24f);
    }

    private void Dispose()
    {
        _node?.Dispose();
        _node = null;
    }
}
