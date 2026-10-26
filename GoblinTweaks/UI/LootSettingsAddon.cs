using System.Numerics;
using GoblinTweaks.Tweaks;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>Native settings window of Goblin Loot: when the window opens, what it follows, and the language of its windows.</summary>
internal sealed class LootSettingsAddon : SettingsPanelAddon
{
    public GoblinLoot? Tweak { get; init; }

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

        Section(Tweak.Text("Section.Window"));
        Toggle(Tweak.Text("Setting.AutoOpen"), Tweak.Text("Setting.AutoOpen.Help"), options.AutoOpen,
            on => { options.AutoOpen = on; Tweak.Changed(); });
        Toggle(Tweak.Text("Setting.Follow"), Tweak.Text("Setting.Follow.Help"), options.FollowSelection,
            on => { options.FollowSelection = on; Tweak.Changed(); });
    }
}
