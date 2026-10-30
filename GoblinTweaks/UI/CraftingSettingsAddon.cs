using System.Numerics;
using GoblinTweaks.Tweaks;

namespace GoblinTweaks.UI;

/// <summary>Native settings window of the Crafting Materials tweak: language of its windows, which inventories to count and when a recipe is "almost" craftable.</summary>
internal sealed class CraftingSettingsAddon : SettingsPanelAddon
{
    public CraftingMaterials? Tweak { get; init; }

    protected override float HeaderHeight => 40f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;

        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 6f));
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var options = Tweak.Current;

        Section(Tweak.Text("Sources"));

        Toggle(Tweak.Text("IncludeSaddlebag"), null, options.IncludeSaddlebag,
            on => { options.IncludeSaddlebag = on; Tweak.SaveCurrent(); });
        Toggle(Tweak.Text("IncludeRetainers"), Tweak.Text("IncludeRetainers.Help"), options.IncludeRetainers,
            on => { options.IncludeRetainers = on; Tweak.SaveCurrent(); });
        Toggle(Tweak.Text("IncludeFCChest"), Tweak.Text("IncludeFCChest.Help"), options.IncludeFCChest,
            on => { options.IncludeFCChest = on; Tweak.SaveCurrent(); });
        Toggle(Tweak.Text("Highlight"), Tweak.Text("Highlight.Help"), options.Highlight,
            on => { options.Highlight = on; Tweak.SaveCurrent(); });
        Toggle(Tweak.Text("HighlightShops"), Tweak.Text("HighlightShops.Help"), options.HighlightShops,
            on => { options.HighlightShops = on; Tweak.SaveCurrent(); });

        Section(Tweak.Text("Status"));

        Number(Tweak.Text("NearCompleteMissing"), Tweak.Text("NearCompleteMissing.Help"),
            0, CraftingMaterials.MaxNearCompleteMissing, 1, options.NearCompleteMissing,
            value => { options.NearCompleteMissing = value; Tweak.SaveCurrent(); });
    }
}
