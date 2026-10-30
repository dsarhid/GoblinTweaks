namespace GoblinTweaks.Core;

/// <summary>What a player did with an item of the loot list.</summary>
internal enum LootChoice { None, Need, Greed, Pass }

/// <summary>A player in the roll of one item.</summary>
internal sealed class LootPlayer
{
    public required string Name { get; init; }
    public uint JobId { get; init; }
    public bool IsMe { get; init; }

    /// <summary>A player of another party of the alliance.</summary>
    public bool Outside { get; init; }

    /// <summary>The player has chosen (the game says so as it happens), even if what they chose is not known yet.</summary>
    public bool Cast { get; set; }

    public LootChoice Choice { get; set; }
    public int Value { get; set; }
    public bool Winner { get; set; }

    /// <summary>The roll ended before the player chose: the game passed for them.</summary>
    public bool TimedOut { get; set; }

    /// <summary>The player has not chosen yet.</summary>
    public bool Pending => !Cast && Choice == LootChoice.None;
}

/// <summary>One item of the loot list with what each player of the party did with it.</summary>
internal sealed class LootEntry
{
    public required int Id { get; init; }
    public required uint ItemId { get; init; }
    public uint Chest { get; set; }
    public uint ChestIndex { get; set; }
    public int Count { get; set; } = 1;
    public string ItemName { get; set; } = string.Empty;
    public uint IconId { get; set; }

    /// <summary>The zone the item dropped in.</summary>
    public uint Territory { get; init; }

    /// <summary>The members of your party.</summary>
    public List<LootPlayer> Players { get; init; } = [];

    /// <summary>Players of the other parties of an alliance that rolled, or got the item.</summary>
    public List<LootPlayer> Others { get; } = [];

    /// <summary>The roll is over: the game said who got the item (or that nobody did).</summary>
    public bool Resolved { get; set; }

    /// <summary>The item is in the game's loot list now. Such an entry is never dropped from the history.</summary>
    public bool InList { get; set; }

    /// <summary>When the item left the loot list while its roll was still open.</summary>
    public DateTime? Gone { get; set; }

    /// <summary>Frame counter of the last time the item was in the loot list.</summary>
    public long SeenTick { get; set; }

    /// <summary>Seconds left to roll, from the loot list; 0 when not known.</summary>
    public int SecondsLeft { get; private set; }

    private float _lastTime = -1f;
    private int   _direction;   // +1 the game's time goes up (elapsed), -1 it goes down (left), 0 not known yet

    /// <summary>
    /// The list gives a time and a maximum without saying which way it counts: it is told by how it moves.
    /// </summary>
    public void SetTime(float time, float max)
    {
        if (max <= 0f) { SecondsLeft = 0; return; }

        if (_lastTime >= 0f && time != _lastTime)
            _direction = time > _lastTime ? 1 : -1;
        _lastTime = time;

        var left = _direction > 0 ? max - time : time;
        SecondsLeft = (int)MathF.Ceiling(Math.Clamp(left, 0f, max));
    }

    /// <summary>Who got the item, in your party or out of it.</summary>
    public LootPlayer? Winner => Players.FirstOrDefault(player => player.Winner) ?? Others.FirstOrDefault(player => player.Winner);

    /// <summary>The item went to a player of another party of the alliance.</summary>
    public bool WonOutside => Others.Any(player => player.Winner);

    public int Chosen => Players.Count(player => !player.Pending);
}

/// <summary>An item that was rolled for in the current duty, and who got it.</summary>
internal sealed record LootAward(uint ItemId, string ItemName, uint IconId, int Count, string? Winner, bool Mine, bool Outside, LootChoice Choice, int Value);

/// <summary>
/// What each member of the party did in the roll of each item of the loot list, built from what the game says.
/// The game says who has chosen as it happens, but not what: Need, Greed and the numbers arrive together when the
/// roll is over, and a Pass is never said at all (whoever chose and did not roll, passed).
/// </summary>
internal sealed class LootRolls(Func<List<LootPlayer>> roster, Func<uint, (string Name, uint IconId)> itemInfo, Func<uint> territory)
{
    /// <summary>
    /// How many items are kept. Items still in the game's loot list (up to 16) are always kept on top of this, so a
    /// big raid chest never pushes out an item that is still being rolled for.
    /// </summary>
    public const int HistoryLimit = 16;

    /// <summary>How long an item may be out of the loot list before its roll is taken as over without a winner.</summary>
    private static readonly TimeSpan GoneGrace = TimeSpan.FromSeconds(3);

    private int _nextId;

    // Zone of the last item that dropped: an item from another one starts a new duty.
    private uint _territory;

    /// <summary>The items, the newest first.</summary>
    public List<LootEntry> Entries { get; } = [];

    /// <summary>Every item rolled for in the current duty, in the order the rolls ended.</summary>
    public List<LootAward> Summary { get; } = [];

    /// <summary>Changes with every change of the data, so a window knows when to draw again.</summary>
    public int Version { get; private set; }

    /// <summary>Changes when <see cref="Summary"/> changes.</summary>
    public int SummaryVersion { get; private set; }

    /// <summary>An item was added to the list.</summary>
    public event Action<LootEntry>? Added;

    public LootEntry? Find(int id) => Entries.FirstOrDefault(entry => entry.Id == id);

    /// <summary>The item of a slot of the loot list: the entry already made for it, or a new one.</summary>
    public LootEntry Track(uint itemId, uint chest, uint chestIndex, int count)
    {
        // The same slot, also after its roll is over: the game keeps it in the list for a moment.
        var entry = Entries.FirstOrDefault(e => e.ItemId == itemId && e.Chest == chest && e.ChestIndex == chestIndex && chest != 0);

        // One made from what the players said before the slot was seen.
        entry ??= Entries.FirstOrDefault(e => !e.Resolved && e.ItemId == itemId && e.Chest == 0);
        if (entry is not null)
        {
            if (entry.Chest == 0 && chest != 0) { entry.Chest = chest; entry.ChestIndex = chestIndex; }
            entry.Count = count;
            entry.InList = true;
            return entry;
        }

        return Create(itemId, chest, chestIndex, count, inList: true);
    }

    private LootEntry Create(uint itemId, uint chest, uint chestIndex, int count, bool inList)
    {
        // The first item of another duty: what was kept belongs to the last one.
        var here = territory();
        if (here != _territory)
        {
            _territory = here;
            Entries.RemoveAll(e => e.Territory != here && !e.InList);
            ClearSummary();
        }

        var (name, icon) = itemInfo(itemId);
        var entry = new LootEntry
        {
            Id         = ++_nextId,
            ItemId     = itemId,
            Chest      = chest,
            ChestIndex = chestIndex,
            Count      = count,
            ItemName   = name,
            IconId     = icon,
            Territory  = here,
            InList     = inList,
            Players    = roster(),
        };

        Entries.Insert(0, entry);
        while (Entries.Count > HistoryLimit)
        {
            // The oldest that is over and out of the list; never one the game still lists, nor the one just made.
            var drop = Entries.LastOrDefault(e => e != entry && !e.InList && e.Resolved)
                    ?? Entries.LastOrDefault(e => e != entry && !e.InList);
            if (drop is null) break;
            Entries.Remove(drop);
        }

        Version++;
        Added?.Invoke(entry);
        return entry;
    }

    /// <summary>The game says a player has chosen for an item. Returns why it was not taken, or null when it was.</summary>
    public string? Cast(string name, uint itemId)
    {
        var entry = Entries.Where(e => !e.Resolved && e.ItemId == itemId && e.Players.Any(p => p.Name == name && p.Pending))
                           .OrderBy(e => e.Id).FirstOrDefault();

        if (entry is null)
        {
            // Said before the item was seen in the list: make it now, the list fills in.
            var open = Entries.Where(e => !e.Resolved && e.ItemId == itemId).ToList();
            if (open.Count > 0)
                return open.Any(e => e.Players.Any(p => p.Name == name))
                    ? $"already cast on every open copy ({open.Count})"
                    : $"not in the party of any open copy ({open.Count}); party: {string.Join(", ", open[0].Players.Select(p => p.Name))}";
            entry = Create(itemId, 0, 0, 1, inList: false);
        }

        var player = entry.Players.FirstOrDefault(p => p.Name == name);
        if (player is null) return $"not in the party; party: {string.Join(", ", entry.Players.Select(p => p.Name))}";
        if (player.Cast) return "already cast";

        player.Cast = true;
        Version++;
        return null;
    }

    /// <summary>Whether a roll is open for the item.</summary>
    public bool IsOpen(uint itemId) => Entries.Any(e => !e.Resolved && e.ItemId == itemId);

    /// <summary>Whether any roll is open.</summary>
    public bool AnyOpen => Entries.Any(e => !e.Resolved);

    /// <summary>The game says what a player rolled.</summary>
    public void Roll(string name, uint itemId, LootChoice choice, int value)
    {
        var open = Entries.Where(e => !e.Resolved && e.ItemId == itemId).OrderBy(e => e.Id).ToList();
        if (open.Count == 0) return;

        // Already known: your own roll is read from the loot list before the chat says it. With two equal items,
        // the message must not land on the other one.
        if (open.Any(e => e.Players.Any(p => p.Name == name && p.Choice == choice && p.Value == value))) return;

        var entry = open.FirstOrDefault(e => e.Players.Any(p => p.Name == name && p.Choice == LootChoice.None));
        if (entry is not null)
        {
            var player = entry.Players.First(p => p.Name == name);
            player.Cast   = true;
            player.Choice = choice;
            player.Value  = value;
            Version++;
            return;
        }

        // A member of your party already rolled on every open copy of the item.
        if (open.Any(e => e.Players.Any(p => p.Name == name))) return;

        // A player of another party of the alliance.
        var other = open.FirstOrDefault(e => e.Others.All(p => p.Name != name));
        if (other is null) return;

        other.Others.Add(new LootPlayer { Name = name, Outside = true, Cast = true, Choice = choice, Value = value });
        Version++;
    }

    /// <summary>What the game says about the player that is you, as it happens (the loot list has it before the chat does).</summary>
    public void Mine(LootEntry entry, LootChoice choice, int value)
    {
        var me = entry.Players.FirstOrDefault(p => p.IsMe);
        if (me is null) return;

        var changed = !me.Cast;
        me.Cast = true;
        if (choice != LootChoice.None && (me.Choice != choice || me.Value != value))
        {
            me.Choice = choice;
            me.Value  = value;
            changed   = true;
        }

        if (changed) Version++;
    }

    /// <summary>The game says a player got the item: the roll is over.</summary>
    public void Award(string name, uint itemId)
    {
        var entry = Entries.Where(e => !e.Resolved && e.ItemId == itemId).OrderBy(e => e.Id).FirstOrDefault();
        if (entry is null) return;   // an item taken without a roll

        Resolve(entry, name);
    }

    /// <summary>The item is not in the loot list now.</summary>
    public void Left(LootEntry entry)
    {
        if (entry.Resolved || entry.Gone is not null) return;
        entry.Gone = DateTime.UtcNow;
    }

    /// <summary>The item is in the loot list.</summary>
    public void Present(LootEntry entry) => entry.Gone = null;

    /// <summary>Closes the rolls of the items that left the list without anyone getting them.</summary>
    public void Sweep()
    {
        foreach (var entry in Entries)
        {
            if (!entry.Resolved && entry.Gone is { } gone && DateTime.UtcNow - gone > GoneGrace)
                Resolve(entry, null);
        }
    }

    private void Resolve(LootEntry entry, string? winner)
    {
        foreach (var player in entry.Players)
        {
            // Whoever never rolled, passed: by choice if they cast their lot, by the timer if they did not.
            if (player.Choice == LootChoice.None)
            {
                player.Choice   = LootChoice.Pass;
                player.TimedOut = !player.Cast;
            }
            player.Winner = winner is not null && player.Name == winner;
        }

        foreach (var other in entry.Others)
            other.Winner = winner is not null && other.Name == winner;

        // Someone of another party got it without their roll being seen.
        if (winner is not null && entry.Winner is null)
            entry.Others.Add(new LootPlayer { Name = winner, Outside = true, Cast = true, Winner = true });

        entry.Resolved = true;
        Version++;

        var got = entry.Winner;
        Summary.Add(new LootAward(entry.ItemId, entry.ItemName, entry.IconId, entry.Count, got?.Name,
            got?.IsMe == true, entry.WonOutside, got?.Choice ?? LootChoice.None, got?.Value ?? 0));
        SummaryVersion++;
    }

    /// <summary>Forgets every roll that is over and the summary; items still in the loot list stay.</summary>
    public void Clear()
    {
        Entries.RemoveAll(entry => !entry.InList);
        ClearSummary();
        Version++;
    }

    private void ClearSummary()
    {
        Summary.Clear();
        SummaryVersion++;
    }
}
