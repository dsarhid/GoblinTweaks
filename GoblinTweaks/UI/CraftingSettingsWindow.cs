using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GoblinTweaks.Tweaks;

namespace GoblinTweaks.UI;

/// <summary>
/// Standalone settings window for the Crafting Materials tweak, opened from its "Settings" button
/// (same idea as the GoblinTweaks settings window, rather than inline options under the card).
/// </summary>
public sealed class CraftingSettingsWindow : Window
{
    private const string WindowId = "###GoblinTweaksCraftingSettings";

    private readonly CraftingMaterials _tweak;

    public CraftingSettingsWindow(CraftingMaterials tweak)
        : base(tweak.SettingsTitle + WindowId, ImGuiWindowFlags.NoCollapse)
    {
        _tweak = tweak;

        Size            = new Vector2(420, 380);
        SizeCondition   = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 240),
            MaximumSize = new Vector2(900, 900),
        };
    }

    public override void PreDraw()
    {
        // Keep the title in the current UI language; the ### id keeps the window stable.
        WindowName = _tweak.SettingsTitle + WindowId;
        base.PreDraw();
    }

    public override void Draw() => _tweak.DrawConfigContents();
}
