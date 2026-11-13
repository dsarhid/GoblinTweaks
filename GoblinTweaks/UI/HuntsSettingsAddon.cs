using System.Numerics;
using GoblinTweaks.Tweaks;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>Native settings window of Goblin Hunts: the table, the sound alert and the appearance.</summary>
internal sealed class HuntsSettingsAddon : SettingsPanelAddon
{
    public GoblinHunts? Tweak { get; init; }

    // A fixed strip at the bottom: help on the left, the language on the right, out of the way of the options.
    protected override float FooterHeight => 36f;

    protected override void BuildFooter(Vector2 origin, float width)
    {
        if (Tweak is null) return;

        var help = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = origin + new Vector2(Pad, 7f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Help.Tooltip"),
        };
        help.OnClick = Tweak.OpenHelp;
        help.AttachNode(this);

        // Added last so its list opens on top.
        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 5f));
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var options = Tweak.Config;

        Section(Tweak.Text("Section.Table"));
        Toggle(Tweak.Text("Setting.Visible"), Tweak.Text("Setting.Visible.Help"), options.Visible,
            on => { options.Visible = on; Tweak.Changed(); });
        Number(Tweak.Text("Setting.Rows"), Tweak.Text("Setting.Rows.Help"), 1, HuntTableNode.MaxRows, 1, options.Rows,
            value => { options.Rows = value; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.Locked"), Tweak.Text("Setting.Locked.Help"), options.Locked,
            on => { options.Locked = on; Tweak.Changed(); });

        Section(Tweak.Text("Section.Alert"));
        Toggle(Tweak.Text("Setting.SoundAlert"), Tweak.Text("Setting.SoundAlert.Help"), options.SoundAlert,
            on => { options.SoundAlert = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.SoundS"), Tweak.Text("Setting.SoundS.Help"), options.SoundS,
            on => { options.SoundS = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.SoundA"), Tweak.Text("Setting.SoundA.Help"), options.SoundA,
            on => { options.SoundA = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.SoundFate"), Tweak.Text("Setting.SoundFate.Help"), options.SoundFate,
            on => { options.SoundFate = on; Tweak.Changed(); });
        Number(Tweak.Text("Setting.SoundId"), Tweak.Text("Setting.SoundId.Help"), 1, 16, 1, options.SoundId,
            value =>
            {
                options.SoundId = value;
                Tweak.Changed();
                Tweak.PreviewSound();   // so the user hears the one picked
            });

        Section(Tweak.Text("Section.Appearance"));
        Number(Tweak.Text("Setting.Opacity"), Tweak.Text("Setting.Opacity.Help"), 0, 100, 5, options.Opacity,
            value => { options.Opacity = value; Tweak.Changed(); });
        Number(Tweak.Text("Setting.FontSize"), Tweak.Text("Setting.FontSize.Help"), 8, 24, 1, options.FontSize,
            value => { options.FontSize = value; Tweak.Changed(); });
        Number(Tweak.Text("Setting.Scale"), Tweak.Text("Setting.Scale.Help"), 50, 200, 5, options.Scale,
            value => { options.Scale = value; Tweak.Changed(); });
    }
}
