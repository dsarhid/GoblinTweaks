namespace GoblinTweaks;

/// <summary>One version entry in the plugin changelog.</summary>
public sealed record ChangelogEntry(
    string   Version,
    string   Date,
    string   Summary,
    string[] Changes);

/// <summary>
/// All GoblinTweaks releases, newest first.
/// When bumping the version in GoblinTweaks.csproj, prepend a new entry here.
/// </summary>
internal static class Changelog
{
    public static readonly ChangelogEntry[] Entries =
    [
        new("1.0.2", "2026-10-02",
            "Added Server Info Bar timers for retainer ventures and squadron activities.",
            [
                "New: Retainer Venture Timer — countdown in the Server Info Bar for the earliest retainer returning from a venture; hover for per-retainer details",
                "New: Squadron Mission & Training Timer — Server Info Bar countdown for squadron missions and training sessions; hover shows both timers when active",
            ]),

        new("1.0.1", "2024-11-01",
            "Added inventory context-menu shortcut and standalone settings window for Crafting Materials.",
            [
                "New: Right-clicking any inventory item now shows a \"Crafting Materials\" shortcut in the menu",
                "New: Crafting Materials has its own settings window (language picker, inventory sources)",
            ]),

        new("1.0.0", "2024-10-20",
            "Initial release with Crafting Materials Inventory and Recipe List Completion Marks.",
            [
                "New: Crafting Materials Inventory — cross-reference every recipe against your bags, saddlebag, retainers and FC chest",
                "New: Recipe List Completion Marks — adds Crafting Log check marks to already-crafted rows in the Recipes List window",
            ]),
    ];

    /// <summary>The most recent changelog entry (used for the What's New banner).</summary>
    public static ChangelogEntry? Latest => Entries.Length > 0 ? Entries[0] : null;
}
