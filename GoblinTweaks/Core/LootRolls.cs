namespace GoblinTweaks.Core;

/// <summary>What a player did with an item of the loot list.</summary>
internal enum LootChoice { None, Need, Greed, Pass }

/// <summary>A member of the party in the roll of one item.</summary>
internal sealed class LootPlayer
{
    public required string Name { get; init; }
    public uint JobId { get; init; }
    public bool IsMe { get; init; }

    /// <summary>The player has chosen (the game says so as it happens), even if what they chose is not known yet.</summary>
    public bool Cast { get; set; }

    public LootChoice Choice { get; set; }
    public int Value { get; set; }
    public bool Winner { get; set; }

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
    public List<LootPlayer> Players { get; init; } = [];

    /// <summary>The roll is over: the game said who got the item (or that nobody did).</summary>
    public bool Resolved { get; set; }

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

    public LootPlayer? Winner => Players.FirstOrDefault(player => player.Winner);

    public int Chosen => Players.Count(player => !player.Pending);
}

/// <summary>
/// What each member of the party did in the roll of each item of the loot list, built from what the game says.
/// The game says who has chosen as it happens, but not what: Need, Greed and the numbers arrive together when the
/// roll is over, and a Pass is never said at all (whoever chose and did not roll, passed).
/// </summary>
internal sealed class LootRolls(Func<List<LootPlayer>> roster, Func<uint, (string Name, uint IconId)> itemInfo)
{
    public const int HistoryLimit = 8;

    /// <summary>How long an item may be out of the loot list before its roll is taken as over without a winner.</summary>
    private static readonly TimeSpan GoneGrace = TimeSpan.FromSeconds(3);

    private int _nextId;

    /// <summary>The items, the newest first.</summary>
    public List<LootEntry> Entries { get; } = [];

    /// <summary>Changes with every change of the data, so a window knows when to draw again.</summary>
    public int Version { get; private set; }

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
            return entry;
        }

        return Create(itemId, chest, chestIndex, count);
    }

    private LootEntry Create(uint itemId, uint chest, uint chestIndex, int count)
    {
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
            Players    = roster(),
        };

        Entries.Insert(0, entry);
        while (Entries.Count > HistoryLimit)
        {
            // The oldest that is over; an open roll is only dropped if there is nothing else to drop.
            var drop = Entries.LastOrDefault(e => e.Resolved) ?? Entries[^1];
            Entries.Remove(drop);
        }

        Version++;
        Added?.Invoke(entry);
        return entry;
    }

    /// <summary>The game says a player has chosen for an item.</summary>
    public void Cast(string name, uint itemId)
    {
        var entry = Entries.Where(e => !e.Resolved && e.ItemId == itemId && e.Players.Any(p => p.Name == name && p.Pending))
                           .OrderBy(e => e.Id).FirstOrDefault();

        if (entry is null)
        {
            // Said before the item was seen in the list: make it now, the list fills in.
            if (Entries.Any(e => !e.Resolved && e.ItemId == itemId)) return;
            entry = Create(itemId, 0, 0, 1);
        }

        var player = entry.Players.FirstOrDefault(p => p.Name == name);
        if (player is null || player.Cast) return;

        player.Cast = true;
        Version++;
    }

    /// <summary>The game says what a player rolled.</summary>
    public void Roll(string name, uint itemId, LootChoice choice, int value)
    {
        var entry = Entries.Where(e => !e.Resolved && e.ItemId == itemId && e.Players.Any(p => p.Name == name && p.Choice == LootChoice.None))
                           .OrderBy(e => e.Id).FirstOrDefault();
        if (entry is null) return;

        var player = entry.Players.First(p => p.Name == name);
        player.Cast   = true;
        player.Choice = choice;
        player.Value  = value;
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
            // Whoever chose and never rolled, passed.
            if (player.Choice == LootChoice.None) player.Choice = LootChoice.Pass;
            player.Winner = winner is not null && player.Name == winner;
        }

        entry.Resolved = true;
        Version++;
    }

    /// <summary>Forgets every roll: a new duty starts from nothing.</summary>
    public void Clear()
    {
        Entries.Clear();
        Version++;
    }
}
