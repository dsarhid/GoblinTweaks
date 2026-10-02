using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;

namespace GoblinTweaks.UI;

/// <summary>General plugin settings (separate from the per-tweak options shown in the main window).</summary>
public sealed class SettingsWindow : Window
{
    private const string WindowId = "###GoblinTweaksSettings";

    private readonly ConfigStore _config;

    public SettingsWindow(ConfigStore config)
        : base(Loc.Get("Settings.Title") + WindowId, ImGuiWindowFlags.NoCollapse)
    {
        _config = config;

        Size = new Vector2(380, 180);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 140),
            MaximumSize = new Vector2(900, 900),
        };
    }

    public override void PreDraw()
    {
        // Keep the title in the selected language; the ### part keeps the window id stable.
        WindowName = Loc.Get("Settings.Title") + WindowId;
        base.PreDraw();
    }

    public override void Draw()
    {
        ImGui.TextColored(Palette.Accent, Loc.Get("Settings.Language"));
        ImGui.Spacing();

        var current = Loc.Languages.FirstOrDefault(language => language.Code == Loc.CurrentLanguage).Name ?? "English";

        ImGui.SetNextItemWidth(220 * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##language", current))
        {
            if (combo)
            {
                foreach (var (code, name) in Loc.Languages)
                {
                    if (ImGui.Selectable(name, code == Loc.CurrentLanguage) && code != Loc.CurrentLanguage)
                    {
                        _config.Data.Language = code;
                        _config.Save();
                        Loc.SetLanguage(code);
                    }
                }
            }
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Palette.Muted, Loc.Get("Settings.Language.Help"));
        ImGui.PopTextWrapPos();
    }
}
