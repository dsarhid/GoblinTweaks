using GoblinTweaks.Localization;

namespace GoblinTweaks.UI;

/// <summary>Scrollable list of all GoblinTweaks releases, newest first.</summary>
internal sealed class ChangelogAddon : SettingsPanelAddon
{
    protected override void Build()
    {
        foreach (var entry in Changelog.Entries)
        {
            Section($"v{entry.Version} — {entry.Date}");
            Note(entry.LocalSummary, Pad, ContentW, Muted, 13);
            Space(6f);

            foreach (var change in entry.LocalChanges)
                Note("• " + change, Pad + 8f, ContentW - 8f, White, 13);
        }
    }
}
