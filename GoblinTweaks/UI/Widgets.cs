using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Utility;

namespace GoblinTweaks.UI;

/// <summary>Small custom ImGui widgets used by the GoblinTweaks window and by tweak settings.</summary>
public static class Widgets
{
    private static float Scale => ImGuiHelpers.GlobalScale;

    /// <summary>An animated-looking on/off switch. Returns true when clicked.</summary>
    public static bool Switch(string id, ref bool value, bool error = false)
    {
        var height = ImGui.GetFrameHeight() * 0.85f;
        var width = height * 1.9f;
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - height) / 2);

        ImGui.InvisibleButton(id, new Vector2(width, ImGui.GetFrameHeight()));
        var clicked = ImGui.IsItemClicked();
        var hovered = ImGui.IsItemHovered();
        if (clicked)
            value = !value;

        var track = error ? Palette.Error : value ? Palette.Accent : Palette.SwitchOff;
        if (hovered)
            track = track with { W = 0.85f };

        var drawList = ImGui.GetWindowDrawList();
        var radius = height / 2;
        drawList.AddRectFilled(pos, pos + new Vector2(width, height), ImGui.GetColorU32(track), radius);

        var knobX = value ? pos.X + width - radius : pos.X + radius;
        drawList.AddCircleFilled(new Vector2(knobX, pos.Y + radius), radius - 2.5f * Scale, ImGui.GetColorU32(Palette.Knob));

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        return clicked;
    }

    /// <summary>A rounded label, e.g. a category or status.</summary>
    public static void Badge(string text, Vector4 color, Vector4? textColor = null)
    {
        var padding = new Vector2(6, 1) * Scale;
        var size = ImGui.CalcTextSize(text) + padding * 2;
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - size.Y) / 2);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(color), size.Y / 2);
        drawList.AddText(pos + padding, ImGui.GetColorU32(textColor ?? Palette.BadgeText), text);

        ImGui.Dummy(new Vector2(size.X, ImGui.GetFrameHeight()));
    }

    /// <summary>A pill-shaped filter button. Returns true when clicked.</summary>
    public static bool Chip(string text, bool selected)
    {
        var padding = new Vector2(10, 3) * Scale;
        var size = ImGui.CalcTextSize(text) + padding * 2;
        var pos = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton($"##chip_{text}", size);
        var hovered = ImGui.IsItemHovered();

        var background = selected ? Palette.Accent : hovered ? Palette.CardHovered with { W = 0.12f } : Palette.CardHovered;
        var foreground = selected ? ImGui.GetColorU32(Palette.BadgeText) : ImGui.GetColorU32(ImGuiCol.Text);

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(pos, pos + size, ImGui.GetColorU32(background), size.Y / 2);
        drawList.AddText(pos + padding, foreground, text);

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        return clicked;
    }

    /// <summary>Clickable text that opens a web page.</summary>
    public static void Link(string text, string url)
    {
        ImGui.TextColored(Palette.Accent, text);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(url);
        }

        if (ImGui.IsItemClicked())
            Util.OpenLink(url);
    }

    /// <summary>
    /// A setting row: switch, label and an optional help text below.
    /// Returns true when the value changed (the caller saves the settings).
    /// </summary>
    public static bool SettingToggle(string label, string? help, ref bool value)
    {
        var changed = Switch($"##setting_{label}", ref value);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);

        if (ImGui.IsItemClicked())
        {
            value = !value;
            changed = true;
        }

        if (!string.IsNullOrEmpty(help))
        {
            var indent = ImGui.GetFrameHeight() * 0.85f * 1.9f + ImGui.GetStyle().ItemSpacing.X;
            ImGui.Indent(indent);
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Palette.Muted, help);
            ImGui.PopTextWrapPos();
            ImGui.Unindent(indent);
        }

        return changed;
    }
}
