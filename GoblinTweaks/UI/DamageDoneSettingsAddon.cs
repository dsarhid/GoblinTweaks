using System.Numerics;
using GoblinTweaks.Tweaks;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>Native settings window of Goblin Damage Done: whether the table shows, what it lists and what it counts.</summary>
internal sealed class DamageDoneSettingsAddon : SettingsPanelAddon
{
    public GoblinDamageDone? Tweak { get; init; }

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

        Section(Tweak.Text("Section.Table"));
        Toggle(Tweak.Text("Setting.Visible"), Tweak.Text("Setting.Visible.Help"), options.Visible,
            on => { options.Visible = on; Tweak.Changed(); });
        Number(Tweak.Text("Setting.Rows"), Tweak.Text("Setting.Rows.Help"), 1, GoblinDamageDone.MaxRows, 1, options.Rows,
            value => { options.Rows = value; Tweak.Changed(); });

        Section(Tweak.Text("Section.Appearance"));
        Number(Tweak.Text("Setting.Opacity"), Tweak.Text("Setting.Opacity.Help"), 0, 100, 5, options.Opacity,
            value => { options.Opacity = value; Tweak.Changed(); });

        Number(Tweak.Text("Setting.FontSize"), Tweak.Text("Setting.FontSize.Help"), 8, 24, 1, options.FontSize,
            value => { options.FontSize = value; Tweak.Changed(); });
        Number(Tweak.Text("Setting.Scale"), Tweak.Text("Setting.Scale.Help"), 50, 200, 5, options.Scale,
            value => { options.Scale = value; Tweak.Changed(); });

        Section(Tweak.Text("Section.Alliance"));
        Toggle(Tweak.Text("Setting.ShowGroup"), Tweak.Text("Setting.ShowGroup.Help"), options.ShowGroup,
            on => { options.ShowGroup = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.OnlyMyGroup"), Tweak.Text("Setting.OnlyMyGroup.Help"), options.OnlyMyGroup,
            on => { options.OnlyMyGroup = on; Tweak.Changed(); });

        Section(Tweak.Text("Section.Counting"));
        Toggle(Tweak.Text("Setting.OverTime"), Tweak.Text("Setting.OverTime.Help"), options.IncludeOverTime,
            on => { options.IncludeOverTime = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.Overheal"), Tweak.Text("Setting.Overheal.Help"), options.IncludeOverheal,
            on => { options.IncludeOverheal = on; Tweak.Changed(); });
    }
}
