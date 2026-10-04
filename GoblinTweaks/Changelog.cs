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
        new("1.0.5", "2026-10-04",
            "New GoblinSniper market deal finder and Weapon pose per job tweaks, plus a memory estimate on every tweak card.",
            [
                "New: GoblinSniper — scans Universalis for listings far below the item's normal price and lists them in a native window with search, sorting and world/data-age filters",
                "New: GoblinSniper shows the number of deals in the Server Info Bar; click it to open the window",
                "New: GoblinSniper settings — discount, minimum price, scan interval, maximum data age, 14 item types, specific items and item name language",
                "New: Weapon pose per job — remembers the /cpose chosen with the weapon drawn for each job and restores it after switching",
                "New: Each tweak card shows the approximate memory the tweak is using",
            ]),

        new("1.0.4", "2026-10-03",
            "Crafting Materials adds HQ tracking in tooltips, non-log recipe markers, market-price sorting, and an All Recipes view.",
            [
                "New: Ingredient hover tooltips show HQ quantities per location — e.g. \"Retainer: 5 (2 HQ)\"",
                "New: Non-log recipes (master books and special non-housing) show a grey ● dot; new \"Hide non-log\" filter removes them",
                "New: Market price sort via Universalis — loads in the background, unknown prices go last",
                "New: \"All Recipes\" entry in the STATUS sidebar shows every recipe regardless of craft status",
                "Fix: Right-clicking a yellow (saddlebag) ingredient opens the item menu; right-clicking a red craftable ingredient opens its recipe in the Crafting Log",
                "Fix: Scrollbar thumb now enforces a minimum size and never shrinks to near-invisible on large lists",
            ]),

        new("1.0.3", "2026-10-02",
            "Crafting Materials now remembers retainer and FC chest data between sessions and auto-refreshes as your inventory changes.",
            [
                "New: Inventory snapshot is saved to disk — retainer and FC chest items are remembered across logins without needing to visit them again",
                "New: Ingredient rows show a breakdown tooltip (hover) listing how many items you have in each inventory when they're spread across multiple sources",
                "Fix: The ingredient list auto-updates when you move items around while the window is open (1.5 s debounce)",
                "Fix: The selected recipe and its ingredient panel are preserved across all refresh types (manual, auto, and after synthesis)",
                "Fix: Craft check mark updates immediately when a synthesis completes",
            ]),

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
