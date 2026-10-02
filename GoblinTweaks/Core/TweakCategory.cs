namespace GoblinTweaks.Core;

/// <summary>Groups tweaks in the main window. Display names live in the localization files ("Category.&lt;Name&gt;").</summary>
public enum TweakCategory
{
    Crafting,
    Gathering,
    Interface,
    Inventory,
    Chat,
    Other,
}

/// <summary>Marks a class as a tweak so the <see cref="TweakManager"/> discovers it.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TweakAttribute(TweakCategory category) : Attribute
{
    public TweakCategory Category { get; } = category;
}

public enum TweakState
{
    Disabled,
    Enabled,
    /// <summary>Failed to enable or crashed while running. The error is shown in the main window.</summary>
    Error,
}
