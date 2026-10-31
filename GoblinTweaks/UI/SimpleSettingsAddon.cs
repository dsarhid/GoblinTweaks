using System.Numerics;
using GoblinTweaks.Core;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>A number field of a <see cref="SimpleSettingsAddon"/>.</summary>
internal readonly record struct SettingsNumber(string Label, string? Help, int Min, int Max, int Step, Func<int> Get, Action<int> Set);

/// <summary>A switch with an icon on its left and a tooltip, in a <see cref="SettingsGroup"/>.</summary>
internal readonly record struct SettingsIconToggle(uint IconId, string Label, string? Tooltip, Func<bool> Get, Action<bool> Set);

/// <summary>
/// A section of switches of a <see cref="SimpleSettingsAddon"/>, under its own title, with an optional note: plain
/// switches, then switches with icons.
/// </summary>
internal readonly record struct SettingsGroup(string Heading, string? Note, IReadOnlyList<TweakToggle> Toggles, IReadOnlyList<SettingsIconToggle>? IconToggles = null);

/// <summary>
/// Settings window for a tweak whose options are switches, and optionally numbers and more sections of switches:
/// the language of the tweak's windows at the top, then one section with the switches and numbers, then the other
/// sections. The labels come from the tweak, in the language chosen for it.
/// </summary>
internal sealed class SimpleSettingsAddon : SettingsPanelAddon
{
    public Tweak? Tweak { get; init; }

    /// <summary>Title of the section the switches go in.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>The switches, asked for each time the window is built so they show the current values.</summary>
    public Func<IReadOnlyList<TweakToggle>> Toggles { get; init; } = () => [];

    /// <summary>Number fields after the switches.</summary>
    public Func<IReadOnlyList<SettingsNumber>> Numbers { get; init; } = () => [];

    /// <summary>More sections of switches, after the first one.</summary>
    public Func<IReadOnlyList<SettingsGroup>> Groups { get; init; } = () => [];

    /// <summary>When set, a small "?" button at the bottom left opens the tweak's help.</summary>
    public System.Action? HelpClick { get; init; }

    /// <summary>Tooltip of the "?" button.</summary>
    public string HelpTooltip { get; init; } = string.Empty;

    protected override float HeaderHeight => 40f;

    protected override float FooterHeight => HelpClick is null ? 0f : 32f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;

        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 6f));
    }

    protected override void BuildFooter(Vector2 origin, float width)
    {
        if (HelpClick is not { } open) return;

        var help = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = origin + new Vector2(Pad, 5f),
            Size        = new Vector2(24f, 24f),
            TextTooltip = HelpTooltip,
        };
        // Opened a moment later, once the click is over: a window that appears while the button is still down
        // was closing again with the release.
        help.OnClick = () => Svc.Framework.RunOnTick(open, delayTicks: 2);
        help.AttachNode(this);
    }

    protected override void Build()
    {
        Section(Heading);

        foreach (var toggle in Toggles())
        {
            var current = toggle;
            Toggle(current.Label, current.Help, current.Get(), current.Set);
        }

        foreach (var number in Numbers())
        {
            var current = number;
            Number(current.Label, current.Help, current.Min, current.Max, current.Step, current.Get(), current.Set);
        }

        foreach (var group in Groups())
        {
            Section(group.Heading);
            if (!string.IsNullOrEmpty(group.Note))
            {
                Note(group.Note, Pad, ContentW, Muted);
                Space(6f);
            }

            foreach (var toggle in group.Toggles)
            {
                var current = toggle;
                Toggle(current.Label, current.Help, current.Get(), current.Set);
            }

            foreach (var toggle in group.IconToggles ?? [])
            {
                var current = toggle;
                IconToggle(current.IconId, current.Label, current.Tooltip, current.Get(), current.Set);
            }
        }
    }
}
