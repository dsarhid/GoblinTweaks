using System.Numerics;
using GoblinTweaks.Tweaks;

namespace GoblinTweaks.UI;

/// <summary>Native settings window of the Crafting Materials tweak: language of the names and which inventories to count.</summary>
internal sealed class CraftingSettingsAddon : SettingsPanelAddon
{
    public CraftingMaterials? Tweak { get; init; }

    protected override float HeaderHeight => 86f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;
        var options = Tweak.Current;

        LanguageSelect(origin, width, Tweak.Text("Language"), Tweak.Text("Language.Help"), Tweak.LanguageChoices,
            () => options.DataLanguage,
            code => { options.DataLanguage = code; Tweak.SaveCurrent(); Tweak.RefreshSettingsWindow(); });
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
        Toggle(Tweak.Text("HighlightFCChest"), Tweak.Text("HighlightFCChest.Help"), options.HighlightFCChest,
            on => { options.HighlightFCChest = on; Tweak.SaveCurrent(); });
    }
}
