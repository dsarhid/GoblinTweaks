using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// While the FC chest window is open, outlines the tab buttons and item slots that hold the
/// ingredients of the recipe selected in Crafting Materials. The outlines are native nodes added to the
/// chest window itself (removed when it closes), and it silently does nothing if the chest layout isn't what it expects.
/// </summary>
internal sealed unsafe class FCChestHighlighter : IDisposable
{
    private const string ChestAddon   = "FreeCompanyChest";
    private const int    PageCount    = 5;
    private const int    SlotsPerPage = 35;

    private static readonly Vector4 Gold = new(1f, 0.85f, 0.2f, 1f);

    /// <summary>Supplies the (tab, slot) pairs to mark, 1-based. Null/empty = nothing to mark.</summary>
    public Func<IReadOnlyCollection<(int Tab, int Slot)>>? Source;

    private sealed record Outline(ColorImageNode? Fill, BorderNineGridNode Border);

    private readonly List<Outline> _outlines = [];
    private bool _loggedMismatch;
    private bool _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        Svc.Framework.Update += OnUpdate;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, ChestAddon, OnChestClosing);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        Svc.Framework.Update -= OnUpdate;
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, ChestAddon, OnChestClosing);
        Clear();
    }

    public void Dispose() => Stop();

    // The chest window is about to be destroyed: our nodes must leave it first.
    private void OnChestClosing(AddonEvent type, AddonArgs args) => Clear();

    private void Clear()
    {
        foreach (var outline in _outlines)
        {
            outline.Fill?.Dispose();
            outline.Border.Dispose();
        }
        _outlines.Clear();
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            var marks = Source?.Invoke();
            if (marks is null || marks.Count == 0) { Clear(); return; }

            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(ChestAddon).Address;
            if (addon == null || !addon->IsVisible || addon->UldManager.NodeList == null) { Clear(); return; }

            var slots = new List<Pointer>();
            var tabs  = new List<Pointer>();
            Collect(&addon->UldManager, slots, tabs, 0);

            if (slots.Count != SlotsPerPage || tabs.Count != PageCount)
            {
                Clear();
                if (!_loggedMismatch)
                {
                    _loggedMismatch = true;
                    Svc.Log.Warning($"FCChestHighlighter: unexpected chest layout ({slots.Count} slots, {tabs.Count} tabs); highlight disabled.");
                }
                return;
            }

            // Order by on-screen position so indexes follow the visual grid / tab order.
            slots.Sort((a, b) => Compare(a, b, rowTolerance: 4f));
            tabs .Sort((a, b) => a.X.CompareTo(b.X));

            var currentTab = -1;
            for (var i = 0; i < tabs.Count; i++)
                if (tabs[i].Selected) { currentTab = i + 1; break; }

            var targets = new List<Target>();
            foreach (var tab in marks.Select(m => m.Tab).Distinct())
                if (tab >= 1 && tab <= tabs.Count && tab != currentTab)
                    targets.Add(new Target(tabs[tab - 1], false, true));

            foreach (var (tab, slot) in marks)
                if (tab == currentTab && slot >= 1 && slot <= slots.Count)
                    targets.Add(new Target(slots[slot - 1], true, false));

            Place(addon, targets);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "FCChestHighlighter: update failed");
            Source = null; // stop instead of spamming the log every frame
            Clear();
        }
    }

    private readonly record struct Target(Pointer Box, bool Fill, bool Pulse);

    private void Place(AtkUnitBase* addon, List<Target> targets)
    {
        // Same kind and count as last frame: only move them. Otherwise start over.
        var same = _outlines.Count == targets.Count;
        for (var i = 0; same && i < targets.Count; i++)
            same = (_outlines[i].Fill is not null) == targets[i].Fill;
        if (!same) Clear();

        var scale = addon->Scale <= 0f ? 1f : addon->Scale;
        var pulse = 0.6f + 0.4f * MathF.Sin(Environment.TickCount64 / 1000f * 4f);

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];

            if (_outlines.Count <= i)
            {
                ColorImageNode? fill = null;
                if (target.Fill)
                {
                    fill = new ColorImageNode { Color = Gold with { W = 0.18f } };
                    fill.AttachNode(addon);
                }

                var border = new BorderNineGridNode { Color = Gold };
                border.AttachNode(addon);
                _outlines.Add(new Outline(fill, border));
            }

            // Screen position -> position inside the chest window.
            var position = new Vector2((target.Box.X - addon->X) / scale, (target.Box.Y - addon->Y) / scale);
            var size     = new Vector2(target.Box.W, target.Box.H);

            var outline = _outlines[i];
            if (outline.Fill is { } fill2)
            {
                fill2.Position = position;
                fill2.Size     = size;
            }
            outline.Border.Position = position;
            outline.Border.Size     = size;
            outline.Border.Color    = Gold with { W = target.Pulse ? pulse : 1f };
        }
    }

    private static int Compare(Pointer a, Pointer b, float rowTolerance) =>
        MathF.Abs(a.Y - b.Y) > rowTolerance ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);

    private readonly record struct Pointer(float X, float Y, float W, float H, bool Selected);

    private static void Collect(AtkUldManager* uld, List<Pointer> slots, List<Pointer> tabs, int depth)
    {
        if (depth > 6 || uld == null || uld->NodeList == null) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || (ushort)node->Type < 1000) continue; // component nodes only

            var compNode  = (AtkComponentNode*)node;
            var component = compNode->Component;
            if (component == null) continue;

            var type = component->GetComponentType();
            if (!node->IsVisible()) continue;

            var p = new Pointer(node->ScreenX, node->ScreenY, node->Width * node->ScaleX, node->Height * node->ScaleY, false);

            if (type == ComponentType.DragDrop)
                slots.Add(p);
            else if (type == ComponentType.RadioButton)
                tabs.Add(p with { Selected = ((AtkComponentRadioButton*)component)->IsSelected });
            else
                Collect(&component->UldManager, slots, tabs, depth + 1);
        }
    }
}
