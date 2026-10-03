using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using GoblinTweaks.Localization;

namespace GoblinTweaks.UI;

/// <summary>Scrollable list of all GoblinTweaks releases, newest first.</summary>
public sealed class ChangelogWindow : Window
{
    public ChangelogWindow() : base("GoblinTweaks — Changelog###GoblinTweaksChangelog")
    {
        Size = new Vector2(620, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(1200, 1000),
        };
    }

    private static float Scale => ImGuiHelpers.GlobalScale;

    public override void Draw()
    {
        foreach (var entry in Changelog.Entries)
            DrawEntry(entry);
    }

    private static void DrawEntry(ChangelogEntry entry)
    {
        var padding  = new Vector2(12, 10) * Scale;
        var barWidth = 4 * Scale;
        var drawList = ImGui.GetWindowDrawList();
        var start    = ImGui.GetCursorScreenPos();
        var width    = ImGui.GetContentRegionAvail().X;

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + padding + new Vector2(barWidth, 0));
        var wrapWidth = width - padding.X * 2 - barWidth;

        using (ImRaii.PushId(entry.Version))
        {
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);

            // Version + date header
            ImGui.TextColored(Palette.Accent, $"v{entry.Version}");
            ImGui.SameLine();
            ImGui.TextColored(Palette.Muted, $"— {entry.Date}");

            // One-line summary
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
            ImGui.TextColored(Palette.Muted, entry.Summary);
            ImGui.PopTextWrapPos();

            // Bullet list of changes
            ImGui.Spacing();
            foreach (var change in entry.Changes)
            {
                ImGui.TextColored(Palette.Accent, "•");
                ImGui.SameLine();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth - 16 * Scale);
                ImGui.TextUnformatted(change);
                ImGui.PopTextWrapPos();
            }

            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
        }

        var end     = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + padding.Y);
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(Palette.CardBackground), 8 * Scale);
        drawList.AddRectFilled(start, new Vector2(start.X + barWidth, end.Y), ImGui.GetColorU32(Palette.Accent), 8 * Scale, ImDrawFlags.RoundCornersLeft);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 8 * Scale));
        ImGui.Dummy(Vector2.Zero);
    }
}
