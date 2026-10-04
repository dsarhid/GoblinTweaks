using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GoblinTweaks.Tweaks;

namespace GoblinTweaks.UI;

/// <summary>
/// Standalone settings window for the GoblinSniper tweak, opened from its "Settings" button
/// or from the gear button of its native window.
/// </summary>
public sealed class GoblinSniperSettingsWindow : Window
{
    private const string WindowId = "###GoblinTweaksSniperSettings";

    private readonly GoblinSniper _tweak;

    public GoblinSniperSettingsWindow(GoblinSniper tweak)
        : base(tweak.SettingsTitle + WindowId, ImGuiWindowFlags.NoCollapse)
    {
        _tweak = tweak;

        Size            = new Vector2(460, 620);
        SizeCondition   = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 300),
            MaximumSize = new Vector2(900, 1200),
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
