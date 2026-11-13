namespace GoblinTweaks.Hunts;

// No Dalamud types in this folder: GoblinTweaks.Tests compiles these files on their own.

public enum HuntKind { A, S, Fate }

/// <summary>The header button of Goblin Hunts: which rows the table shows.</summary>
public enum HuntFilter { All, S, A, Fate }

public static class HuntFilters
{
    /// <summary>All → S → A → FATE → All.</summary>
    public static HuntFilter Next(HuntFilter f) => f switch
    {
        HuntFilter.All => HuntFilter.S,
        HuntFilter.S => HuntFilter.A,
        HuntFilter.A => HuntFilter.Fate,
        _ => HuntFilter.All,
    };

    public static bool Accepts(HuntFilter f, HuntKind k) => f switch
    {
        HuntFilter.S => k == HuntKind.S,
        HuntFilter.A => k == HuntKind.A,
        HuntFilter.Fate => k == HuntKind.Fate,
        _ => true,
    };

    public static string Label(HuntFilter f) => f switch
    {
        HuntFilter.S => "S",
        HuntFilter.A => "A",
        HuntFilter.Fate => "FATE",
        _ => "All",
    };
}

/// <summary>Which kinds of entries sound the alert: each has its own switch in the settings.</summary>
public static class HuntSounds
{
    public static bool Wants(HuntKind kind, bool s, bool a, bool fate) => kind switch
    {
        HuntKind.S => s,
        HuntKind.A => a,
        _ => fate,
    };
}

/// <summary>Where an entry has been heard of: Sonar's chat messages, Sonar's memory, or both.</summary>
[Flags]
public enum HuntSources { None = 0, Chat = 1, Memory = 2 }

/// <summary>One hunt or FATE: same kind, name, world, zone and instance. Names compare case-insensitively.</summary>
public readonly record struct HuntKey(HuntKind Kind, string Name, uint WorldId, uint ZoneId, uint InstanceId)
{
    public static HuntKey Of(HuntKind kind, string name, uint worldId, uint zoneId, uint instanceId)
        => new(kind, name.Trim().ToUpperInvariant(), worldId, zoneId, instanceId);
}

/// <summary>
/// What one source says about a hunt or FATE. <see cref="X"/>/<see cref="Y"/> are map coordinates;
/// <see cref="Percent"/> is HP for hunts and progress for FATEs (null when unknown);
/// <see cref="Dead"/> means the hunt was killed or the FATE ended.
/// </summary>
public sealed record HuntReport(
    HuntKind Kind, string Name, uint WorldId, string WorldName, uint ZoneId, uint MapId, uint InstanceId,
    float X, float Y, float? Percent, int? Players, bool Dead, DateTime? FoundAt, HuntSources Source)
{
    public HuntKey Key => HuntKey.Of(Kind, Name, WorldId, ZoneId, InstanceId);
}

/// <summary>A row of the table: what is known about one hunt or FATE, merged from every source.</summary>
public sealed class HuntEntry
{
    public HuntKey Key { get; init; }
    public HuntKind Kind { get; init; }
    public string Name { get; set; } = "";
    public uint WorldId { get; init; }
    public string WorldName { get; set; } = "";
    public uint ZoneId { get; init; }
    public uint MapId { get; set; }
    public uint InstanceId { get; init; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Percent { get; set; }
    public int? Players { get; set; }
    public bool Dead { get; set; }
    public DateTime FoundAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeadAt { get; set; }
    public HuntSources Sources { get; set; }
}

/// <summary>How a row is colored: green untouched, orange pulled, red dead, purple FATE.</summary>
public enum HuntRowState { Untouched, Pulled, Dead, Fate }
