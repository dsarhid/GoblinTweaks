using System.Numerics;
using GoblinTweaks.Tweaks;

namespace GoblinTweaks.UI;

/// <summary>Native settings window of Goblin Hotbar Icons: language of its windows, the circle, the ring, the charge counter and the sphere.</summary>
internal sealed class GoblinHotbarSettingsAddon : SettingsPanelAddon
{
    public GoblinHotbarIcons? Tweak { get; init; }

    protected override float HeaderHeight => 40f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;

        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 6f));
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var options = Tweak.Config;

        Section(Tweak.Text("Section.Shape"));

        Number(Tweak.Text("CircleSize"), Tweak.Text("CircleSize.Help"),
            GoblinHotbarIcons.MinCircleSize, GoblinHotbarIcons.MaxCircleSize, 5, options.CircleSize,
            value => { options.CircleSize = value; Tweak.SaveConfig(); });
        Toggle(Tweak.Text("Border"), Tweak.Text("Border.Help"), options.Border,
            on => { options.Border = on; Tweak.SaveConfig(); });
        Toggle(Tweak.Text("ChargesOutside"), Tweak.Text("ChargesOutside.Help"), options.ChargesOutside,
            on => { options.ChargesOutside = on; Tweak.SaveConfig(); });

        Section(Tweak.Text("Section.Sphere"));

        Toggle(Tweak.Text("Sphere"), Tweak.Text("Sphere.Help"), options.Sphere,
            on => { options.Sphere = on; Tweak.SaveConfig(); });
        Number(Tweak.Text("SphereStrength"), Tweak.Text("SphereStrength.Help"),
            GoblinHotbarIcons.MinSphereStrength, GoblinHotbarIcons.MaxSphereStrength, 10, options.SphereStrength,
            value => { options.SphereStrength = value; Tweak.SaveConfig(); });
    }
}
