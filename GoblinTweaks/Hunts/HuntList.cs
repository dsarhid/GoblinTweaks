namespace GoblinTweaks.Hunts;

/// <summary>
/// The entries of Goblin Hunts, merged from Sonar's chat messages and Sonar's memory.
/// A memory report wins over a chat report (it is more complete). Entries Sonar's memory no longer
/// shows are dropped unless the chat also announced them; entries known only from the chat, or kept
/// after the memory read stopped working, follow this list's own timers.
/// </summary>
public sealed class HuntList
{
    public static readonly TimeSpan DeadKeep = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FateChatKeep = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan StaleKeep = TimeSpan.FromHours(1);

    /// <summary>How recently Sonar must have found an entry for its arrival to be news (sound) rather than a world change or a login.</summary>
    public static readonly TimeSpan NewsWindow = TimeSpan.FromMinutes(2);

    private readonly Dictionary<HuntKey, HuntEntry> entries = [];

    public int Count => entries.Count;

    /// <summary>Adds or updates an entry. Returns it only when this call created it.</summary>
    public HuntEntry? Upsert(HuntReport report, DateTime now)
    {
        var key = report.Key;
        var found = report.FoundAt ?? now;
        if (!entries.TryGetValue(key, out var e))
        {
            e = new HuntEntry
            {
                Key = key,
                Kind = report.Kind,
                Name = report.Name.Trim(),
                WorldId = report.WorldId,
                WorldName = report.WorldName,
                ZoneId = report.ZoneId,
                MapId = report.MapId,
                InstanceId = report.InstanceId,
                X = report.X,
                Y = report.Y,
                Percent = report.Percent ?? (report.Kind == HuntKind.Fate ? 0f : 100f),
                Players = report.Players,
                Dead = report.Dead,
                FoundAt = found,
                UpdatedAt = now,
                DeadAt = report.Dead ? now : null,
                Sources = report.Source,
            };
            entries[key] = e;
            return e;
        }

        var hadMemory = e.Sources.HasFlag(HuntSources.Memory);
        if (found < e.FoundAt) e.FoundAt = found;
        e.UpdatedAt = now;
        e.Sources |= report.Source;

        if (report.Source.HasFlag(HuntSources.Memory))
        {
            if (report.Percent is { } pct) e.Percent = pct;
            e.Players = report.Players ?? e.Players;
            e.Dead = report.Dead;
            e.X = report.X;
            e.Y = report.Y;
            if (report.MapId != 0) e.MapId = report.MapId;
            if (report.WorldName.Length > 0) e.WorldName = report.WorldName;
        }
        else
        {
            if (report.Dead) e.Dead = true;
            if (!hadMemory && report.Percent is { } pct) e.Percent = pct;
            e.Players ??= report.Players;
            if (e.MapId == 0) e.MapId = report.MapId;
            if (e.WorldName.Length == 0) e.WorldName = report.WorldName;
        }

        if (e.Dead) e.DeadAt ??= now;
        else e.DeadAt = null;
        return null;
    }

    /// <summary>
    /// Takes Sonar's whole current list. While it can be read, Sonar's view is the authority: entries it showed
    /// and no longer shows go, even if the chat announced them (and so do entries kept after a read failure).
    /// Entries known only from the chat stay; Sonar's memory may not have them yet. Returns the entries this call created, or nothing when <paramref name="announce"/> is false.
    /// </summary>
    public IReadOnlyList<HuntEntry> ReplaceMemory(IReadOnlyList<HuntReport> reports, DateTime now, bool announce)
    {
        var seen = new HashSet<HuntKey>();
        var created = new List<HuntEntry>();
        foreach (var r in reports)
        {
            if (!seen.Add(r.Key)) continue;
            var e = Upsert(r with { Source = HuntSources.Memory }, now);
            if (e != null) created.Add(e);
        }

        foreach (var (key, e) in entries.ToArray())
        {
            if (seen.Contains(key) || e.Sources == HuntSources.Chat) continue;
            entries.Remove(key);
        }

        return announce ? created : [];
    }

    /// <summary>The memory read stopped working: keep the entries, but let this list's own timers decide when they go.</summary>
    public void MemoryLost()
    {
        foreach (var e in entries.Values) e.Sources &= ~HuntSources.Memory;
    }

    /// <summary>HP read from the game when the hunt is nearby; wins over the other sources while it lasts.</summary>
    public void SetLivePercent(HuntKey key, float percent, DateTime now)
    {
        if (!entries.TryGetValue(key, out var e)) return;
        e.Percent = Math.Clamp(percent, 0f, 100f);
        e.UpdatedAt = now;
    }

    public void Expire(DateTime now)
    {
        foreach (var (key, e) in entries.ToArray())
        {
            var gone =
                (e.Kind == HuntKind.Fate && e.Dead)
                || (e.Dead && !e.Sources.HasFlag(HuntSources.Memory) && e.DeadAt is { } dead && now - dead > DeadKeep)
                || (e.Kind == HuntKind.Fate && e.Sources == HuntSources.Chat && now - e.FoundAt > FateChatKeep)
                || (!e.Sources.HasFlag(HuntSources.Memory) && now - e.UpdatedAt > StaleKeep);
            if (gone) entries.Remove(key);
        }
    }

    /// <summary>The entries the filter lets through, newest found first.</summary>
    public IReadOnlyList<HuntEntry> Visible(HuntFilter filter)
        => entries.Values
            .Where(e => HuntFilters.Accepts(filter, e.Kind))
            .OrderByDescending(e => e.FoundAt)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Whether an entry was found recently enough to sound the alert (Sonar's view also gains old entries on a world change).</summary>
    public static bool IsNews(HuntEntry e, DateTime now) => now - e.FoundAt < NewsWindow;

    public static HuntRowState StateOf(HuntEntry e)
        => e.Dead ? HuntRowState.Dead
            : e.Kind == HuntKind.Fate ? HuntRowState.Fate
            : e.Percent < 100f ? HuntRowState.Pulled
            : HuntRowState.Untouched;
}
