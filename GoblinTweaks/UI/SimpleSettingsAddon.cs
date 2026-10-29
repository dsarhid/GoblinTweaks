using System.Numerics;
using GoblinTweaks.Core;

namespace GoblinTweaks.UI;

/// <summary>
/// Settings window for a tweak whose options are only on/off switches: the language of the tweak's windows at the
/// top, then one section with the switches. The labels come from the tweak, in the language chosen for it.
/// </summary>
internal sealed class SimpleSettingsAddon : SettingsPanelAddon
{
    public Tweak? Tweak { get; init; }

    /// <summary>Title of the section the switches go in.</summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>The switches, asked for each time the window is built so they show the current values.</summary>
    public Func<IReadOnlyList<TweakToggle>> Toggles { get; init; } = () => [];

    protected override float HeaderHeight => 40f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;

        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 6f));
    }

    protected override void Build()
    {
        Section(Heading);

        foreach (var toggle in Toggles())
        {
            var current = toggle;
            Toggle(current.Label, current.Help, current.Get(), current.Set);
        }
    }
}
