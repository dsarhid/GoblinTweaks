using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;

namespace GoblinTweaks.UI;

/// <summary>Main window: header with search, category filters and one card per tweak.</summary>
public sealed class MainWindow : Window
{
    private const string FilterEnabled = "__enabled";
    private const string FilterDisabled = "__disabled";

    private readonly TweakManager _manager;
    private readonly Action _openSettings;
    private readonly Action _openChangelog;
    private readonly HashSet<string> _expanded = [];
    private readonly string _version;
    private string _search = string.Empty;
    private string? _filter;

    public MainWindow(TweakManager manager, Action openSettings, Action openChangelog)
        : base("GoblinTweaks###GoblinTweaksMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _manager = manager;
        _openSettings = openSettings;
        _openChangelog = openChangelog;
        _version = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");

        Size = new Vector2(680, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 380),
            MaximumSize = new Vector2(1600, 1400),
        };
    }

    private static float Scale => ImGuiHelpers.GlobalScale;

    public override void Draw()
    {
        _manager.UpdateMemoryUsage();

        DrawHeader();
        DrawFilters();
        ImGui.Separator();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using (var list = ImRaii.Child("##tweaks", new Vector2(-1, -footerHeight)))
        {
            if (list)
            {
                if (_manager.Store.Data.ShowWelcome)
                    DrawWelcome();

                DrawWhatsNew();
                DrawTweakList();
            }
        }

        DrawFooter();
    }

    private void DrawHeader()
    {
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var iconSize = new Vector2(40, 40) * Scale;
        var icon = Svc.Textures.GetFromManifestResource(Assembly.GetExecutingAssembly(), "GoblinTweaks.Assets.Icon.png").GetWrapOrDefault();
        if (icon != null)
        {
            ImGui.Image(icon.Handle, iconSize);
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextColored(Palette.Accent, "GoblinTweaks");
        ImGui.TextColored(Palette.Muted, Loc.Format("Window.Subtitle", _manager.EnabledCount, _manager.Tweaks.Count));
        ImGui.EndGroup();

        var searchWidth = 230 * Scale;
        var buttonWidth = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(rightEdge - searchWidth - spacing - buttonWidth);
        var rowY = ImGui.GetCursorPosY() + (iconSize.Y - ImGui.GetFrameHeight()) / 2;
        ImGui.SetCursorPosY(rowY);
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##search", Loc.Get("Window.Search"), ref _search, 128);

        ImGui.SameLine();
        ImGui.SetCursorPosY(rowY);
        bool settingsClicked;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            settingsClicked = ImGui.Button(FontAwesomeIcon.Cog.ToIconString(), new Vector2(buttonWidth, ImGui.GetFrameHeight()));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Loc.Get("Settings.Title"));
        if (settingsClicked)
            _openSettings();

        ImGui.Spacing();
    }

    private void DrawFilters()
    {
        if (Widgets.Chip(Loc.Get("Window.Filter.All"), _filter == null))
            _filter = null;

        ImGui.SameLine();
        if (Widgets.Chip(Loc.Get("Window.Filter.Enabled"), _filter == FilterEnabled))
            _filter = FilterEnabled;

        ImGui.SameLine();
        if (Widgets.Chip(Loc.Get("Window.Filter.Disabled"), _filter == FilterDisabled))
            _filter = FilterDisabled;

        foreach (var category in _manager.Tweaks.Select(tweak => tweak.Category).Distinct().Order())
        {
            ImGui.SameLine();
            var key = category.ToString();
            if (Widgets.Chip(Loc.Get($"Category.{key}", key), _filter == key))
                _filter = key;
        }

        ImGui.Spacing();
    }

    private void DrawWhatsNew()
    {
        var store = _manager.Store;
        var latest = Changelog.Latest;

        // Only show for existing users who haven't seen this version yet.
        // New installs (null) skip the banner silently — they just installed it.
        if (latest == null || store.Data.LastSeenVersion == null || store.Data.LastSeenVersion == latest.Version)
            return;

        DrawCard("whats-new", Palette.Accent, () =>
        {
            ImGui.TextColored(Palette.Accent, Loc.Get("Window.WhatsNew.Title"));
            ImGui.SameLine();
            ImGui.TextColored(Palette.Muted, $"v{latest.Version} — {latest.Date}");

            ImGui.PushTextWrapPos(0);
            ImGui.Text(latest.Summary);
            ImGui.PopTextWrapPos();

            ImGui.Spacing();
            foreach (var change in latest.Changes)
            {
                ImGui.TextColored(Palette.Accent, "•");
                ImGui.SameLine();
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(change);
                ImGui.PopTextWrapPos();
            }

            ImGui.Spacing();
            if (ImGui.Button(Loc.Get("Window.WhatsNew.SeeAll")))
                _openChangelog();

            ImGui.SameLine();
            if (ImGui.Button(Loc.Get("Window.WhatsNew.Dismiss")))
            {
                store.Data.LastSeenVersion = latest.Version;
                store.Save();
            }
        });
    }

    private void DrawWelcome()
    {
        DrawCard("welcome", Palette.Accent, () =>
        {
            ImGui.TextColored(Palette.Accent, Loc.Get("Window.Welcome.Title"));
            ImGui.PushTextWrapPos(0);
            ImGui.Text(Loc.Get("Window.Welcome.Text"));
            ImGui.PopTextWrapPos();

            if (ImGui.Button(Loc.Get("Window.Welcome.Dismiss")))
            {
                _manager.Store.Data.ShowWelcome = false;
                _manager.Store.Save();
            }
        });
    }

    private void DrawTweakList()
    {
        var tweaks = _manager.Tweaks
            .Where(MatchesFilter)
            .Where(MatchesSearch)
            .OrderBy(tweak => tweak.Category)
            .ThenBy(tweak => tweak.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (tweaks.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Palette.Muted, Loc.Get("Window.Empty"));
            return;
        }

        foreach (var tweak in tweaks)
            DrawTweakCard(tweak);
    }

    private bool MatchesFilter(Tweak tweak) => _filter switch
    {
        null => true,
        FilterEnabled => tweak.State == TweakState.Enabled,
        FilterDisabled => tweak.State != TweakState.Enabled,
        _ => tweak.Category.ToString() == _filter,
    };

    // Searches names and descriptions in every language, whatever language is selected.
    private bool MatchesSearch(Tweak tweak)
        => string.IsNullOrWhiteSpace(_search)
            || tweak.SearchTexts.Any(text => text.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase));

    private void DrawTweakCard(Tweak tweak)
    {
        var accent = tweak.State switch
        {
            TweakState.Enabled => Palette.Accent,
            TweakState.Error => Palette.Error,
            _ => Palette.SwitchOff,
        };

        DrawCard(tweak.Id, accent, () =>
        {
            using var id = ImRaii.PushId(tweak.Id);

            var enabled = tweak.State != TweakState.Disabled;
            if (Widgets.Switch("##toggle", ref enabled, tweak.State == TweakState.Error))
                _manager.SetEnabled(tweak, enabled);

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(tweak.Name);

            ImGui.SameLine();
            Widgets.Badge(Loc.Get($"Category.{tweak.Category}", tweak.Category.ToString()), Palette.AccentDim, Palette.Accent);

            if (tweak.MemoryBytes >= 0)
            {
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Palette.Muted, FormatMemory(tweak.MemoryBytes));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Loc.Get("Window.Memory.Tooltip"));
            }

            if (!string.IsNullOrEmpty(tweak.Description))
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextColored(Palette.Muted, tweak.Description);
                ImGui.PopTextWrapPos();
            }

            if (tweak.State == TweakState.Error)
                DrawError(tweak);

            // Options are only reachable while the tweak is enabled.
            if (tweak.HasSettings && tweak.State == TweakState.Enabled)
                DrawSettingsSection(tweak);
        });
    }

    private static string FormatMemory(long bytes) => bytes switch
    {
        < 1024 => $"~{bytes} B",
        < 1024 * 1024 => $"~{bytes / 1024.0:0.#} KB",
        _ => $"~{bytes / (1024.0 * 1024.0):0.##} MB",
    };

    private void DrawError(Tweak tweak)
    {
        ImGui.Spacing();
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Palette.Error, $"{Loc.Get("Window.Status.Error")}: {tweak.ErrorMessage}");
        ImGui.TextColored(Palette.Muted, Loc.Get("Window.Status.ErrorHint"));
        ImGui.PopTextWrapPos();

        if (ImGui.Button(Loc.Get("Window.Retry")))
        {
            _manager.SetEnabled(tweak, false);
            _manager.SetEnabled(tweak, true);
        }
    }

    private void DrawSettingsSection(Tweak tweak)
    {
        var open = _expanded.Contains(tweak.Id);
        var icon = open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.Cog;
        var label = Loc.Get(open ? "Window.Settings.Hide" : "Window.Settings.Show");

        ImGui.Spacing();
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(Palette.Accent, icon.ToIconString());
        var iconClicked = ImGui.IsItemClicked();

        ImGui.SameLine();
        ImGui.TextColored(Palette.Accent, label);
        var labelClicked = ImGui.IsItemClicked();
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        if (iconClicked || labelClicked)
        {
            if (!_expanded.Add(tweak.Id))
                _expanded.Remove(tweak.Id);
        }

        if (!open)
            return;

        ImGui.Spacing();
        ImGui.Indent(8 * Scale);
        try
        {
            tweak.DrawSettings();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "Error drawing settings of {tweak}", tweak.Id);
        }
        ImGui.Unindent(8 * Scale);
    }

    /// <summary>Draws content inside a rounded card with a colored bar on the left.</summary>
    private static void DrawCard(string id, Vector4 accent, Action content)
    {
        var padding = new Vector2(12, 10) * Scale;
        var barWidth = 4 * Scale;
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        // Content goes on the top channel, the background is drawn afterwards on the bottom one.
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + padding + new Vector2(barWidth, 0));
        var wrapWidth = width - padding.X * 2 - barWidth;

        using (ImRaii.PushId(id))
        {
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
            content();
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
        }

        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + padding.Y);
        var hovered = ImGui.IsMouseHoveringRect(start, end);

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(hovered ? Palette.CardHovered : Palette.CardBackground), 8 * Scale);
        drawList.AddRectFilled(start, new Vector2(start.X + barWidth, end.Y), ImGui.GetColorU32(accent), 8 * Scale, ImDrawFlags.RoundCornersLeft);
        drawList.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 8 * Scale));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawFooter()
    {
        ImGui.Separator();
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.TextColored(Palette.Muted, _version);

        ImGui.SameLine();
        ImGui.TextColored(Palette.Muted, "·");
        ImGui.SameLine();
        Widgets.Link(Loc.Get("Window.Footer.Repository"), PluginInfo.RepositoryUrl);

        ImGui.SameLine();
        ImGui.TextColored(Palette.Muted, "·");
        ImGui.SameLine();
        if (Widgets.FooterButton(Loc.Get("Window.Footer.Changelog")))
            _openChangelog();

        var issues = Loc.Get("Window.Footer.Issues");
        ImGui.SameLine(rightEdge - ImGui.CalcTextSize(issues).X);
        Widgets.Link(issues, PluginInfo.RepositoryUrl + "/issues");
    }
}
