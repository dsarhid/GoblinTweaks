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
        new("1.0.10", "2026-10-05",
            "Goblin Battle Text: ground damage like Doton shows its name and only when it is yours.",
            [
                "Fix: Goblin Battle Text damage from the ground, like Doton, showed as a bare number with (DoT) — it now shows the name and icon of its effect",
                "Fix: Goblin Battle Text showed the ground damage of other players as yours — only yours shows now, even next to someone of your job",
                "Change: Goblin Battle Text healing over time is told apart by who gave it, as the game reports it, instead of by the effects on whoever is healed",
            ]),

        new("1.0.9", "2026-10-05",
            "Goblin Battle Text: positional alerts, names on damage and healing over time, only your own debuffs, and critical hits kept apart.",
            [
                "New: Goblin Battle Text positional alerts — actions that hit harder from the rear or the flank say whether you hit them from there, as an alert of their own or in the damage message",
                "New: Goblin Battle Text Highlights has a look for a positional that was hit and one for a positional that was missed, and shows one kind of highlight at a time",
                "New: Goblin Battle Text ticks of damage and healing over time show the name and icon of their effect, like Higanbana or Regen",
                "New: Goblin Battle Text auto-attacks say who makes them when it is not you: your chocobo, your pet or the enemy",
                "New: Goblin Battle Text Merge healing on several targets, apart from the option for damage",
                "New: Goblin Battle Text Show who my healing is for — the healing you give ends with the name of its target",
                "Change: Goblin Battle Text critical hits are never merged — they show apart with their own look, and direct hits merge only among themselves",
                "Change: Goblin Battle Text damage over time shared with other players shows your share as an estimate, with ~ before it, and is not shown when none of it is yours",
                "Change: Goblin Battle Text healing over time on you always shows as received, and the one you or your pet give shows as given",
                "Fix: Goblin Battle Text showed the debuffs other players put on enemies — only yours show now, even next to the same debuff from someone of your job",
            ]),

        new("1.0.8", "2026-10-04",
            "Goblin Battle Text: settings redone in 5 tabs, areas you drag on screen, colors per kind of hit, and text for Bloodbath heals and dashes.",
            [
                "New: Goblin Battle Text areas can be dragged on screen by a box with their name while the settings window is open",
                "New: Goblin Battle Text Action with no damage event — dashes and other actions the game shows no text for appear with their icon and name",
                "New: Goblin Battle Text critical, direct and critical direct hits have one color for damage dealt and one for damage taken; critical hits have a third one for healing",
                "New: Goblin Battle Text position has a number box with + and - next to its slider, and alignment has buttons for 0, 50 and 100",
                "New: Goblin Battle Text can copy the look of an area to another one, from the cog next to the areas",
                "New: Goblin Battle Text Messages at once is available for every area, 1 to 15",
                "New: Goblin Battle Text sample text follows the tab on screen — Highlights and Cooldowns show only their messages and play at once what you change",
                "Change: Goblin Battle Text settings go from 8 tabs to 5 — the three areas share one tab with the game's own switch, colors are in General, and everything about the cooldown alert is in Cooldowns",
                "Change: Goblin Battle Text animations are chosen by kind (Gentle, Impacts, Size, Rotation, Movement, Light) and then by animation",
                "Change: Goblin Battle Text cooldown actions are icon buttons with a search box, lit when announced",
                "Change: Goblin Battle Text order of each message is a row of parts, and only lists the parts used by the events sent to the area",
                "Change: Goblin Battle Text Restore button has to be held down, and duration is shown in seconds",
                "Fix: Goblin Battle Text did not show what effects like Bloodbath heal you for, which the game kept showing as its own text",
            ]),

        new("1.0.7", "2026-10-04",
            "Goblin Battle Text: highlights for critical and direct hits with 35 animations, per-area message order, and no more crossing text.",
            [
                "New: Goblin Battle Text Highlights tab — critical hits, direct hits, critical direct hits and the cooldown alert each get their own font, size, color, animation and intensity",
                "New: Goblin Battle Text has 35 animations for highlighted messages, from Pop to Meteor and Fury",
                "New: Goblin Battle Text shows critical direct hits with !!, apart from critical hits (!)",
                "New: Goblin Battle Text order of each message is set per area (Outgoing, Incoming, Center)",
                "New: Goblin Battle Text buffs and debuffs have one color when they start and another when they end",
                "New: Goblin Battle Text has a button to restore the defaults of the tab on screen",
                "New: Goblin Battle Text Center area can limit how many messages show at once (1 to 10)",
                "New: Goblin Battle Text adds the Trump Gothic Italic font",
                "Change: Goblin Battle Text no longer crosses text — events scrolling up and down in one area each take half of it, and static ones pile up outside the path",
                "Change: Goblin Battle Text Center area is static by default",
                "Change: Goblin Battle Text removed the Hide the game's own text and Follow my character options — the game's text is always hidden and the areas stay around the center of the screen",
                "Change: Goblin Battle Text no longer shows an action icon on auto-attacks or on what NPCs do to you; other players' actions (PvP) keep theirs",
                "Fix: Goblin Battle Text buff and debuff icons were squashed",
                "Fix: Goblin Battle Text drop-down lists showed an internal name while closed",
                "Fix: Goblin Battle Text help topic title no longer overflows the list of topics",
            ]),

        new("1.0.6", "2026-10-04",
            "New Goblin Battle Text: your damage, healing, buffs and cooldowns scroll around your character. GoblinSniper adds a resale column.",
            [
                "New: Goblin Battle Text — the damage and healing you deal and receive scroll next to your character instead of over whoever was hit",
                "New: Goblin Battle Text has three areas (Outgoing, Incoming, Centre) with position, size, duration, alignment and path, including a static one",
                "New: Goblin Battle Text events — damage, healing, MP, buffs and debuffs can each be turned off, sent to any area and scroll up, down or stay still",
                "New: Goblin Battle Text cooldown alerts, off by default, with a checklist of the actions of your current job",
                "New: Goblin Battle Text shows action and damage type icons, with the order and visibility of each part of a message configurable",
                "New: Goblin Battle Text colors per kind of text, a help window and the /gbt chat command",
                "New: GoblinSniper Resale column — chance that the item sells on your world within a week, with sorting by it",
                "New: GoblinSniper ignores items nobody bought for too long (60 days by default, configurable)",
                "Fix: Color picker — the textures of the native color picker are now shipped with the plugin",
            ]),

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
