using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using GoblinTweaks.UI;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.UiOverlay;
using LuminaAchievement = Lumina.Excel.Sheets.Achievement;

namespace GoblinTweaks.Tweaks;

/// <summary>An achievement of the game, with the texts of its row and where it is listed.</summary>
internal sealed record AchievementInfo(uint Id, string Name, string Description, ushort Points, uint IconId,
    string Kind, string Category, uint CategoryId, string Reward, int Order, bool HasTitle, bool HasItem, uint RewardIconId, uint RewardItemId, bool ItemUnlocks);

/// <summary>What the server said of the progress of an achievement, and when (Unix seconds).</summary>
public sealed class SavedProgress
{
    public uint Current { get; set; }
    public uint Max { get; set; }
    public long Time { get; set; }
}

/// <summary>An achievement the user completed while this tweak was on, and when (Unix seconds).</summary>
public sealed class CompletedEntry
{
    public uint Id { get; set; }
    public long Time { get; set; }
}

/// <summary>How much of the game is done: overall and per category.</summary>
internal sealed record AchievementStats(int Done, int Total, int PointsDone, int PointsTotal,
    IReadOnlyDictionary<uint, (int Done, int Total)> Categories);

/// <summary>How far along an achievement is, as the server said.</summary>
internal readonly record struct AchievementProgress(uint Current, uint Max);

/// <summary>
/// A browser and a tracker for the achievements of the game. The browser is a native window that lists all of them with
/// search and filters (done, missing, almost done, tracked); the tracker is an overlay with the ones you pin and how far
/// along they are. It also tells you in chat when you complete one.
/// </summary>
/// <remarks>
/// The game only knows which achievements you have once it has asked the server, which it does when you open its own
/// Achievement window. This tweak never asks on its own: the window has a button for it, and the progress of the tracked
/// achievements (37/50) is asked for one at a time, only when you pin one or press the refresh button.
/// </remarks>
[Tweak(TweakCategory.Interface)]
public sealed unsafe class GoblinAchievements : Tweak<GoblinAchievements.Options>
{
    public sealed class Options
    {
        /// <summary>Ids of the pinned achievements, in the order they were pinned.</summary>
        public List<uint> Tracked { get; set; } = [];

        public bool TrackerVisible { get; set; } = true;
        public bool HideWhenEmpty { get; set; } = true;
        public bool Locked { get; set; }

        /// <summary>The tracker lists the closest to done first, instead of the order they were pinned.</summary>
        public bool ClosestFirst { get; set; }

        public int Opacity { get; set; } = 62;
        public int FontSize { get; set; } = 14;

        /// <summary>Position of the tracker, in pixels from the centre of the screen to its top-left corner.</summary>
        public int OffsetX { get; set; } = -700;
        public int OffsetY { get; set; } = -150;

        public bool NotifyDone { get; set; } = true;
        public bool NotifyNear { get; set; } = true;

        /// <summary>The percentage of a tracked achievement from which chat says it is almost done.</summary>
        public int NearPercent { get; set; } = 90;

        /// <summary>The groups of the category column that were left open.</summary>
        public List<string> ExpandedGroups { get; set; } = [];

        /// <summary>The last progress the server told of each achievement, so the bars are there when the window opens.</summary>
        public Dictionary<uint, SavedProgress> Progress { get; set; } = [];

        /// <summary>The last achievements completed while the tweak was on, newest first.</summary>
        public List<CompletedEntry> History { get; set; } = [];

        /// <summary>Saved lists of pinned achievements: three slots.</summary>
        public List<List<uint>> Lists { get; set; } = [[], [], []];
    }

    private static readonly string[] HelpTopics = ["Overview", "Browser", "Panel", "Tracker", "Loading", "Notices"];

    private static readonly TimeSpan RequestGap   = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Refresh      = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SaveDelay    = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ProgressFresh = TimeSpan.FromMinutes(10);
    private const int MaxSavedProgress = 400;
    private static readonly TimeSpan OpenLimit    = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OpenSettle   = TimeSpan.FromMilliseconds(1200);

    private readonly Dictionary<uint, AchievementProgress> _progress = [];
    private readonly Queue<uint> _queue = [];
    private readonly HashSet<uint> _asked = [];
    private readonly HashSet<uint> _warned = [];
    private readonly Dictionary<uint, uint> _baseline = [];

    private const int MaxHistory = 100;
    private const int MaxTracked = 50;
    public const int ListSlots = 3;

    private AchievementStats? _stats;
    private int _statsVersion = -1;
    private bool _statsLoaded;
    private int _sessionDone;
    private int _sessionPoints;

    private List<AchievementInfo>? _catalog;
    private OverlayController? _overlay;
    private AchievementTrackerNode? _tracker;
    private AchievementsAddon? _addon;
    private AchievementsSettingsAddon? _settingsAddon;
    private TextHelpAddon? _help;
    private Exception? _failure;
    private uint? _waiting;
    private DateTime _waitUntil = DateTime.MinValue;
    private DateTime _nextRequest = DateTime.MinValue;
    private DateTime _nextRefresh = DateTime.MinValue;
    private DateTime? _saveAt;
    private uint? _openId;
    private DateTime _openUntil;
    private DateTime? _openReadyAt;
    private bool _openShownByUs;
    private ushort _lastHistory;
    private bool _historySeen;
    private Vector2? _placed;
    private bool _dirty = true;

    /// <summary>Options of this tweak, for the native windows. Call <see cref="Changed"/> after editing them.</summary>
    internal Options Config => Settings;

    /// <summary>Localized text of this tweak, for the native windows.</summary>
    internal string Text(string key) => T(key);

    /// <summary>Every achievement of the game, in the order the game lists them. Read from the game data the first time it is asked for.</summary>
    internal IReadOnlyList<AchievementInfo> Catalog => _catalog ??= BuildCatalog();

    /// <summary>Raised each time the tracked achievements or their numbers change.</summary>
    internal int Version { get; private set; }

    // ── Enabling ────────────────────────────────────────────────────────────────

    protected internal override void Enable()
    {
        _failure = null;
        _placed = null;
        _dirty = true;
        _historySeen = false;
        _waiting = null;
        _queue.Clear();
        _asked.Clear();
        _baseline.Clear();
        _sessionDone = 0;
        _sessionPoints = 0;
        _stats = null;
        EnsureLists();

        _progress.Clear();
        foreach (var (id, saved) in Settings.Progress)
            if (saved.Max > 0) _progress[id] = new AchievementProgress(saved.Current, saved.Max);

        _overlay = new OverlayController();
        _tracker = new AchievementTrackerNode();
        _overlay.AddNode(_tracker);

        _addon = NewAddon();
        _settingsAddon = NewSettingsAddon();

        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;

        _addon?.Dispose();
        _addon = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;
        _help?.Dispose();
        _help = null;

        // Disposing the controller frees the nodes it owns.
        _overlay?.Dispose();
        _overlay = null;
        _tracker = null;

        _catalog = null;
        _progress.Clear();
        _queue.Clear();
        _warned.Clear();
        _baseline.Clear();
        _stats = null;
        FlushSettings();
    }

    protected override IReadOnlyList<string> CommandNames => ["/gach", "/goblinachievements"];

    protected override void OnCommand() => _addon?.Toggle();

    public override TweakButton? OpenButton => new(TMain("Open"), TMain("Open.Help"), () => _addon?.Toggle());

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, OpenHelp);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(TMain("Button.Settings"), TMain("Settings.Help"), OpenSettings),
        new(TMain("Button.ResetPosition"), TMain("ResetPosition.Help"), ResetPosition),
    ];

    private AchievementsAddon NewAddon() => new()
    {
        InternalName = "GtkAchievements",
        Title        = T("Title"),
        Size         = new Vector2(1300f, 720f),
        Tweak        = this,
    };

    private AchievementsSettingsAddon NewSettingsAddon() => new()
    {
        InternalName = "GtkAchievementsSettings",
        Title        = T("Settings.Title"),
        Size         = new Vector2(540f, 640f),
        Tweak        = this,
    };

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;

        Rebuild(ref _addon, NewAddon);
        Rebuild(ref _settingsAddon, NewSettingsAddon);
        RebuildHelp();
        _dirty = true;
    }

    internal void OpenSettings()
    {
        if (_settingsAddon is { } window)
        {
            if (window.IsOpen) window.Close(); else window.Open();
        }
    }

    internal void OpenHelp()
    {
        _help ??= NewHelp();
        _help.Toggle();
    }

    private TextHelpAddon NewHelp() => new()
    {
        InternalName = "GtkAchievementsHelp",
        Title        = T("Help.Title"),
        Size         = new Vector2(780f, 560f),
        Pages        = [.. HelpTopics.Select(topic => (T($"Help.{topic}.Title"), T($"Help.{topic}.Text")))],
    };

    private void RebuildHelp()
    {
        var wasOpen = _help is { IsOpen: true };
        _help?.Dispose();
        _help = null;
        if (wasOpen) OpenHelp();
    }

    /// <summary>The options were edited: they apply at once and are saved once the user stops changing them.</summary>
    internal void Changed()
    {
        _dirty = true;
        _saveAt = DateTime.UtcNow + SaveDelay;
    }

    private void ResetPosition()
    {
        var defaults = new Options();
        Settings.OffsetX = defaults.OffsetX;
        Settings.OffsetY = defaults.OffsetY;
        _placed = null;
        Changed();
    }

    private void FlushSettings()
    {
        if (_saveAt is null) return;
        _saveAt = null;
        SaveSettings();
    }

    // ── The data ────────────────────────────────────────────────────────────────

    private List<AchievementInfo> BuildCatalog()
    {
        var list = new List<AchievementInfo>();
        var sheet = Svc.Data.GetExcelSheet<LuminaAchievement>();
        if (sheet is null) return list;

        foreach (var row in sheet)
        {
            var name = row.Name.ExtractText();
            if (name.Length == 0) continue;

            // Rows without a category (and without an icon) are placeholders the game's own window never lists.
            if (row.AchievementCategory.RowId == 0) continue;

            var category = row.AchievementCategory.ValueNullable;
            var kind     = category?.AchievementKind.ValueNullable;
            var reward   = row.Title.ValueNullable is { } title && title.Masculine.ExtractText().Length > 0
                ? title.Masculine.ExtractText()
                : row.Item.ValueNullable is { } item ? item.Name.ExtractText() : string.Empty;

            list.Add(new AchievementInfo(
                row.RowId, name, row.Description.ExtractText(), row.Points, row.Icon,
                kind?.Name.ExtractText() ?? string.Empty,
                category?.Name.ExtractText() ?? string.Empty,
                category?.RowId ?? 0,
                reward,
                row.Order,
                row.Title.ValueNullable is { } t && t.Masculine.ExtractText().Length > 0,
                row.Item.RowId != 0,
                row.Item.ValueNullable is { } rewardItem && row.Item.RowId != 0 ? (uint)rewardItem.Icon : 0u,
                row.Item.RowId,
                row.Item.ValueNullable is { } unlockItem && row.Item.RowId != 0 && unlockItem.ItemAction.RowId != 0));
        }

        // Kind and category in the order of the game's own window, then each achievement in its order.
        return [.. list.OrderBy(a => a.Kind, StringComparer.Ordinal).ThenBy(a => a.CategoryId).ThenBy(a => a.Order).ThenBy(a => a.Id)];
    }

    /// <summary>Whether the game has asked the server for the achievements: until it has, nothing is known of what is done.</summary>
    internal bool IsLoaded
    {
        get
        {
            var achievements = Achievement.Instance();
            return achievements != null && achievements->IsLoaded();
        }
    }

    internal bool NearLoaded
    {
        get
        {
            var achievements = Achievement.Instance();
            return achievements != null
                && achievements->GetNearCompletionAchievementState(Achievement.NearCompletionAchievementSlot.AchievementAddon) == Achievement.AchievementState.Loaded;
        }
    }

    internal bool IsComplete(uint id)
    {
        var achievements = Achievement.Instance();
        return achievements != null && achievements->IsLoaded() && achievements->IsComplete((int)id);
    }

    /// <summary>Whether the game lists it as almost done (its "Near Completion" page).</summary>
    internal bool IsNear(uint id)
    {
        var achievements = Achievement.Instance();
        return achievements != null && NearLoaded
            && achievements->IsNearCompletionAchievement(Achievement.NearCompletionAchievementSlot.AchievementAddon, id);
    }

    internal bool TryGetProgress(uint id, out AchievementProgress progress) => _progress.TryGetValue(id, out progress);

    internal bool IsTracked(uint id) => Settings.Tracked.Contains(id);

    private void EnsureLists()
    {
        while (Settings.Lists.Count < ListSlots) Settings.Lists.Add([]);
    }

    internal int SessionDone => _sessionDone;
    internal int SessionPoints => _sessionPoints;

    /// <summary>Completed so far, overall and by category. Computed again only when something changed.</summary>
    internal AchievementStats Stats
    {
        get
        {
            var loaded = IsLoaded;
            if (_stats is not null && _statsVersion == Version && _statsLoaded == loaded) return _stats;

            var categories = new Dictionary<uint, (int Done, int Total)>();
            int done = 0, points = 0, pointsDone = 0;
            foreach (var info in Catalog)
            {
                var complete = loaded && IsComplete(info.Id);
                points += info.Points;
                if (complete) { done++; pointsDone += info.Points; }

                categories.TryGetValue(info.CategoryId, out var entry);
                categories[info.CategoryId] = (entry.Done + (complete ? 1 : 0), entry.Total + 1);
            }

            _statsVersion = Version;
            _statsLoaded = loaded;
            return _stats = new AchievementStats(done, Catalog.Count, pointsDone, points, categories);
        }
    }

    /// <summary>Achievements completed while the tweak was on, newest first.</summary>
    internal IReadOnlyList<CompletedEntry> History => Settings.History;

    internal DateTime? CompletedAt(uint id)
    {
        var entry = Settings.History.FirstOrDefault(e => e.Id == id);
        return entry is null || entry.Time == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(entry.Time).LocalDateTime;
    }

    /// <summary>0 to 1 for how close to done it is, or -1 when nothing is known of it.</summary>
    internal float Closeness(uint id)
    {
        if (TryGetProgress(id, out var progress) && progress.Max > 0) return (float)progress.Current / progress.Max;
        return IsNear(id) ? 0.5f : -1f;
    }

    /// <summary>
    /// Opens the game's own Achievement window on this achievement. Asking the agent to open one while its window was not
    /// there crashed the game (a null read in the window's refresh), so this only asks once the window is shown and the
    /// game has loaded the achievements: it opens the window first when it is not, waits for it, and then asks.
    /// </summary>
    internal void OpenInGame(uint id)
    {
        try
        {
            var agent = AgentAchievement.Instance();
            if (agent == null) return;

            _openShownByUs = !agent->IsAddonShown();
            if (_openShownByUs) agent->Show();

            _openId      = id;
            _openReadyAt = null;
            _openUntil   = DateTime.UtcNow + OpenLimit;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Could not open the achievement window");
            Svc.Chat.Print(T("Chat.OpenFailed"), Name);
        }
    }

    /// <summary>The second half of <see cref="OpenInGame"/>: asks for the achievement once the window and the data are ready.</summary>
    private void ServeOpen(DateTime now)
    {
        if (_openId is not { } id) return;

        var agent = AgentAchievement.Instance();
        if (agent == null || now > _openUntil)
        {
            _openId = null;
            if (agent != null) Svc.Chat.Print(T("Chat.OpenTimeout"), Name);
            return;
        }

        if (!agent->IsAddonShown() || !IsLoaded)
        {
            _openReadyAt = null;
            return;
        }

        // A window that has just opened gets a moment to finish its first refresh.
        _openReadyAt ??= now + (_openShownByUs ? OpenSettle : TimeSpan.Zero);
        if (now < _openReadyAt) return;

        _openId = null;
        try
        {
            agent->OpenById(id);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "Could not open the achievement");
            Svc.Chat.Print(T("Chat.OpenFailed"), Name);
        }
    }

    /// <summary>Pins up to <paramref name="count"/> of the closest to done that are not pinned yet.</summary>
    internal void TrackClosest(int count)
    {
        if (!IsLoaded || !NearLoaded)
        {
            Svc.Chat.Print(T("Chat.NeedLoad"), Name);
            return;
        }

        var pick = Catalog
            .Where(a => !IsComplete(a.Id) && !IsTracked(a.Id) && Closeness(a.Id) >= 0f)
            .OrderByDescending(a => Closeness(a.Id))
            .Take(Math.Max(0, Math.Min(count, MaxTracked - Settings.Tracked.Count)))
            .ToList();

        foreach (var info in pick)
        {
            Settings.Tracked.Add(info.Id);
            Enqueue(info.Id);
        }

        Version++;
        Changed();
        Svc.Chat.Print(string.Format(T("Chat.Pinned"), pick.Count), Name);
    }

    internal void ClearTracked()
    {
        Settings.Tracked.Clear();
        _warned.Clear();
        Version++;
        Changed();
    }

    internal void CopyList()
    {
        var ok = ClipboardHelper.SetText(string.Join(",", Settings.Tracked));
        Svc.Chat.Print(string.Format(T(ok ? "Chat.Copied" : "Chat.CopyFailed"), Settings.Tracked.Count), Name);
    }

    /// <summary>Adds the ids on the clipboard (numbers separated by anything) that exist and are not pinned yet.</summary>
    internal void PasteList()
    {
        var valid = Catalog.Select(a => a.Id).ToHashSet();
        var added = 0;
        foreach (var part in ClipboardHelper.GetText().Split([',', ' ', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Settings.Tracked.Count >= MaxTracked) break;
            if (!uint.TryParse(part, out var id) || !valid.Contains(id) || Settings.Tracked.Contains(id)) continue;

            Settings.Tracked.Add(id);
            Enqueue(id);
            added++;
        }

        Version++;
        Changed();
        Svc.Chat.Print(string.Format(T("Chat.Pasted"), added), Name);
    }

    internal int ListSize(int slot) => slot >= 0 && slot < Settings.Lists.Count ? Settings.Lists[slot].Count : 0;

    internal void SaveList(int slot)
    {
        EnsureLists();
        Settings.Lists[slot] = [.. Settings.Tracked];
        Version++;
        Changed();
    }

    internal void LoadList(int slot)
    {
        EnsureLists();
        Settings.Tracked = [.. Settings.Lists[slot].Take(MaxTracked)];
        _warned.Clear();
        foreach (var id in Settings.Tracked) Enqueue(id);
        Version++;
        Changed();
    }

    /// <summary>Asks the server for what is done and what is almost done. It is the only thing that does so on its own, and only when the user presses its button.</summary>
    internal void LoadFromGame()
    {
        var achievements = Achievement.Instance();
        if (achievements == null) return;

        achievements->RequestCompletedAchievements();
        achievements->RequestAllNearCompletionAchievements();
        Version++;
    }

    /// <summary>Pins an achievement to the tracker, or takes it off. Pinning one asks for its progress.</summary>
    internal void ToggleTracked(uint id)
    {
        if (!Settings.Tracked.Remove(id))
        {
            Settings.Tracked.Add(id);
            Enqueue(id);
        }
        else
        {
            _warned.Remove(id);
        }

        Version++;
        Changed();
    }

    /// <summary>
    /// Asks for the progress of one achievement the user selected in the window, once: it joins the same queue as the
    /// tracked ones (one request at a time, a couple of seconds apart) and the answer is kept.
    /// </summary>
    internal void RequestProgress(uint id)
    {
        if (!IsLoaded || IsComplete(id)) return;

        // What was saved from an earlier session counts for a while; after that it is asked again.
        if (_progress.ContainsKey(id) && ProgressAge(id) is { } age && age < ProgressFresh) return;

        _asked.Add(id);
        Enqueue(id);
    }

    /// <summary>
    /// Where the reward stands: none, or waiting for the achievement, or the achievement is done. Whether the reward was
    /// claimed is not told by the game, and looking in the bags is wrong for what sits in the armoire or the dresser.
    /// </summary>
    internal AchievementReward RewardOf(AchievementInfo info, bool done)
    {
        if (!info.HasTitle && !info.HasItem) return AchievementReward.None;
        return done ? AchievementReward.Done : AchievementReward.Pending;
    }

    /// <summary>How old the progress of an achievement is, from when the server told it, or null when it was never asked.</summary>
    internal TimeSpan? ProgressAge(uint id)
    {
        if (!Settings.Progress.TryGetValue(id, out var saved) || saved.Time == 0) return null;
        return DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(saved.Time);
    }

    /// <summary>Asks for the progress of every tracked achievement again, one at a time.</summary>
    internal void RefreshProgress()
    {
        foreach (var id in Settings.Tracked) Enqueue(id);
    }

    internal int QueuedProgress => _queue.Count + (_waiting is null ? 0 : 1);

    private void Enqueue(uint id)
    {
        if (!_queue.Contains(id) && _waiting != id)
            _queue.Enqueue(id);
    }

    // ── Frame by frame ──────────────────────────────────────────────────────────

    private void OnUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        if (_failure is { } failure)
        {
            _failure = null;
            ReportFailure(failure);
            return;
        }

        try
        {
            if (_saveAt is { } saveAt && DateTime.UtcNow >= saveAt)
                FlushSettings();

            if (_tracker is not { } node) return;

            var now = DateTime.UtcNow;
            ServeProgress(now);
            ServeOpen(now);
            WatchCompletions();
            PlaceTracker(node);

            if (!_dirty && now < _nextRefresh) return;

            _dirty = false;
            _nextRefresh = now + Refresh;
            ShowTracker(node);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>One request at a time, a couple of seconds apart: it reads the answer, then asks for the next.</summary>
    private void ServeProgress(DateTime now)
    {
        var achievements = Achievement.Instance();
        if (achievements == null) return;

        if (_waiting is { } id)
        {
            if (achievements->ProgressRequestState == Achievement.AchievementState.Loaded && achievements->ProgressAchievementId == id)
            {
                _progress[id] = new AchievementProgress(achievements->ProgressCurrent, achievements->ProgressMax);
                _baseline.TryAdd(id, achievements->ProgressCurrent);
                SaveProgress(id, achievements->ProgressCurrent, achievements->ProgressMax);
                _waiting = null;
                Version++;
                _dirty = true;
                WarnIfNear(id);
            }
            else if (now >= _waitUntil)
            {
                _waiting = null;
            }
            return;
        }

        if (_queue.Count == 0 || now < _nextRequest || !achievements->IsLoaded()) return;

        var next = _queue.Dequeue();
        if (!Settings.Tracked.Contains(next) && !_asked.Contains(next)) return;

        achievements->RequestAchievementProgress(next);
        _waiting = next;
        _waitUntil = now + RequestLimit;
        _nextRequest = now + RequestGap;
    }

    /// <summary>Keeps what the server said, with the time, for the next session. The oldest are dropped past a limit.</summary>
    private void SaveProgress(uint id, uint current, uint max)
    {
        Settings.Progress[id] = new SavedProgress { Current = current, Max = max, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };

        if (Settings.Progress.Count > MaxSavedProgress)
        {
            foreach (var old in Settings.Progress.OrderBy(pair => pair.Value.Time).Take(Settings.Progress.Count - MaxSavedProgress).Select(pair => pair.Key).ToList())
                Settings.Progress.Remove(old);
        }

        Changed();
    }

    /// <summary>Chat tells you once when a tracked achievement gets close to done.</summary>
    private void WarnIfNear(uint id)
    {
        if (!Settings.NotifyNear || !_progress.TryGetValue(id, out var progress) || progress.Max == 0) return;
        if (progress.Current >= progress.Max || progress.Current * 100d / progress.Max < Settings.NearPercent) return;
        if (!_warned.Add(id)) return;

        var info = Catalog.FirstOrDefault(a => a.Id == id);
        if (info is null) return;

        Svc.Chat.Print(string.Format(T("Chat.Near"), info.Name, progress.Current, progress.Max), Name);
    }

    /// <summary>The game keeps the last five achievements completed; a new one at the front is one just completed.</summary>
    /// <summary>
    /// The game keeps its own last five completed. They go to the end of the recent list (without a date) the first time
    /// they are read, so the list is not empty before the tweak has seen anything complete.
    /// </summary>
    private void SeedHistory(Achievement* achievements)
    {
        var added = false;
        for (var i = 0; i < 5; i++)
        {
            var id = achievements->History[i];
            if (id == 0 || Settings.History.Any(e => e.Id == id)) continue;

            Settings.History.Add(new CompletedEntry { Id = id, Time = 0 });
            added = true;
        }

        if (!added) return;
        Version++;
        Changed();
    }

    private void WatchCompletions()
    {
        var achievements = Achievement.Instance();
        if (achievements == null || !achievements->IsLoaded()) return;

        var latest = achievements->History[0];
        if (!_historySeen)
        {
            _historySeen = true;
            _lastHistory = latest;
            SeedHistory(achievements);
            return;
        }

        if (latest == _lastHistory) return;
        _lastHistory = latest;
        if (latest == 0) return;

        var info = Catalog.FirstOrDefault(a => a.Id == latest);

        Settings.Tracked.Remove(latest);
        Settings.Progress.Remove(latest);
        _progress.Remove(latest);
        _warned.Remove(latest);
        Settings.History.RemoveAll(e => e.Id == latest);
        Settings.History.Insert(0, new CompletedEntry { Id = latest, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        if (Settings.History.Count > MaxHistory) Settings.History.RemoveRange(MaxHistory, Settings.History.Count - MaxHistory);

        _sessionDone++;
        _sessionPoints += info?.Points ?? 0;
        Version++;
        Changed();

        if (!Settings.NotifyDone || info is null) return;

        Svc.Chat.Print(string.Format(T("Chat.Done"), info.Name, info.Points), Name);
        Svc.Chat.Print(string.Format(T("Chat.Session"), _sessionDone, _sessionPoints), Name);
    }

    // ── The tracker ─────────────────────────────────────────────────────────────

    /// <summary>Puts the tracker where it was left, and takes its new place when the user has dragged it.</summary>
    private void PlaceTracker(AchievementTrackerNode node)
    {
        var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (device == null) return;

        var anchor = new Vector2(device->Width / 2f, device->Height / 2f);
        node.Movable  = !Settings.Locked;
        node.Opacity  = Settings.Opacity / 100f;
        node.FontSize = Settings.FontSize;
        node.Title    = T("Tracker.Title");

        if (_placed is { } placed && node.Position != placed)
        {
            var point = node.Position - anchor;
            Settings.OffsetX = (int)MathF.Round(point.X);
            Settings.OffsetY = (int)MathF.Round(point.Y);
            _saveAt = DateTime.UtcNow + SaveDelay;
        }

        node.Position = anchor + new Vector2(Settings.OffsetX, Settings.OffsetY);
        _placed = node.Position;
    }

    private void ShowTracker(AchievementTrackerNode node)
    {
        var rows = new List<TrackerRow>();
        IEnumerable<uint> order = Settings.Tracked;
        if (Settings.ClosestFirst)
            order = order.OrderByDescending(id => IsComplete(id) ? 2f : Closeness(id));

        foreach (var id in order.Take(AchievementTrackerNode.MaxRows))
        {
            var info = Catalog.FirstOrDefault(a => a.Id == id);
            if (info is null) continue;

            var done = IsComplete(id);
            string progress;
            float fraction;
            var near = false;
            if (done)
            {
                progress = T("Status.Done");
                fraction = 1f;
            }
            else if (_progress.TryGetValue(id, out var value) && value.Max > 0)
            {
                progress = $"{value.Current:N0} / {value.Max:N0}";
                fraction = (float)value.Current / value.Max;
                near     = fraction * 100f >= Settings.NearPercent;
                if (_baseline.TryGetValue(id, out var start) && value.Current > start)
                    progress += $" (+{value.Current - start:N0})";
            }
            else
            {
                progress = "?";
                fraction = 0f;
            }

            rows.Add(new TrackerRow(info.IconId, info.Name, progress, fraction, done, near));
        }

        node.Shown = Settings.TrackerVisible && (rows.Count > 0 || !Settings.HideWhenEmpty);
        node.SetRows(rows);
    }
}
