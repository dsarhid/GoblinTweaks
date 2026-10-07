using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.UI;

/// <summary>
/// While the FC chest window is open, outlines the tab buttons and item slots that hold the
/// ingredients of the recipe selected in Crafting Materials. Drawn as an ImGui overlay (no native
/// nodes are created), and it silently does nothing if the chest layout isn't what it expects.
/// </summary>
internal sealed unsafe class FCChestHighlighter
{
    private const string ChestAddon = "FreeCompanyChest";
    private const int    PageCount  = 5;
    private const int    SlotsPerPage = 35;

    private static readonly uint ColorBorder = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.2f, 1f));
    private static readonly uint ColorFill   = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.2f, 0.18f));

    /// <summary>Supplies the (tab, slot) pairs to mark, 1-based. Null/empty = nothing to mark.</summary>
    public Func<IReadOnlyCollection<(int Tab, int Slot)>>? Source;

    private bool _loggedMismatch;

    public void Draw()
    {
        try
        {
            var marks = Source?.Invoke();
            if (marks is null || marks.Count == 0) return;

            var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName(ChestAddon).Address;
            if (addon == null || !addon->IsVisible || addon->UldManager.NodeList == null) return;

            var slots = new List<Pointer>();
            var tabs  = new List<Pointer>();
            Collect(&addon->UldManager, slots, tabs, addon->Scale, 0);

            if (slots.Count != SlotsPerPage || tabs.Count != PageCount)
            {
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

            var draw  = ImGui.GetForegroundDrawList();
            var pulse = 0.6f + 0.4f * MathF.Sin((float)ImGui.GetTime() * 4f);
            var pulsed = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.2f, pulse));

            foreach (var tab in marks.Select(m => m.Tab).Distinct())
                if (tab >= 1 && tab <= tabs.Count && tab != currentTab)
                    Outline(draw, tabs[tab - 1], pulsed);

            foreach (var (tab, slot) in marks)
                if (tab == currentTab && slot >= 1 && slot <= slots.Count)
                    Outline(draw, slots[slot - 1], ColorBorder, fill: true);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "FCChestHighlighter: draw failed");
            Source = null; // stop drawing rather than spam the log every frame
        }
    }

    private static void Outline(ImDrawListPtr draw, Pointer p, uint color, bool fill = false)
    {
        var min = new Vector2(p.X, p.Y);
        var max = new Vector2(p.X + p.W, p.Y + p.H);
        if (fill) draw.AddRectFilled(min, max, ColorFill);
        draw.AddRect(min, max, color, 0f, ImDrawFlags.None, 2f);
    }

    private static int Compare(Pointer a, Pointer b, float rowTolerance) =>
        MathF.Abs(a.Y - b.Y) > rowTolerance ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);

    private readonly record struct Pointer(float X, float Y, float W, float H, bool Selected);

    private static void Collect(AtkUldManager* uld, List<Pointer> slots, List<Pointer> tabs, float scale, int depth)
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

            var p = new Pointer(node->ScreenX, node->ScreenY, node->Width * node->ScaleX * scale, node->Height * node->ScaleY * scale, false);

            if (type == ComponentType.DragDrop)
                slots.Add(p);
            else if (type == ComponentType.RadioButton)
                tabs.Add(p with { Selected = ((AtkComponentRadioButton*)component)->IsSelected });
            else
                Collect(&component->UldManager, slots, tabs, scale, depth + 1);
        }
    }
}
