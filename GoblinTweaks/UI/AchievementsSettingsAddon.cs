using System.Numerics;
using GoblinTweaks.Tweaks;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>Native settings window of the achievements tweak: the tracker and the notices in chat.</summary>
internal sealed class AchievementsSettingsAddon : SettingsPanelAddon
{
    public GoblinAchievements? Tweak { get; init; }

    private const float FooterHeight = 34f;

    protected override float HeaderHeight => 40f;

    protected override Vector2 ScrollSize => base.ScrollSize - new Vector2(0f, FooterHeight);

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;

        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 6f));

        // Bottom left, like the help button of the other windows.
        var help = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = ContentStartPosition + new Vector2(Pad, ContentSize.Y - FooterHeight + 6f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Help.Tooltip"),
        };
        help.OnClick = Tweak.OpenHelp;
        help.AttachNode(this);
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var options = Tweak.Config;

        Section(Tweak.Text("Section.Tracker"));
        Toggle(Tweak.Text("Setting.Visible"), Tweak.Text("Setting.Visible.Help"), options.TrackerVisible,
            on => { options.TrackerVisible = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.HideWhenEmpty"), Tweak.Text("Setting.HideWhenEmpty.Help"), options.HideWhenEmpty,
            on => { options.HideWhenEmpty = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.Locked"), Tweak.Text("Setting.Locked.Help"), options.Locked,
            on => { options.Locked = on; Tweak.Changed(); });

        Toggle(Tweak.Text("Setting.ClosestFirst"), Tweak.Text("Setting.ClosestFirst.Help"), options.ClosestFirst,
            on => { options.ClosestFirst = on; Tweak.Changed(); });

        Section(Tweak.Text("Section.Progress"));
        Toggle(Tweak.Text("Setting.AutoRefresh"), Tweak.Text("Setting.AutoRefresh.Help"), options.AutoRefresh,
            on => { options.AutoRefresh = on; Tweak.Changed(); });
        Number(Tweak.Text("Setting.AutoRefreshMinutes"), null, 5, 60, 1, options.AutoRefreshMinutes,
            value => { options.AutoRefreshMinutes = value; Tweak.Changed(); });

        Section(Tweak.Text("Section.Appearance"));
        Number(Tweak.Text("Setting.Opacity"), Tweak.Text("Setting.Opacity.Help"), 0, 100, 5, options.Opacity,
            value => { options.Opacity = value; Tweak.Changed(); });
        Number(Tweak.Text("Setting.FontSize"), Tweak.Text("Setting.FontSize.Help"), 8, 24, 1, options.FontSize,
            value => { options.FontSize = value; Tweak.Changed(); });

        Section(Tweak.Text("Section.Notices"));
        Toggle(Tweak.Text("Setting.NotifyDone"), Tweak.Text("Setting.NotifyDone.Help"), options.NotifyDone,
            on => { options.NotifyDone = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.NotifyNear"), Tweak.Text("Setting.NotifyNear.Help"), options.NotifyNear,
            on => { options.NotifyNear = on; Tweak.Changed(); });
        Number(Tweak.Text("Setting.NearPercent"), Tweak.Text("Setting.NearPercent.Help"), 50, 99, 1, options.NearPercent,
            value => { options.NearPercent = value; Tweak.Changed(); });
    }
}
