using System.Reflection;
using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;
using LuminaAddon = Lumina.Excel.Sheets.Addon;
using LuminaClassJobCategory = Lumina.Excel.Sheets.ClassJobCategory;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// A table of what each member of the party did with the items of the loot list (Need, Greed or Pass, and the
/// number of the roll), in a window of its own, with the last rolls kept and a summary of the duty.
/// </summary>
/// <remarks>
/// The game says who has chosen as it happens (log message 5180) but not what; Need, Greed and the numbers come
/// together when the roll is over (1231, one for each player that rolled), then who got the item (1232). A Pass is
/// never said. The log messages are used and not the chat text, so it works in every language of the game.
/// </remarks>
[Tweak(TweakCategory.Interface)]
public sealed unsafe class GoblinLoot : Tweak<GoblinLoot.Options>
{
    public sealed class Options
    {
        /// <summary>Open the window when an item is added to the loot list.</summary>
        public bool AutoOpen { get; set; } = true;

        /// <summary>The table shows the item selected in the loot window of the game.</summary>
        public bool FollowSelection { get; set; } = true;

        /// <summary>Close the window a moment after every roll is over, when it opened by itself.</summary>
        public bool AutoClose { get; set; }
    }

    private static readonly string[] HelpTopics = ["Overview", "Table", "Timing", "Settings"];

    private const uint CastLotMessage = 5180;   // "X casts their lot for item"
    private const uint RollMessage    = 1231;   // "X rolls Need on item. 34!"
    private const uint ObtainMessage  = 1232;   // "X obtains item"

    // Kinds of message from the party already written to the log while the rolls are open (see LogUnknown).
    private readonly HashSet<uint> _unknownLogged = [];
    private const int  MaxSlots       = 16;

    private static readonly TimeSpan AutoCloseDelay = TimeSpan.FromSeconds(10);

    // The job columns of ClassJobCategory, in ClassJob order (ADV, GLA, PGL...): column n is the job of row n.
    private static readonly PropertyInfo[] JobColumns =
        [.. typeof(LuminaClassJobCategory).GetProperties().Where(property => property.PropertyType == typeof(bool))];

    private LootRolls?         _rolls;
    private LootRollsAddon?    _addon;
    private LootSettingsAddon? _settingsAddon;
    private LootSummaryAddon?  _summaryAddon;
    private TextHelpAddon?     _help;
    private HashSet<string>    _needWords  = [];
    private HashSet<string>    _greedWords = [];
    private readonly Dictionary<(uint Item, uint Job), bool> _forJob = [];
    private long               _tick;
    private int                _gameSelected = -1;
    private bool               _placed;
    private bool               _openedByLoot;
    private bool               _wasOpen;
    private DateTime?          _closeAt;

    internal LootRolls Rolls => _rolls ??= NewRolls();

    internal string Text(string key) => T(key);

    protected internal override void Enable()
    {
        _rolls = NewRolls();
        LoadChoiceWords();
        _addon         = NewAddon();
        _settingsAddon = NewSettingsAddon();
        _summaryAddon  = NewSummaryAddon();
        _gameSelected = -1;
        _placed       = false;
        _openedByLoot = false;
        _wasOpen      = false;
        _closeAt      = null;

        _rolls.Added += OnAdded;
        Svc.Framework.Update += OnUpdate;
        Svc.Chat.LogMessage  += OnLogMessage;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Chat.LogMessage  -= OnLogMessage;

        if (_rolls is not null) _rolls.Added -= OnAdded;
        _addon?.Dispose();
        _addon = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;
        _summaryAddon?.Dispose();
        _summaryAddon = null;
        _help?.Dispose();
        _help  = null;
        _rolls = null;
        _forJob.Clear();
    }

    public override TweakButton? OpenButton => new(TMain("Open"), TMain("Open.Help"), () => _addon?.Toggle());

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, OpenHelp);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(Loc.Get("Window.Settings"), TMain("Settings.Help"), OpenSettings),
    ];

    protected override IReadOnlyList<string> CommandNames => ["/gloot", "/gloottable"];

    protected override void OnCommand() => _addon?.Toggle();

    internal Options Config => Settings;

    /// <summary>An option was changed in the settings window.</summary>
    internal void Changed() => SaveSettings();

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;

        Rebuild(ref _addon, NewAddon);
        Rebuild(ref _settingsAddon, NewSettingsAddon);
        Rebuild(ref _summaryAddon, NewSummaryAddon);

        var helpWasOpen = _help is { IsOpen: true };
        _help?.Dispose();
        _help = null;
        if (helpWasOpen) OpenHelp();
    }

    internal void OpenSettings()
    {
        if (_settingsAddon is { } window)
        {
            if (window.IsOpen) window.Close(); else window.Open();
        }
    }

    internal void OpenSummary()
    {
        if (_summaryAddon is { } window)
        {
            if (window.IsOpen) window.Close(); else window.Open();
        }
    }

    internal void OpenHelp()
    {
        _help ??= NewHelp();
        _help.Toggle();
    }

    /// <summary>Forgets the rolls that are over and the summary of the duty.</summary>
    internal void ClearHistory() => _rolls?.Clear();

    private LootSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkGoblinLootSettings",
        Title           = $"{T("Title")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size            = new System.Numerics.Vector2(480f, 400f),
        RespectCloseAll = false,
        Tweak           = this,
    };

    private LootSummaryAddon NewSummaryAddon() => new()
    {
        InternalName    = "GtkGoblinLootSummary",
        Title           = T("Summary.Title"),
        Size            = new System.Numerics.Vector2(460f, 480f),
        RespectCloseAll = false,
        Tweak           = this,
    };

    private TextHelpAddon NewHelp() => new()
    {
        InternalName = "GtkGoblinLootHelp",
        Title        = T("Help.Title"),
        Size         = new System.Numerics.Vector2(700f, 480f),
        Pages        = [.. HelpTopics.Select(topic => (T($"Help.{topic}.Title"), T($"Help.{topic}.Text")))],
    };

    private LootRolls NewRolls() => new(Roster, ItemInfo, () => Svc.ClientState.TerritoryType);

    private LootRollsAddon NewAddon() => new()
    {
        InternalName    = "GtkGoblinLoot",
        Title           = T("Title"),
        Size            = new System.Numerics.Vector2(620f, 470f),
        RespectCloseAll = false,
        Tweak           = this,
    };

    // ── What the game says ────────────────────────────────────────────────────

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            var id = message.LogMessageId;
            if (id is not (CastLotMessage or RollMessage or ObtainMessage))
            {
                LogUnknown(message);
                return;
            }

            if (!message.TryGetIntParameter(0, out var item) || item <= 0) return;

            var name = message.SourceEntity?.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
            {
                Svc.Log.Info($"GoblinLoot: message {id} for item {item} came with no player ({Describe(message)}).");
                return;
            }

            var rolls = Rolls;
            switch (id)
            {
                case CastLotMessage:
                    if (rolls.Cast(name, (uint)item) is { } why)
                        Svc.Log.Info($"GoblinLoot: \"{name}\" cast their lot for item {item}, not taken: {why}.");
                    break;

                case RollMessage:
                    if (!message.TryGetStringParameter(1, out var kind) || !message.TryGetIntParameter(2, out var value)) return;
                    var choice = ChoiceOf(kind.ExtractText());
                    if (choice != LootChoice.None) rolls.Roll(name, (uint)item, choice, value);
                    break;

                case ObtainMessage:
                    rolls.Award(name, (uint)item);
                    break;
            }
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    /// <summary>
    /// While a roll is open, a message of another kind about one of its items, or from someone of the party, is written
    /// to the log: a choice the game tells in some other way would be missed, and this shows which one it is.
    /// </summary>
    private void LogUnknown(ILogMessage message)
    {
        if (_rolls is not { AnyOpen: true } rolls)
        {
            _unknownLogged.Clear();
            return;
        }

        var about = message.TryGetIntParameter(0, out var item) && item > 0 && rolls.IsOpen((uint)item);
        var name  = message.SourceEntity?.Name.ExtractText();
        var party = !string.IsNullOrEmpty(name) && rolls.Entries.Any(e => !e.Resolved && e.Players.Any(p => p.Name == name));

        // What the party does in a fight is said in messages too: of those, each kind once while the rolls are open.
        if (!about && (!party || !_unknownLogged.Add(message.LogMessageId))) return;

        Svc.Log.Info($"GoblinLoot: message {message.LogMessageId} during a roll ({Describe(message)}).");
    }

    /// <summary>Who a message is from and its parameters, for the log.</summary>
    private static string Describe(ILogMessage message)
    {
        var parts = new List<string> { $"from \"{message.SourceEntity?.Name.ExtractText()}\"" };
        for (var i = 0; i < message.ParameterCount; i++)
        {
            if (message.TryGetIntParameter(i, out var number)) parts.Add($"#{i}={number}");
            else if (message.TryGetStringParameter(i, out var text)) parts.Add($"#{i}=\"{text.ExtractText()}\"");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The roll message names what was rolled in the language of the game: it is told by the words of the game's own buttons.</summary>
    private LootChoice ChoiceOf(string word)
    {
        if (_needWords.Contains(word))  return LootChoice.Need;
        if (_greedWords.Contains(word)) return LootChoice.Greed;
        return LootChoice.None;
    }

    private void LoadChoiceWords()
    {
        _needWords  = ["Need"];
        _greedWords = ["Greed"];

        var english = Svc.Data.GetExcelSheet<LuminaAddon>(Dalamud.Game.ClientLanguage.English);
        var client  = Svc.Data.GetExcelSheet<LuminaAddon>();
        if (english is null || client is null) return;

        foreach (var row in english)
        {
            var text = row.Text.ExtractText();
            if (text is not ("Need" or "Greed")) continue;
            if (!client.TryGetRow(row.RowId, out var local)) continue;

            var word = local.Text.ExtractText();
            if (word.Length > 0) (text == "Need" ? _needWords : _greedWords).Add(word);
        }
    }

    // ── The loot list ─────────────────────────────────────────────────────────

    private void OnUpdate(IFramework _)
    {
        try
        {
            var loot = Loot.Instance();
            if (loot == null || _rolls is null) return;

            _tick++;
            var items = loot->Items;
            for (var i = 0; i < MaxSlots && i < items.Length; i++)
            {
                ref readonly var item = ref items[i];
                if (item.ItemId == 0) continue;

                var entry = _rolls.Track(item.ItemId, item.ChestObjectId, item.ChestItemIndex, item.ItemCount);
                entry.SeenTick = _tick;
                if (!entry.Resolved) entry.SetTime(item.Time, item.MaxTime);
                _rolls.Present(entry);

                if (item.RollState == RollState.Rolled)
                {
                    // What you chose is in the list as you choose it, before the chat says it.
                    var choice = item.RollResult switch
                    {
                        RollResult.Needed  => LootChoice.Need,
                        RollResult.Greeded => LootChoice.Greed,
                        RollResult.Passed  => LootChoice.Pass,
                        _                  => LootChoice.None,
                    };
                    _rolls.Mine(entry, choice, choice is LootChoice.Need or LootChoice.Greed ? item.RollValue : 0);
                }
            }

            foreach (var entry in _rolls.Entries)
            {
                entry.InList = entry.SeenTick == _tick;
                if (!entry.Resolved && !entry.InList) _rolls.Left(entry);
            }

            _rolls.Sweep();
            FollowGameSelection(loot);
            AutoCloseWhenOver();
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    /// <summary>The item selected in the loot window of the game is the one the table shows.</summary>
    private void FollowGameSelection(Loot* loot)
    {
        var agent = AgentLoot.Instance();
        if (!Settings.FollowSelection || agent == null || _rolls is null || _addon is null) return;

        var selected = agent->SelectedSlotIndex;
        if (selected == _gameSelected) return;
        _gameSelected = selected;

        if (selected < 0 || selected >= MaxSlots) return;
        var item = loot->Items[selected];
        if (item.ItemId == 0) return;

        var entry = _rolls.Entries.FirstOrDefault(e => e.ItemId == item.ItemId && e.Chest == item.ChestObjectId && e.ChestIndex == item.ChestItemIndex);
        if (entry is not null) _addon.Select(entry.Id);
    }

    /// <summary>With the option on, the window that opened by itself closes a moment after the last roll is over.</summary>
    private void AutoCloseWhenOver()
    {
        if (_addon is not { IsOpen: true } addon || _rolls is null)
        {
            // Closed after being open (by the player or by this): the next time it opens on its own again.
            // While it is still opening, the flag stays.
            if (_wasOpen) _openedByLoot = false;
            _wasOpen = false;
            _closeAt = null;
            return;
        }
        _wasOpen = true;

        if (!Settings.AutoClose || !_openedByLoot || _rolls.Entries.Any(e => !e.Resolved || e.InList))
        {
            _closeAt = null;
            return;
        }

        _closeAt ??= DateTime.UtcNow + AutoCloseDelay;
        if (DateTime.UtcNow < _closeAt) return;

        addon.Close();
        _openedByLoot = false;
        _closeAt      = null;
    }

    private void OnAdded(LootEntry entry)
    {
        _addon?.FollowNew(entry);

        if (!Settings.AutoOpen || _addon is null || _addon.IsOpen) return;
        _addon.Open();
        _openedByLoot = true;

        // Next to the loot window of the game, the first time: after that, where the player left it.
        if (!_placed) Svc.Framework.RunOnTick(PlaceNextToLoot, delayTicks: 3);
    }

    private void PlaceNextToLoot()
    {
        try
        {
            var own  = (AtkUnitBase*)Svc.GameGui.GetAddonByName("GtkGoblinLoot").Address;
            var loot = (AtkUnitBase*)Svc.GameGui.GetAddonByName("NeedGreed").Address;
            if (own == null || loot == null || !loot->IsVisible) return;

            // To the right of the loot window, or to its left when there is no room; always on the screen.
            var screen = AtkStage.Instance()->ScreenSize;
            var width  = own->GetScaledWidth(true);
            var height = own->GetScaledHeight(true);

            var x = loot->X + loot->GetScaledWidth(true) + 8f;
            if (x + width > screen.Width) x = loot->X - width - 8f;
            x = Math.Clamp(x, 0f, Math.Max(0f, screen.Width - width));
            var y = Math.Clamp((float)loot->Y, 0f, Math.Max(0f, screen.Height - height));

            own->SetPosition((short)x, (short)y);
            _placed = true;
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    // ── The party and the items ───────────────────────────────────────────────

    /// <summary>Everyone in the party that is a player (the Trust NPCs have no content id), and always you.</summary>
    private List<LootPlayer> Roster()
    {
        var me      = Svc.PlayerState.CharacterName;
        var players = new List<LootPlayer>();

        foreach (var member in Svc.Party)
        {
            var name = member.Name.TextValue;
            if (name.Length == 0 || (member.ContentId == 0 && name != me)) continue;

            players.Add(new LootPlayer { Name = name, JobId = member.ClassJob.RowId, IsMe = name == me });
        }

        if (players.All(player => !player.IsMe) && me.Length > 0)
            players.Insert(0, new LootPlayer { Name = me, JobId = Svc.PlayerState.ClassJob.RowId, IsMe = true });

        return players;
    }

    private static (string Name, uint IconId) ItemInfo(uint itemId)
    {
        var sheet = Svc.Data.GetExcelSheet<LuminaItem>();
        return sheet is not null && sheet.TryGetRow(PlainItemId(itemId), out var row)
            ? (row.Name.ExtractText(), row.Icon)
            : ($"#{itemId}", 0u);
    }

    /// <summary>The item id without the high-quality mark.</summary>
    internal static uint PlainItemId(uint itemId) => itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    /// <summary>Whether the job you are on now can wear the item (only gear has a list of jobs).</summary>
    internal bool ForMyJob(uint itemId)
    {
        var job = Svc.PlayerState.IsLoaded ? Svc.PlayerState.ClassJob.RowId : 0;
        if (job == 0) return false;

        var key = (PlainItemId(itemId), job);
        if (_forJob.TryGetValue(key, out var known)) return known;

        var result = false;
        var sheet  = Svc.Data.GetExcelSheet<LuminaItem>();
        if (sheet is not null && sheet.TryGetRow(key.Item1, out var row)
            && row.ClassJobCategory.RowId != 0 && row.ClassJobCategory.IsValid && job < JobColumns.Length)
        {
            result = JobColumns[job].GetValue(row.ClassJobCategory.Value) is true;
        }

        _forJob[key] = result;
        return result;
    }
}
