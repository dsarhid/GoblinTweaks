using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;
using LuminaAddon = Lumina.Excel.Sheets.Addon;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// A table of what each member of the party did with the items of the loot list (Need, Greed or Pass, and the
/// number of the roll), in a window of its own, with the last rolls kept.
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
    }

    private static readonly string[] HelpTopics = ["Overview", "Table", "Timing", "Settings"];

    private const uint CastLotMessage = 5180;   // "X casts their lot for item"
    private const uint RollMessage    = 1231;   // "X rolls Need on item. 34!"
    private const uint ObtainMessage  = 1232;   // "X obtains item"
    private const int  MaxSlots       = 16;

    private LootRolls?       _rolls;
    private LootRollsAddon?  _addon;
    private LootSettingsAddon? _settingsAddon;
    private TextHelpAddon?   _help;
    private HashSet<string>  _needWords  = [];
    private HashSet<string>  _greedWords = [];
    private long             _tick;
    private int              _gameSelected = -1;
    private bool             _placed;

    internal LootRolls Rolls => _rolls ??= NewRolls();

    internal string Text(string key) => T(key);

    protected internal override void Enable()
    {
        _rolls = NewRolls();
        LoadChoiceWords();
        _addon         = NewAddon();
        _settingsAddon = NewSettingsAddon();
        _gameSelected = -1;
        _placed       = false;

        _rolls.Added += OnAdded;
        Svc.Framework.Update += OnUpdate;
        Svc.Chat.LogMessage  += OnLogMessage;
        Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    /// <summary>The rolls of the last duty are not the rolls of this one.</summary>
    private void OnTerritoryChanged(uint territory)
    {
        try { _rolls?.Clear(); }
        catch (Exception ex) { ReportFailure(ex); }
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Chat.LogMessage  -= OnLogMessage;
        Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;

        if (_rolls is not null) _rolls.Added -= OnAdded;
        _addon?.Dispose();
        _addon = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;
        _help?.Dispose();
        _help  = null;
        _rolls = null;
    }

    public override TweakButton? OpenButton => new(TMain("Open"), TMain("Open.Help"), () => _addon?.Toggle());

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, OpenHelp);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(TMain("Button.Settings"), TMain("Settings.Help"), OpenSettings),
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

    internal void OpenHelp()
    {
        _help ??= NewHelp();
        _help.Toggle();
    }

    private LootSettingsAddon NewSettingsAddon() => new()
    {
        InternalName = "GtkGoblinLootSettings",
        Title        = T("Settings.Title"),
        Size         = new System.Numerics.Vector2(480f, 330f),
        Tweak        = this,
    };

    private TextHelpAddon NewHelp() => new()
    {
        InternalName = "GtkGoblinLootHelp",
        Title        = T("Help.Title"),
        Size         = new System.Numerics.Vector2(700f, 480f),
        Pages        = [.. HelpTopics.Select(topic => (T($"Help.{topic}.Title"), T($"Help.{topic}.Text")))],
    };

    private LootRolls NewRolls() => new(Roster, ItemInfo);

    private LootRollsAddon NewAddon() => new()
    {
        InternalName    = "GtkGoblinLoot",
        Title           = T("Title"),
        Size            = new System.Numerics.Vector2(600f, 420f),
        RespectCloseAll = false,
        Tweak           = this,
    };

    // ── What the game says ────────────────────────────────────────────────────

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            var id = message.LogMessageId;
            if (id is not (CastLotMessage or RollMessage or ObtainMessage)) return;
            if (!message.TryGetIntParameter(0, out var item) || item <= 0) return;

            var name = message.SourceEntity?.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) return;

            var rolls = Rolls;
            switch (id)
            {
                case CastLotMessage:
                    rolls.Cast(name, (uint)item);
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
                if (!entry.Resolved && entry.SeenTick != _tick) _rolls.Left(entry);
            }

            _rolls.Sweep();
            FollowGameSelection(loot);
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

    private void OnAdded(LootEntry entry)
    {
        _addon?.FollowNew(entry);

        if (!Settings.AutoOpen || _addon is null || _addon.IsOpen) return;
        _addon.Open();

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

            var x = (short)(loot->X + loot->GetScaledWidth(true) + 8);
            var y = loot->Y;
            own->SetPosition(x, y);
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
        var plain = itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;
        return sheet is not null && sheet.TryGetRow(plain, out var row)
            ? (row.Name.ExtractText(), row.Icon)
            : ($"#{itemId}", 0u);
    }
}
