using System.Globalization;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using GoblinTweaks.UI;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.UiOverlay;
using Lumina.Excel;
using LuminaClassJob = Lumina.Excel.Sheets.ClassJob;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// A damage and healing table of the fight in progress, for trials, raids and alliance raids, in a panel on the
/// screen: one bar per player of the duty, with the job, the total, the per-second rate and the share of the group.
/// </summary>
/// <remarks>
/// It reads the effects of every action the game resolves (the same game function the battle text reads, from
/// FFXIVClientStructs) and every tick of damage or healing over time. The table belongs to the fight, not to the
/// phase: it is kept when the fight ends, and a new one starts only when the next fight does.
/// A tick does not say whose it is, except for what ticks from the ground: it is shared out among the statuses
/// that cause it, so it is exact with one player's damage over time on an enemy, and an estimate with several.
/// </remarks>
[Tweak(TweakCategory.Interface)]
public sealed unsafe class GoblinDamageDone : Tweak<GoblinDamageDone.Options>
{
    public enum Meter { Damage, Heal, Taken }

    private enum SideView { None, History, Detail, Breakdown }

    public sealed class Options
    {
        /// <summary>Position of the panel, in pixels from the centre of the screen to its top-left corner.</summary>
        public int OffsetX { get; set; } = 330;
        public int OffsetY { get; set; } = -230;

        /// <summary>How many players the panel lists at most.</summary>
        public int Rows { get; set; } = 8;

        public Meter Mode { get; set; }

        public bool Visible { get; set; } = true;

        /// <summary>A locked table cannot be dragged.</summary>
        public bool Locked { get; set; }

        /// <summary>Opacity of the backgrounds, in percent.</summary>
        public int Opacity { get; set; } = 62;

        /// <summary>Size of the letters, in points.</summary>
        public int FontSize { get; set; } = 14;

        /// <summary>Size of the whole table, in percent.</summary>
        public int Scale { get; set; } = 100;

        /// <summary>In an alliance raid, write the group (A, B, C) before each name.</summary>
        public bool ShowGroup { get; set; } = true;

        /// <summary>In an alliance raid, list only the players of your own group.</summary>
        public bool OnlyMyGroup { get; set; }

        /// <summary>The extra table is free of the damage table and has its own place on screen.</summary>
        public bool ExtraDetached { get; set; }

        /// <summary>Position of the free extra table, in pixels from the centre of the screen to its top-left corner.</summary>
        public int ExtraX { get; set; }
        public int ExtraY { get; set; }

        /// <summary>Count damage and healing over time, shared out among the statuses that cause it.</summary>
        public bool IncludeOverTime { get; set; } = true;

        /// <summary>Count what a heal wasted on a target that was already full. Off: healing is what was really restored.</summary>
        public bool IncludeOverheal { get; set; }

        /// <summary>Clicking a player of the table shows their damage by action at the side.</summary>
        public bool BreakdownOnClick { get; set; } = true;

        /// <summary>The table only shows in combat or in a duty (and while a fight is going on).</summary>
        public bool OnlyWhenRelevant { get; set; }
    }

    private sealed class Stat
    {
        public required string Name { get; init; }
        public bool IsLimitBreak { get; init; }
        public uint JobId { get; set; }
        public long Damage { get; set; }
        public long Heal { get; set; }
        public long Taken { get; set; }
        public int Hits { get; set; }
        public int Crits { get; set; }
        public int DirectHits { get; set; }
        public int CritDirectHits { get; set; }
        public int Deaths { get; set; }
        public int Dispels { get; set; }

        /// <summary>Damage and hits by action (0xFFFFFF00 for damage over time), to compare with another meter.</summary>
        public Dictionary<uint, (long Damage, int Hits)> Actions { get; } = [];
        public bool Dead { get; set; }
    }

    private sealed class Fight
    {
        public int Number { get; init; }
        public DateTime Start { get; init; }
        public DateTime Last { get; set; }
        public bool Ended { get; set; }
        public ushort Territory { get; init; }
        public Dictionary<uint, Stat> Players { get; } = [];

        /// <summary>Damage taken by each enemy by name: the one that took the most names the fight.</summary>
        public Dictionary<string, long> Targets { get; } = [];

        public string Boss => Targets.Count == 0 ? string.Empty : Targets.MaxBy(pair => pair.Value).Key;
        public TimeSpan Length => (Ended ? Last : DateTime.UtcNow) - Start;
        public long TotalDamage => Players.Values.Sum(stat => stat.Damage);
    }

    /// <summary>What a status does every few seconds, if anything.</summary>
    private enum OverTime : byte { None, Damage, Healing }

    private const byte DamageEffect  = 3;
    private const byte HealEffect    = 4;
    private const byte BlockedEffect = 5;
    private const byte ParriedEffect = 6;
    private const byte RecoveredEffect = 16;   // a status taken off the target (a cleanse)
    private const int HistoryLimit = 24;
    private const float ExtraDockedWidth = 330f;
    private const float ExtraFreeWidth = 430f;
    private const float HistoryWidth = 440f;
    private const byte CriticalFlag  = 0x20;   // in Param0 (the hit severity) of a damage effect
    private const byte DirectHitFlag = 0x40;
    private const byte BeneficialStatus  = 1;   // StatusCategory of buffs
    private const byte DetrimentalStatus = 2;   // StatusCategory of debuffs
    private const uint NoEntity = 0xE0000000;

    /// <summary>Entity id of the "player" that stands for every Limit Break of the fight: it is not a person, so it has its own.</summary>
    private const uint LimitBreakId = 0xFFFFFFF0;
    private const uint TickAction   = 0xFFFFFF00;   // stands for "damage over time" in the breakdown by action

    /// <summary>The action whose effects are being recorded (0 outside of them).</summary>
    private uint _actionId;
    private const uint LimitBreakCategory = 9;     // ActionCategory row of Limit Break actions
    private const byte ActionKindAction   = 1;     // Header.ActionType of an action (as opposed to an item, a mount...)
    private const uint LimitBreakIconAction = 200; // Braver: its icon stands for the Limit Break in the table

    /// <summary>The most players that can be in a duty: an alliance raid.</summary>
    internal const int MaxRows = DamageMeterNode.MaxRows;

    private static readonly TimeSpan EndGrace  = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Refresh   = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    // Colors of the bars, by the role of the job.
    private static readonly Vector4 TankColor   = new(0.22f, 0.42f, 0.80f, 0.85f);
    private static readonly Vector4 HealerColor = new(0.28f, 0.62f, 0.34f, 0.85f);
    private static readonly Vector4 MeleeColor  = new(0.72f, 0.20f, 0.22f, 0.85f);
    private static readonly Vector4 RangedColor = new(0.82f, 0.52f, 0.18f, 0.85f);
    private static readonly Vector4 CasterColor = new(0.58f, 0.30f, 0.72f, 0.85f);
    private static readonly Vector4 OtherColor  = new(0.45f, 0.45f, 0.50f, 0.85f);

    private static readonly string[] HelpTopics = ["Overview", "Table", "Fights", "Panels", "OverTime", "Limits"];

    private readonly Dictionary<uint, Vector4> _jobColors = [];
    private readonly Dictionary<uint, OverTime> _overTime = [];
    private readonly Dictionary<(uint Source, uint Status), double> _tickWeights = [];

    // Ticks arrive one per status when two different statuses tick on the same target within moments; seen once, it is kept.
    private readonly Dictionary<nint, (DateTime At, uint Status, OverTime Kind)> _lastTick = [];
    private bool _perStatusTicks;
    private ExcelSheet<LuminaStatus>? _statusSheet;

    private Hook<ActionEffectHandler.Delegates.Receive>? _hook;
    private Hook<StatusManager.Delegates.ProcessHotDot>? _tickHook;
    private OverlayController? _overlay;
    private DamageMeterNode? _node;
    private DamageDoneSettingsAddon? _addon;
    private TextHelpAddon? _help;
    private Fight? _fight;
    private Fight? _detail;
    private readonly List<Fight> _history = [];
    private SideView _side;
    private SidePanelNode? _extra;
    private SidePanelNode? _historyPanel;
    private bool _extraOpen;
    private bool _seedExtra;
    private Vector2? _placedExtra;
    private readonly List<uint> _mainIds = [];
    private uint _breakdownId;
    private int _fightCount;
    private DateTime _nextPoll = DateTime.MinValue;
    private Exception? _failure;
    private DateTime _nextRefresh = DateTime.MinValue;
    private DateTime _outOfCombatSince = DateTime.MinValue;
    private DateTime? _saveAt;
    private Vector2? _placed;
    private bool _dirty = true;

    /// <summary>Options of this tweak, for the native settings window. Call <see cref="Changed"/> after editing them.</summary>
    internal Options Config => Settings;

    /// <summary>Localized text of this tweak, for the native windows.</summary>
    internal string Text(string key) => T(key);

    protected internal override void Enable()
    {
        var address = ActionEffectHandler.Addresses.Receive.Value;
        if (address == 0)
            throw new InvalidOperationException("The game function that resolves the effects of actions was not found (game updated?).");

        _failure = null;
        _fight = null;
        _placed = null;
        _dirty = true;
        _outOfCombatSince = DateTime.MinValue;

        _overlay = new OverlayController();
        _node = new DamageMeterNode
        {
            OnClear        = ClearTable,
            OnOpenSettings = () => _addon?.Toggle(),
            OnToggleMode   = () =>
            {
                Settings.Mode = Settings.Mode switch { Meter.Damage => Meter.Heal, Meter.Heal => Meter.Taken, _ => Meter.Damage };
                Changed();
            },
            OnToggleExtra   = ToggleExtra,
            OnToggleHistory = () => SetSide(_side == SideView.None ? SideView.History : SideView.None),
            OnCopy          = CopyTable,
            OnRow           = OpenBreakdown,
            OnToggleLock = () =>
            {
                Settings.Locked = !Settings.Locked;
                Changed();
            },
        };
        _extra = new SidePanelNode(ExtraDockedWidth, pin: true, columns: 5) { OnPin = TogglePin };
        _historyPanel = new SidePanelNode(HistoryWidth, pin: false)
        {
            OnRow    = OpenFight,
            OnHeader = () =>
            {
                if (_side == SideView.Detail) SetSide(SideView.History);
                else if (_side == SideView.Breakdown) SetSide(SideView.None);
            },
        };
        _extraOpen = false;
        _side = SideView.None;
        _detail = null;

        _overlay.AddNode(_node.Handle);
        _overlay.AddNode(_extra.Handle!);
        _overlay.AddNode(_node);
        _overlay.AddNode(_historyPanel);
        _overlay.AddNode(_extra);
        ApplyTooltips();

        _addon = NewAddon();

        _hook = Svc.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>((nint)address, OnActionEffect);
        _hook.Enable();

        // Without this one, only what actions do is counted, not what ticks.
        var tickAddress = StatusManager.Addresses.ProcessHotDot.Value;
        if (tickAddress != 0)
        {
            _tickHook = Svc.GameInterop.HookFromAddress<StatusManager.Delegates.ProcessHotDot>((nint)tickAddress, OnTick);
            _tickHook.Enable();
        }
        else
        {
            Svc.Log.Warning("GoblinDamageDone: the game function of damage and healing over time was not found (game updated?); only what actions do is counted.");
        }

        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;

        _hook?.Dispose();
        _hook = null;
        _tickHook?.Dispose();
        _tickHook = null;

        _addon?.Dispose();
        _addon = null;
        _help?.Dispose();
        _help = null;

        // Disposing the controller frees the nodes it owns.
        _overlay?.Dispose();
        _overlay = null;
        _node = null;
        _extra = null;
        _historyPanel = null;
        _fight = null;
        FlushSettings();
    }

    protected override IReadOnlyList<string> CommandNames => ["/gdd", "/goblindamagedone"];

    protected override void OnCommand() => _addon?.Toggle();

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, OpenHelp);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(Loc.Get("Window.Settings"), TMain("Open.Help"), () => _addon?.Toggle()),
        new(TMain("Button.ResetPosition"), TMain("ResetPosition.Help"), ResetPosition),
    ];

    // ── The example shown while the settings are open ────────────────────────────

    /// <summary>A player of the example fight: what each table would say about them.</summary>
    private sealed record Sample(string Name, uint JobId, char Group, long Damage, long Heal, long Taken, string[] Hits);

    // A group of four (tank, healer, melee, caster) in an alliance raid: two of group A, one of B and one of C. The
    // numbers are those of a fight of about two and a half minutes.
    private static readonly Sample[] Samples =
    [
        new("Player 1", 21, 'B', 1_110_000,   168_000, 1_450_000, ["29.5%", "36.7%", "10.8%", "0", "0"]),   // warrior
        new("Player 2",      24, 'C',   780_000,   930_000,   420_000, ["24.1%", "33.0%",  "8.0%", "1", "2"]),   // white mage
        new("Player 3",     34, 'A', 1_980_000,    52_000,   380_000, ["31.2%", "38.5%", "12.1%", "0", "0"]),   // samurai
        new("Player 4",     25, 'A', 1_820_000,    14_000,   310_000, ["26.4%", "41.3%", "10.9%", "0", "0"]),   // black mage
        new("Limit Break",  0, (char)0,    28_000,         0,         0, ["0%", "0%", "0%", "0", "0"]),   // limit break
    ];

    private static readonly TimeSpan SampleLength = TimeSpan.FromSeconds(150);

    /// <summary>While the settings window is open the table shows an example, so every option can be seen working.</summary>
    private bool Previewing => _addon is { IsOpen: true };

    private bool _wasPreviewing;

    private static long SampleAmount(Sample sample, Meter mode) => mode switch
    {
        Meter.Heal  => sample.Heal,
        Meter.Taken => sample.Taken,
        _           => sample.Damage,
    };

    /// <summary>The players of the example that the table lists with the current options, best first.</summary>
    private List<Sample> SamplePlayers(Meter mode)
        => Samples.Where(sample => !Settings.OnlyMyGroup || sample.Group == (char)0 || sample.Group == 'A')
                  .Where(sample => SampleAmount(sample, mode) > 0)
                  .OrderByDescending(sample => SampleAmount(sample, mode))
                  .Take(Math.Clamp(Settings.Rows, 1, MaxRows))
                  .ToList();

    private List<DamageMeterRow> SampleRows(Meter mode)
    {
        var players = SamplePlayers(mode);
        var rows    = new List<DamageMeterRow>();
        if (players.Count == 0) return rows;

        var total   = players.Sum(sample => SampleAmount(sample, mode));
        var top     = SampleAmount(players[0], mode);
        var seconds = SampleLength.TotalSeconds;
        for (var i = 0; i < players.Count; i++)
        {
            var sample = players[i];
            var amount = SampleAmount(sample, mode);
            var isLb   = sample.Group == (char)0;
            var label  = isLb ? T("LimitBreak") : sample.Name;
            var name   = Settings.ShowGroup && !isLb ? $"{i + 1}. [{sample.Group}] {label}" : $"{i + 1}. {label}";
            var value  = $"{Short(amount)} ({Short(amount / seconds)}, {(amount * 100d / total).ToString("0.0", CultureInfo.InvariantCulture)}%)";
            rows.Add(new DamageMeterRow(name, sample.JobId, value, (float)((double)amount / top), ColorOf(sample.JobId),
                IconId: isLb ? LimitBreakIcon : 0));
        }

        return rows;
    }

    private DamageDoneSettingsAddon NewAddon() => new()
    {
        InternalName = "GtkDamageDone",
        Title        = $"{T("Title")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size         = new Vector2(520f, 520f),
        Tweak        = this,
    };

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;

        Rebuild(ref _addon, NewAddon);
        RebuildHelp();
        ApplyTooltips();
        _dirty = true;
    }

    private void ApplyTooltips() => _node?.SetTooltips(
        [T("Tooltip.Mode"), T("Tooltip.Extra"), T("Tooltip.History"), T("Tooltip.Lock"), T("Tooltip.Settings"), T("Tooltip.Copy"), T("Tooltip.Clear")]);

    private void ToggleExtra()
    {
        _extraOpen = !_extraOpen;

        // Two tables docked in the same place would be one over the other.
        if (_extraOpen && !Settings.ExtraDetached) _side = SideView.None;
        _dirty = true;
        _nextRefresh = DateTime.MinValue;
    }

    private void TogglePin()
    {
        Settings.ExtraDetached = !Settings.ExtraDetached;
        if (Settings.ExtraDetached)
            _seedExtra = true;
        else if (_side != SideView.None)
            _side = SideView.None;
        Changed();
        _nextRefresh = DateTime.MinValue;
    }

    private void SetSide(SideView view, Fight? detail = null)
    {
        _side = view;
        if (view != SideView.None && !Settings.ExtraDetached) _extraOpen = false;
        _detail = view == SideView.Detail ? detail ?? _detail : null;
        _dirty = true;
        _nextRefresh = DateTime.MinValue;
    }

    /// <summary>Clicked a player of the main table: their damage by action at the side, or closes it when it already shows them.</summary>
    private void OpenBreakdown(int line)
    {
        if (!Settings.BreakdownOnClick || Previewing || line < 0 || line >= _mainIds.Count) return;

        var id = _mainIds[line];
        if (_side == SideView.Breakdown && _breakdownId == id)
        {
            SetSide(SideView.None);
            return;
        }

        _breakdownId = id;
        SetSide(SideView.Breakdown);
    }

    /// <summary>
    /// Copies the table of the fight as text, one player per line, to paste in Discord or a chat: what it shows, the boss
    /// and how long it lasted, then each line as the table writes it.
    /// </summary>
    private void CopyTable()
    {
        if (Previewing || _fight is not { } fight || fight.Players.Count == 0)
        {
            Svc.Chat.Print(T("Chat.NothingToCopy"), Name);
            return;
        }

        var mode  = Settings.Mode;
        var rows  = RowsOf(fight, mode, Math.Clamp(Settings.Rows, 1, MaxRows), null);
        var title = $"{TitleOf(mode)} · {(fight.Boss.Length > 0 ? fight.Boss : T("Side.Unknown"))} · {Length(fight.Length)}";
        var text  = string.Join("\n", rows.Select(row => $"{row.Name}  {row.Value}").Prepend(title));

        Svc.Chat.Print(T(ClipboardHelper.SetText(text) ? "Chat.Copied" : "Chat.CopyFailed"), Name);
    }

    /// <summary>Clicked a fight of the history: opens its details.</summary>
    private void OpenFight(int line)
    {
        if (_side != SideView.History) return;

        var index = _history.Count - 1 - line;
        if (index < 0 || index >= _history.Count) return;

        SetSide(SideView.Detail, _history[index]);
    }

    /// <summary>
    /// In an alliance raid, the letter (A, B, C…) of the group of each player by entity id; null otherwise.
    /// <paramref name="mine"/> is the letter of your own group. The own group is the first slot of the game's alliance list.
    /// </summary>
    private static Dictionary<uint, char>? GroupLetters(out char mine)
    {
        mine = '?';
        var manager = GroupManager.Instance();
        if (manager == null) return null;

        var group = &manager->MainGroup;
        if (!group->IsAlliance) return null;

        char Letter(int slot)
        {
            var index = group->GetAllianceGroupIndexBySlot(slot);
            return index == 0xFF ? '?' : (char)('A' + index);
        }

        var letters = new Dictionary<uint, char>();
        mine = Letter(0);
        for (var i = 0; i < group->PartyMembers.Length; i++)
        {
            var id = group->PartyMembers[i].EntityId;
            if (id != 0 && id != NoEntity) letters[id] = mine;
        }

        var size = group->IsSmallGroupAlliance ? 4 : 8;
        for (var i = 0; i < group->AllianceMembers.Length; i++)
        {
            var id = group->AllianceMembers[i].EntityId;
            if (id != 0 && id != NoEntity) letters[id] = Letter(1 + i / size);
        }

        return letters;
    }

    internal void OpenHelp()
    {
        _help ??= NewHelp();
        _help.Toggle();
    }

    private TextHelpAddon NewHelp() => new()
    {
        InternalName = "GtkDamageDoneHelp",
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

    private void ClearTable()
    {
        _fight = null;
        _dirty = true;
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

    // ── Reading the fight ───────────────────────────────────────────────────────

    /// <summary>Runs on the game thread, inside the game's own code: it must never throw.</summary>
    private void OnActionEffect(uint casterEntityId, Character* caster, Vector3* targetPos, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            _actionId = header != null ? header->ActionId : 0;
            Record(casterEntityId, header, effects, targetEntityIds);
        }
        catch (Exception ex)
        {
            // Reported from OnUpdate: disabling the tweak here would free the hook that is running.
            _failure ??= ex;
        }

        _actionId = 0;
        _hook!.Original(casterEntityId, caster, targetPos, header, effects, targetEntityIds);
    }

    private void Record(uint casterEntityId, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        if (header == null || effects == null || targetEntityIds == null) return;

        var manager = GameObjectManager.Instance();
        if (manager == null) return;

        var caster = manager->Objects.GetObjectByEntityId(casterEntityId);

        // What an enemy does to the players is damage taken.
        if (caster != null && IsHostile(manager, caster))
        {
            RecordTaken(manager, header, effects, targetEntityIds);
            return;
        }

        // A Limit Break is not the doing of whoever pressed it: like the other meters, it is a line of its own.
        if (header->ActionType == ActionKindAction && IsLimitBreak(header->ActionId))
        {
            RecordLimitBreak(manager, header, effects, targetEntityIds);
            return;
        }

        if (caster == null)
        {
            _skipped.NoCaster++;
            return;
        }

        var credit = CreditedPlayer(manager, casterEntityId);
        if (credit == null)
        {
            _skipped.NotOfTheDuty++;
            return;
        }

        for (var target = 0; target < header->NumTargets; target++)
        {
            var enemy = manager->Objects.GetObjectByEntityId(targetEntityIds[target].ObjectId);
            if (enemy == null)
            {
                _skipped.NoTarget++;
                continue;
            }

            // The game says what each heal did, overheal included; the health before it lands says how much of it was real.
            var missing = Settings.IncludeOverheal ? long.MaxValue : MissingHealth(enemy);

            foreach (ref readonly var effect in effects[target].Effects)
            {
                if (effect.Type == RecoveredEffect)
                {
                    AddDispel(credit);
                    continue;
                }

                var heal = effect.Type == HealEffect;
                if (!heal && effect.Type is not (DamageEffect or BlockedEffect or ParriedEffect)) continue;

                // The amount is 16 bits, and a flag says the next byte holds the higher ones.
                long amount = effect.Value;
                if ((effect.Param4 & 0x40) != 0)
                    amount += (long)effect.Param3 << 16;

                if (heal)
                {
                    var restored = Math.Min(amount, missing);
                    if (missing != long.MaxValue) missing -= restored;
                    AddHeal(credit, restored);
                }
                else
                {
                    AddDamage(credit, enemy, amount);
                    if (IsHostile(manager, enemy)) AddHit(credit, effect.Param0);
                }
            }
        }
    }

    private void RecordTaken(GameObjectManager* manager, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        for (var target = 0; target < header->NumTargets; target++)
        {
            var victim = manager->Objects.GetObjectByEntityId(targetEntityIds[target].ObjectId);
            if (victim == null || victim->ObjectKind != ObjectKind.Pc) continue;
            if (!Counts(victim->EntityId)) continue;

            foreach (ref readonly var effect in effects[target].Effects)
            {
                if (effect.Type is not (DamageEffect or BlockedEffect or ParriedEffect)) continue;

                long amount = effect.Value;
                if ((effect.Param4 & 0x40) != 0)
                    amount += (long)effect.Param3 << 16;
                if (amount <= 0) continue;

                var fight = ActiveFight(damage: true);
                if (fight == null) return;

                fight.Last = DateTime.UtcNow;
                Credit(fight, (Character*)victim).Taken += amount;
                _dirty = true;
            }
        }
    }

    // ── Limit Break ─────────────────────────────────────────────────────────────

    private readonly Dictionary<uint, bool> _limitBreakActions = [];
    private uint? _limitBreakIcon;

    /// <summary>Whether an action is a Limit Break (its category says so).</summary>
    private bool IsLimitBreak(uint actionId)
    {
        if (_limitBreakActions.TryGetValue(actionId, out var known)) return known;

        var sheet = Svc.Data.GetExcelSheet<LuminaAction>();
        var is_   = sheet != null && sheet.TryGetRow(actionId, out var row) && row.ActionCategory.RowId == LimitBreakCategory;
        return _limitBreakActions[actionId] = is_;
    }

    /// <summary>The icon the Limit Break line is drawn with.</summary>
    private uint LimitBreakIcon
        => _limitBreakIcon ??= Svc.Data.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(LimitBreakIconAction)?.Icon ?? 0u;

    private static Stat LimitBreakStat(Fight fight)
    {
        if (!fight.Players.TryGetValue(LimitBreakId, out var stat))
            fight.Players[LimitBreakId] = stat = new Stat { Name = "Limit Break", IsLimitBreak = true };
        return stat;
    }

    /// <summary>Adds damage to the breakdown by action of a player: what is being recorded is the action of the effect, or a tick.</summary>
    private void AddToBreakdown(Stat stat, long amount)
    {
        var entry = stat.Actions.GetValueOrDefault(_actionId);
        stat.Actions[_actionId] = (entry.Damage + amount, entry.Hits + 1);
    }

    /// <summary>The name of an action for the log, with "auto" when it is an auto-attack.</summary>
    private string ActionLabel(uint actionId)
    {
        if (actionId == TickAction) return "damage over time";
        if (actionId == 0) return "?";

        var row = Svc.Data.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
        var name = row?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name)) name = actionId.ToString();
        return row?.ActionCategory.RowId == 1 ? $"{name} (auto)" : name;
    }

    /// <summary>The effects of a Limit Break: its damage and healing go to the Limit Break line, whoever pressed it.</summary>
    private void RecordLimitBreak(GameObjectManager* manager, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        for (var target = 0; target < header->NumTargets; target++)
        {
            var victim = manager->Objects.GetObjectByEntityId(targetEntityIds[target].ObjectId);
            if (victim == null)
            {
                _skipped.NoTarget++;
                continue;
            }

            var missing = Settings.IncludeOverheal ? long.MaxValue : MissingHealth(victim);
            foreach (ref readonly var effect in effects[target].Effects)
            {
                var heal = effect.Type == HealEffect;
                if (!heal && effect.Type is not (DamageEffect or BlockedEffect or ParriedEffect)) continue;

                long amount = effect.Value;
                if ((effect.Param4 & 0x40) != 0)
                    amount += (long)effect.Param3 << 16;
                if (amount <= 0) continue;

                var fight = ActiveFight(damage: true);
                if (fight == null) return;

                fight.Last = DateTime.UtcNow;
                var stat = LimitBreakStat(fight);
                if (heal)
                {
                    var restored = Math.Min(amount, missing);
                    if (missing != long.MaxValue) missing -= restored;
                    stat.Heal += restored;
                }
                else if (IsHostile(manager, victim))
                {
                    stat.Damage += amount;
                    AddToBreakdown(stat, amount);
                    var name = ((Character*)victim)->NameString;
                    fight.Targets[name] = fight.Targets.GetValueOrDefault(name) + amount;

                    stat.Hits++;
                    if ((effect.Param0 & CriticalFlag) != 0) stat.Crits++;
                    if ((effect.Param0 & DirectHitFlag) != 0) stat.DirectHits++;
                    if ((effect.Param0 & (CriticalFlag | DirectHitFlag)) == (CriticalFlag | DirectHitFlag)) stat.CritDirectHits++;
                }

                _dirty = true;
            }
        }
    }

    // ── What was left out, for the log ──────────────────────────────────────────

    /// <summary>What the capture could not use during the fight, to compare with another meter: logged when it ends.</summary>
    private struct Skipped
    {
        public int NoCaster, NotOfTheDuty, NoTarget, NotEnemy;
        public long TickLost, TickCounted;
    }

    private Skipped _skipped;

    /// <summary>One hit of an action: the game says in a flag of the effect whether it was critical and a direct hit.</summary>
    private void AddHit(Character* credit, byte flags)
    {
        if (ActiveFight() is not { } running) return;

        var stat   = Credit(running, credit);
        var crit   = (flags & CriticalFlag) != 0;
        var direct = (flags & DirectHitFlag) != 0;
        stat.Hits++;
        if (crit) stat.Crits++;
        if (direct) stat.DirectHits++;
        if (crit && direct) stat.CritDirectHits++;
        _dirty = true;
    }

    private void AddDispel(Character* credit)
    {
        if (ActiveFight() is not { } running) return;

        Credit(running, credit).Dispels++;
        _dirty = true;
    }

    /// <summary>Counts the deaths: each time someone of the duty or of your party goes from alive to dead.</summary>
    private void PollDeaths(Fight fight)
    {
        var manager = GameObjectManager.Instance();
        if (manager == null) return;

        var ids = new HashSet<uint>();
        var me = Control.GetLocalPlayer();
        if (me != null) ids.Add(me->EntityId);

        var groups = GroupManager.Instance();
        if (groups != null)
        {
            var group = &groups->MainGroup;
            for (var i = 0; i < group->PartyMembers.Length; i++) ids.Add(group->PartyMembers[i].EntityId);
            if (InDuty())
                for (var i = 0; i < group->AllianceMembers.Length; i++) ids.Add(group->AllianceMembers[i].EntityId);
        }

        // Out of a duty, a cross-world party is only in the cross-world list.
        var crossRealm = InfoProxyCrossRealm.Instance();
        if (crossRealm != null && InfoProxyCrossRealm.IsCrossRealmParty() && crossRealm->LocalPlayerGroupIndex < crossRealm->CrossRealmGroups.Length)
        {
            ref var party = ref crossRealm->CrossRealmGroups[crossRealm->LocalPlayerGroupIndex];
            for (var i = 0; i < party.GroupMemberCount && i < party.GroupMembers.Length; i++) ids.Add(party.GroupMembers[i].EntityId);
        }

        foreach (var id in ids)
        {
            if (id is 0 or NoEntity) continue;

            var player = manager->Objects.GetObjectByEntityId(id);
            if (player == null || player->ObjectKind != ObjectKind.Pc) continue;

            var chara = (Character*)player;
            var stat  = Credit(fight, chara);
            var dead  = chara->Health == 0 && chara->MaxHealth > 0;
            if (dead && !stat.Dead)
            {
                stat.Deaths++;
                _dirty = true;
            }
            stat.Dead = dead;
        }
    }

    private void AddHeal(Character* credit, long amount)
    {
        if (amount <= 0 || ActiveFight() is not { } running) return;

        running.Last = DateTime.UtcNow;
        Credit(running, credit).Heal += amount;
        _dirty = true;
    }

    /// <summary>How much health a target is missing right now; no limit when it cannot be told.</summary>
    private static long MissingHealth(GameObject* target)
    {
        if (target->ObjectKind is not (ObjectKind.Pc or ObjectKind.BattleNpc)) return long.MaxValue;

        var chara = (Character*)target;
        return chara->MaxHealth == 0 ? long.MaxValue : Math.Max(0L, (long)chara->MaxHealth - chara->Health);
    }

    private void AddDamage(Character* credit, GameObject* enemy, long amount)
    {
        if (amount <= 0) return;
        if (!IsHostile(GameObjectManager.Instance(), enemy))
        {
            _skipped.NotEnemy++;
            return;
        }

        var fight = ActiveFight(damage: true);
        if (fight == null) return;

        fight.Last = DateTime.UtcNow;
        var stat = Credit(fight, credit);
        stat.Damage += amount;
        AddToBreakdown(stat, amount);

        var name = ((Character*)enemy)->NameString;
        fight.Targets[name] = fight.Targets.GetValueOrDefault(name) + amount;
        _dirty = true;
    }

    /// <summary>
    /// The game working out one tick of damage or healing over time. Runs inside the game's own code: it must never throw.
    /// </summary>
    private void OnTick(StatusManager* statuses, BattleChara* target, uint statusId, int tickMode, uint value, uint sourceEntityId, int damageType)
    {
        try
        {
            _actionId = TickAction;
            RecordTick(target, statusId, tickMode, value, sourceEntityId);
        }
        catch (Exception ex)
        {
            _failure ??= ex;
        }

        _actionId = 0;
        _tickHook!.Original(statuses, target, statusId, tickMode, value, sourceEntityId, damageType);
    }

    private void RecordTick(BattleChara* target, uint statusId, int tickMode, uint value, uint sourceEntityId)
    {
        if (target == null || value == 0) return;
        // Two different statuses of the same kind (both damage, or both healing) ticking on one target at once: the game
        // sends them one by one. A damage tick next to a healing one proves nothing, they may be two sums.
        var now      = DateTime.UtcNow;
        var statusKind = statusId == 0 ? OverTime.None : OverTimeOf(statusId);
        if (_lastTick.TryGetValue((nint)target, out var last) && now - last.At < TimeSpan.FromMilliseconds(150)
            && last.Status != statusId && statusKind != OverTime.None && last.Kind == statusKind)
            _perStatusTicks = true;
        if (_lastTick.Count > 256) _lastTick.Clear();
        _lastTick[(nint)target] = (now, statusId, statusKind);

        if (!Settings.IncludeOverTime) return;

        var manager = GameObjectManager.Instance();
        if (manager == null) return;

        // What the status says it does comes first; then the kind of effect the game reports; then who the target is.
        var onEnemy = IsHostile(manager, (GameObject*)target);
        var kind    = statusKind;
        if (kind == OverTime.None)
            kind = tickMode == HealEffect ? OverTime.Healing : tickMode == DamageEffect ? OverTime.Damage : onEnemy ? OverTime.Damage : OverTime.Healing;

        // Damage over time on a player is damage taken, not healing.
        if (kind == OverTime.Damage && !onEnemy)
        {
            RecordTakenTick((GameObject*)target, value);
            return;
        }

        // The game says whose it is for what ticks from the ground (Doton, Salted Earth...).
        if (sourceEntityId is not (0 or NoEntity))
        {
            var source = CreditedPlayer(manager, sourceEntityId);
            if (source != null)
            {
                AddTick(source, (GameObject*)target, kind, value);
                if (kind == OverTime.Damage) _skipped.TickCounted += value;
            }
            else if (kind == OverTime.Damage)
            {
                _skipped.TickLost += value;
            }
            return;
        }

        var list = target->GetStatusManager();
        if (list == null)
        {
            if (kind == OverTime.Damage) _skipped.TickLost += value;
            return;
        }

        // One tick per status: the status that ticked is the one that did it, and the tick is all its own.
        if (_perStatusTicks && statusId != 0)
        {
            var found = false;
            foreach (ref readonly var status in list->Status)
            {
                if (status.StatusId != statusId) continue;

                found = true;
                var player = CreditedPlayer(manager, status.SourceObject.ObjectId);
                if (player == null) continue;

                AddTick(player, (GameObject*)target, kind, value);
                if (kind == OverTime.Damage) _skipped.TickCounted += value;
                return;
            }

            if (found)
            {
                if (kind == OverTime.Damage) _skipped.TickLost += value;
                return;
            }
        }

        // Otherwise the game adds up the ticks of every status of the target in one number. It is shared out among the
        // statuses that cause it in proportion to what each is worth, as other meters do with the potency of each. Here
        // that is learned: a tick that has a single status on the target is exactly that status's, and it is kept as its
        // weight for when others are on the same target. Until it is known, a status weighs what the known ones do on
        // average. A status of someone who is not of the duty (an enemy) takes its share too, so it does not go to a player.
        var parts = new List<(nint Player, uint Source, uint Status)>();
        foreach (ref readonly var status in list->Status)
        {
            uint id = status.StatusId;
            if (id == 0 || OverTimeOf(id) != kind) continue;

            var sourceId = status.SourceObject.ObjectId;
            parts.Add(((nint)CreditedPlayer(manager, sourceId), sourceId, id));
        }

        if (parts.Count == 0)
        {
            if (kind == OverTime.Damage) _skipped.TickLost += value;
            return;
        }

        if (parts.Count == 1 && parts[0].Source is not (0 or NoEntity))
            _tickWeights[(parts[0].Source, parts[0].Status)] = value;

        var known    = parts.Select(part => _tickWeights.TryGetValue((part.Source, part.Status), out var weight) ? weight : 0d).ToList();
        var average  = known.Where(weight => weight > 0d).DefaultIfEmpty(0d).Average();
        var weights  = known.Select(weight => weight > 0d ? weight : average > 0d ? average : 1d).ToList();
        var sum      = weights.Sum();

        long credited = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i].Player == 0) continue;

            var amount = (long)(value * weights[i] / sum);
            AddTick((Character*)parts[i].Player, (GameObject*)target, kind, amount);
            credited += amount;
        }

        if (kind == OverTime.Damage)
        {
            _skipped.TickCounted += credited;
            _skipped.TickLost    += value - credited;
        }
    }

    /// <summary>A tick of damage over time on a player: counted as damage taken, in a duty or on yourself.</summary>
    private void RecordTakenTick(GameObject* target, uint value)
    {
        if (target->ObjectKind != ObjectKind.Pc) return;

        if (!Counts(target->EntityId)) return;

        var fight = ActiveFight(damage: true);
        if (fight == null) return;

        fight.Last = DateTime.UtcNow;
        Credit(fight, (Character*)target).Taken += value;
        _dirty = true;
    }

    private void AddTick(Character* credit, GameObject* target, OverTime kind, long amount)
    {
        if (kind == OverTime.Damage)
        {
            AddDamage(credit, target, amount);
            return;
        }

        if (!Settings.IncludeOverheal) amount = Math.Min(amount, MissingHealth(target));
        AddHeal(credit, amount);
    }

    /// <summary>
    /// Whether a status deals damage or heals over time. The game has no column for it, but its own
    /// description of every such status says so in the same words; the English text is read whatever
    /// the language of the game, which carries all of them.
    /// </summary>
    private OverTime OverTimeOf(uint statusId)
    {
        if (_overTime.TryGetValue(statusId, out var kind)) return kind;

        kind = OverTime.None;
        _statusSheet ??= Svc.Data.GetExcelSheet<LuminaStatus>(Dalamud.Game.ClientLanguage.English);
        if (_statusSheet.TryGetRow(statusId, out var row))
        {
            var description = row.Description.ExtractText();
            if (row.StatusCategory == DetrimentalStatus
                && (description.Contains("damage over time", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("bleeding HP over time", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("draining HP", StringComparison.OrdinalIgnoreCase)))
                kind = OverTime.Damage;
            else if (row.StatusCategory == BeneficialStatus && description.Contains("HP over time", StringComparison.OrdinalIgnoreCase))
                kind = OverTime.Healing;
        }

        return _overTime[statusId] = kind;
    }

    /// <summary>The player an action counts for: its caster, or the owner of the pet or chocobo that cast it. Null when it is nobody of the duty or of your party.</summary>
    private static Character* CreditedPlayer(GameObjectManager* manager, uint casterEntityId)
    {
        var caster = manager->Objects.GetObjectByEntityId(casterEntityId);
        if (caster == null) return null;

        if (caster->ObjectKind == ObjectKind.BattleNpc && caster->OwnerId is not (0 or NoEntity))
            caster = manager->Objects.GetObjectByEntityId(caster->OwnerId);
        if (caster == null || caster->ObjectKind != ObjectKind.Pc) return null;

        return Counts(caster->EntityId) ? (Character*)caster : null;
    }

    private static bool InDuty()
        => Svc.Condition[ConditionFlag.BoundByDuty] || Svc.Condition[ConditionFlag.BoundByDuty56];

    /// <summary>
    /// Whether a player counts: you, your party, and in a duty your alliance. Not everyone around: Eureka, Bozja, Occult
    /// Crescent and Frontline are duties too, full of players who are not with you.
    /// </summary>
    private static bool Counts(uint entityId)
    {
        var player = Control.GetLocalPlayer();
        if (player != null && entityId == player->EntityId) return true;

        return InParty(entityId) || (InDuty() && InAlliance(entityId));
    }

    /// <summary>Whether a player is in one of the other groups of your alliance.</summary>
    private static bool InAlliance(uint entityId)
    {
        if (entityId is 0 or NoEntity) return false;

        var groups = GroupManager.Instance();
        if (groups == null || !groups->MainGroup.IsAlliance) return false;

        var group = &groups->MainGroup;
        for (var i = 0; i < group->AllianceMembers.Length; i++)
            if (group->AllianceMembers[i].EntityId == entityId) return true;

        return false;
    }

    /// <summary>
    /// Whether a player is in your party. A party of your own world is in the group; out of a duty, a cross-world
    /// one (Party Finder) is only in the cross-world list.
    /// </summary>
    private static bool InParty(uint entityId)
    {
        if (entityId is 0 or NoEntity) return false;

        var groups = GroupManager.Instance();
        if (groups != null && groups->MainGroup.IsEntityIdInParty(entityId)) return true;

        return InfoProxyCrossRealm.IsCrossRealmParty() && InfoProxyCrossRealm.GetMemberByEntityId(entityId) != null;
    }

    /// <summary>
    /// Whether an object is on the other side: a combatant, or what an enemy summoned (the adds of a boss are not
    /// "combatants" of their own, they belong to the boss). What belongs to a player (a pet, a chocobo) or is an ally is not.
    /// </summary>
    private static bool IsHostile(GameObjectManager* manager, GameObject* target)
    {
        if (target == null || target->ObjectKind != ObjectKind.BattleNpc) return false;
        if (target->SubKind == (byte)BattleNpcSubKind.Combatant) return true;

        var owner = target->OwnerId;
        if (manager == null || owner is 0 or NoEntity) return false;

        var parent = manager->Objects.GetObjectByEntityId(owner);
        return parent != null && parent->ObjectKind != ObjectKind.Pc;
    }

    private static Stat Credit(Fight fight, Character* player)
    {
        if (!fight.Players.TryGetValue(player->EntityId, out var stat))
        {
            stat = new Stat { Name = player->NameString };
            fight.Players[player->EntityId] = stat;
        }

        stat.JobId = player->ClassJob;
        return stat;
    }

    /// <summary>The fight damage counts for: the one in progress, or a new one if you are in combat and the last one ended.</summary>
    private Fight? ActiveFight(bool damage = false)
    {
        if (_fight is { Ended: false }) return _fight;
        // A fight starts with the first damage even if your own combat flag is not up: the flag is yours, and the
        // others of the duty or of your party may be fighting (only theirs comes this far). Anything else needs the flag.
        if (!Svc.Condition[ConditionFlag.InCombat] && !damage) return null;

        var now = DateTime.UtcNow;
        _outOfCombatSince = DateTime.MinValue;

        // The history is of one place: fights of another zone do not mix with it, and numbering starts again.
        var main      = GameMain.Instance();
        var territory = main == null ? (ushort)0 : (ushort)main->CurrentTerritoryTypeId;
        if (_history.Count > 0 && _history[^1].Territory != territory)
        {
            _history.Clear();
            _fightCount = 0;
            _detail = null;
            if (_side == SideView.Detail) _side = SideView.History;

            // What was learned of the ticks is of entities of the last zone, which are gone.
            _tickWeights.Clear();
            _lastTick.Clear();
        }

        var fight = _fight = new Fight { Number = ++_fightCount, Start = now, Last = now, Territory = territory };
        _history.Add(fight);
        if (_history.Count > HistoryLimit) _history.RemoveAt(0);
        _skipped = default;
        Svc.Log.Debug($"GoblinDamageDone: fight #{fight.Number} started.");
        return fight;
    }

    // ── The panel ───────────────────────────────────────────────────────────────

    private void OnUpdate(IFramework _)
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

            if (_node is not { } node) return;

            EndFightWhenOver();
            PlacePanel(node);

            var now = DateTime.UtcNow;
            // The example appears when the settings open and goes when they close: draw again at once.
            if (Previewing != _wasPreviewing)
            {
                _wasPreviewing = Previewing;
                _dirty = true;
                _nextRefresh = DateTime.MinValue;
            }

            var live = _fight is { Ended: false };
            if (_fight is { Ended: false } running && now >= _nextPoll)
            {
                _nextPoll = now + TimeSpan.FromMilliseconds(500);
                PollDeaths(running);
            }

            if (!_dirty || (live && now < _nextRefresh)) return;

            _dirty = false;
            _nextRefresh = now + Refresh;
            ShowFight(node);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>The fight is over when you have been out of combat a few seconds (a phase change does not take you out of it).</summary>
    private void EndFightWhenOver()
    {
        if (_fight is not { Ended: false } fight) return;

        if (Svc.Condition[ConditionFlag.InCombat])
        {
            _outOfCombatSince = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        if (_outOfCombatSince == DateTime.MinValue) _outOfCombatSince = now;
        if (now - _outOfCombatSince < EndGrace) return;

        // Being out of combat is only about you: dead (or raised and not yet back in it), you are out while the
        // fight goes on. It is over when nobody has done or taken anything for a moment either, like other meters.
        if (now - fight.Last < EndGrace) return;

        fight.Ended = true;
        _dirty = true;
        LogFight(fight);
    }

    /// <summary>
    /// What the fight added up to and what the capture left out, written once when it ends: the lines to compare with
    /// another meter (the same fight length, the same totals per player) and see where a difference comes from.
    /// </summary>
    private void LogFight(Fight fight)
    {
        var players = string.Join(" | ", fight.Players.Values
            .OrderByDescending(stat => stat.Damage)
            .Select(stat => $"{stat.Name} dmg {stat.Damage:N0} heal {stat.Heal:N0} taken {stat.Taken:N0} hits {stat.Hits} (crit {stat.Crits}, direct {stat.DirectHits}) "
                + $"by action [{string.Join("; ", stat.Actions.OrderByDescending(pair => pair.Value.Damage).Take(12).Select(pair => $"{ActionLabel(pair.Key)} {pair.Value.Damage:N0} x{pair.Value.Hits}"))}]"));
        Svc.Log.Debug($"GoblinDamageDone: fight #{fight.Number} ended after {fight.Length:m\\:ss} against {(fight.Boss.Length > 0 ? fight.Boss : "-")}; "
            + $"{players}. Left out: effects with no caster {_skipped.NoCaster}, caster not of the duty {_skipped.NotOfTheDuty}, "
            + $"target not found {_skipped.NoTarget}, target not an enemy {_skipped.NotEnemy}; "
            + $"damage over time counted {_skipped.TickCounted:N0}, not credited {_skipped.TickLost:N0}.");
    }

    /// <summary>Puts the panel where it was left, and takes its new place when the user has dragged it.</summary>
    /// <summary>
    /// Whether the table is on screen: on, and with "only when it matters" also in combat, in a duty or during a fight.
    /// While the settings are open it always is, to show the example.
    /// </summary>
    private bool ShownNow => Previewing
        || Settings.Visible && (!Settings.OnlyWhenRelevant || Svc.Condition[ConditionFlag.InCombat] || InDuty() || _fight is { Ended: false });

    private void PlacePanel(DamageMeterNode node)
    {
        var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (device == null) return;

        var anchor = new Vector2(device->Width / 2f, device->Height / 2f);
        node.Shown   = ShownNow;
        node.RowsClickable = Settings.BreakdownOnClick && !Previewing;
        node.Locked  = Settings.Locked;
        node.Opacity  = Settings.Opacity / 100f;
        node.FontSize = Settings.FontSize;
        node.Zoom     = Settings.Scale / 100f;

        // The title is what the user drags; the rest of the table follows it.
        var handle = node.Handle;
        if (_placed is { } placed && handle.Position != placed)
        {
            var point = handle.Position - anchor;
            Settings.OffsetX = (int)MathF.Round(point.X);
            Settings.OffsetY = (int)MathF.Round(point.Y);
            _saveAt = DateTime.UtcNow + SaveDelay;
        }

        handle.Position = anchor + new Vector2(Settings.OffsetX, Settings.OffsetY);
        node.Position   = handle.Position;
        _placed = handle.Position;

        PlaceSidePanels(anchor, handle.Position);
    }

    /// <summary>Puts the tables at the side: next to the main one, or the extra one wherever the user left it.</summary>
    private void PlaceSidePanels(Vector2 anchor, Vector2 mainPosition)
    {
        var zoom   = Settings.Scale / 100f;
        var docked = mainPosition + new Vector2((DamageMeterNode.PanelWidth + DamageMeterNode.SideGap) * zoom, 0f);

        if (_historyPanel is { } history)
        {
            history.SetOpacity(Settings.Opacity / 100f);
            history.SetFontSize(Settings.FontSize);
            history.SetZoom(zoom);
            history.Position  = docked;
            history.IsVisible = ShownNow && _side != SideView.None && !Previewing;
        }

        if (_extra is not { } extra) return;

        extra.SetOpacity(Settings.Opacity / 100f);
        extra.SetFontSize(Settings.FontSize);
        extra.SetZoom(zoom);

        if (Settings.ExtraDetached)
        {
            var handle = extra.Handle!;
            if (_seedExtra)
            {
                // It starts where it was docked, so it does not jump.
                var point = docked - anchor;
                Settings.ExtraX = (int)MathF.Round(point.X);
                Settings.ExtraY = (int)MathF.Round(point.Y);
                _seedExtra = false;
                _placedExtra = null;
            }
            else if (_placedExtra is { } placed && handle.Position != placed)
            {
                var point = handle.Position - anchor;
                Settings.ExtraX = (int)MathF.Round(point.X);
                Settings.ExtraY = (int)MathF.Round(point.Y);
                _saveAt = DateTime.UtcNow + SaveDelay;
            }

            handle.Position = anchor + new Vector2(Settings.ExtraX, Settings.ExtraY);
            extra.Position  = handle.Position;
            _placedExtra    = handle.Position;
        }
        else
        {
            extra.Position = docked;
            _placedExtra   = null;
        }

        extra.IsVisible        = ShownNow && (_extraOpen || Previewing);
        extra.Handle!.IsVisible = extra.IsVisible && Settings.ExtraDetached;
    }

    private void ShowFight(DamageMeterNode node)
    {
        ShowMain(node);
        ShowSide(node);
    }

    private string TitleOf(Meter mode) => mode switch
    {
        Meter.Heal  => T("Title.Heal"),
        Meter.Taken => T("Title.Taken"),
        _           => T("Title.Damage"),
    };

    private void ShowMain(DamageMeterNode node)
    {
        var title = TitleOf(Settings.Mode);
        node.Title = title;
        node.TitleTooltip = string.Empty;

        _mainIds.Clear();
        if (Previewing)
        {
            node.Title        = $"{title} · {T("Preview.Boss")}";
            node.TitleTooltip = string.Empty;
            node.SetRows(SampleRows(Settings.Mode));
            return;
        }

        if (_fight is not { } fight || fight.Players.Count == 0)
        {
            node.SetRows([]);
            return;
        }

        if (fight.Boss.Length > 0)
        {
            node.Title = $"{title} · {fight.Boss}";
            node.TitleTooltip = fight.Boss;
        }

        node.SetRows(RowsOf(fight, Settings.Mode, Math.Clamp(Settings.Rows, 1, MaxRows), null, _mainIds));
    }

    private static long AmountOf(Stat stat, Meter mode) => mode switch
    {
        Meter.Heal  => stat.Heal,
        Meter.Taken => stat.Taken,
        _           => stat.Damage,
    };

    private static double SecondsOf(Fight fight) => Math.Max(1d, fight.Length.TotalSeconds);

    /// <summary>
    /// The lines of a fight for one table, best first. With <paramref name="before"/> (an earlier fight against the same boss),
    /// each line also says how its rate compares with that one.
    /// </summary>
    private List<DamageMeterRow> RowsOf(Fight fight, Meter mode, int limit, Fight? before, List<uint>? ids = null)
    {
        var myGroup = (char)0;
        var letters = Settings.ShowGroup || Settings.OnlyMyGroup ? GroupLetters(out myGroup) : null;
        var ranked = fight.Players.Where(pair => AmountOf(pair.Value, mode) > 0)
                                  .Where(pair => letters is null || !Settings.OnlyMyGroup || pair.Value.IsLimitBreak
                                                 || (letters.TryGetValue(pair.Key, out var letter) && letter == myGroup))
                                  .OrderByDescending(pair => AmountOf(pair.Value, mode))
                                  .ToList();
        var total = ranked.Sum(pair => AmountOf(pair.Value, mode));
        var rows = new List<DamageMeterRow>();
        if (total == 0) return rows;

        var seconds = SecondsOf(fight);
        var top     = AmountOf(ranked[0].Value, mode);
        var me      = Control.GetLocalPlayer();
        var myId    = me == null ? 0 : me->EntityId;
        var beforeSeconds = before is null ? 1d : SecondsOf(before);
        var oldRate = before?.Players.ToDictionary(pair => pair.Key, pair => AmountOf(pair.Value, mode) / beforeSeconds);

        for (var i = 0; i < ranked.Count && rows.Count < limit; i++)
        {
            var (id, stat) = (ranked[i].Key, ranked[i].Value);

            // Past the limit, you still see yourself, in the last line.
            var shown = i;
            if (rows.Count == limit - 1 && id != myId)
            {
                var mine = ranked.FindIndex(pair => pair.Key == myId);
                if (mine > i) { (id, stat) = (ranked[mine].Key, ranked[mine].Value); shown = mine; }
            }

            ids?.Add(id);
            var amount = AmountOf(stat, mode);
            var rate   = amount / seconds;
            var value  = oldRate is null
                ? $"{Short(amount)} ({Short(rate)}, {(amount * 100d / total).ToString("0.0", CultureInfo.InvariantCulture)}%)"
                : $"{Short(amount)} ({Short(rate)}) {Delta(rate, oldRate.GetValueOrDefault(id))}";

            var label = stat.IsLimitBreak ? T("LimitBreak") : stat.Name;
            rows.Add(new DamageMeterRow(
                letters is not null && Settings.ShowGroup && letters.TryGetValue(id, out var group)
                    ? $"{shown + 1}. [{group}] {label}"
                    : $"{shown + 1}. {label}",
                stat.JobId,
                value,
                (float)((double)amount / top),
                ColorOf(stat.JobId),
                IconId: stat.IsLimitBreak ? LimitBreakIcon : 0));
        }

        return rows;
    }

    /// <summary>"+12.4%" or "-3.0%": how a rate changed; empty when there is nothing to compare with.</summary>
    private static string Delta(double now, double before)
        => before <= 0 ? string.Empty : ((now / before - 1) * 100).ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + "%";

    private static string Length(TimeSpan span)
        => $"{(int)span.TotalMinutes}:{span.Seconds:00}";

    private void ShowSide(DamageMeterNode node)
    {
        if (_historyPanel is { } history)
        {
            history.Open = _side != SideView.None;
            history.SetLayout(HistoryWidth, true);
            switch (_side)
            {
                case SideView.History:
                    ShowHistory(history);
                    break;
                case SideView.Detail:
                    ShowDetail(history);
                    break;
                case SideView.Breakdown:
                    ShowBreakdown(history);
                    break;
            }
        }

        ShowExtra();
    }

    /// <summary>
    /// The extra table: critical and direct hit rates, deaths and dispels. Docked, its lines are those of the damage table,
    /// side by side, so it has no names; free, it has its own and lists everyone.
    /// </summary>
    private void ShowExtra()
    {
        if (_extra is not { } panel) return;

        var showing = _extraOpen || Previewing;
        panel.Open = showing;
        if (!showing) return;

        var free = Settings.ExtraDetached;
        panel.Free = free;
        panel.SetLayout(free ? ExtraFreeWidth : ExtraDockedWidth, free);
        panel.SetColumns(["CH%", "DH%", "CDH%", T("Column.Deaths"), T("Column.Dispels")]);
        panel.SetPinTooltip(free ? T("Tooltip.PinOn") : T("Tooltip.PinOff"));

        static string Percent(int part, int whole)
            => whole == 0 ? "-" : (part * 100d / whole).ToString("0.#", CultureInfo.InvariantCulture) + "%";

        var rows = new List<DamageMeterRow>();
        if (Previewing)
        {
            // Docked, the lines are those of the damage table side by side, in the same order; free, everyone is listed.
            var players = free
                ? [.. Samples.OrderByDescending(sample => sample.Damage)]
                : SamplePlayers(Settings.Mode);
            foreach (var sample in players)
                rows.Add(new DamageMeterRow(free ? (sample.Group == (char)0 ? T("LimitBreak") : sample.Name) : string.Empty, free ? sample.JobId : 0,
                    string.Empty, 0f, ColorOf(sample.JobId) with { W = 0f }, sample.Hits,
                    free && sample.Group == (char)0 ? LimitBreakIcon : 0));
        }
        else if (_fight is { } current)
        {
            IEnumerable<Stat> stats = free
                ? current.Players.Values.Where(stat => stat.Hits > 0 || stat.Deaths > 0 || stat.Dispels > 0).OrderByDescending(stat => stat.Damage).Take(MaxRows)
                : _mainIds.Where(current.Players.ContainsKey).Select(id => current.Players[id]);

            foreach (var stat in stats)
                rows.Add(new DamageMeterRow(free ? (stat.IsLimitBreak ? T("LimitBreak") : stat.Name) : string.Empty, free ? stat.JobId : 0,
                    string.Empty, 0f, ColorOf(stat.JobId) with { W = 0f },
                    [Percent(stat.Crits, stat.Hits), Percent(stat.DirectHits, stat.Hits), Percent(stat.CritDirectHits, stat.Hits), stat.Deaths.ToString(), stat.Dispels.ToString()],
                    free && stat.IsLimitBreak ? LimitBreakIcon : 0));
        }

        panel.SetRows(rows, false);
    }

    /// <summary>The fights of this session, the last one first; clicking one opens its details.</summary>
    private void ShowHistory(SidePanelNode node)
    {
        node.SetHeader(T("Side.History"), false);

        // Each fight is summed once: the total, the rate and the boss do not change twice in one refresh.
        var totals = _history.ConvertAll(fight => fight.TotalDamage);
        var rates  = _history.ConvertAll(fight => fight.TotalDamage / SecondsOf(fight));
        var best   = rates.Count == 0 ? 1d : rates.Max();
        var rows   = new List<DamageMeterRow>();
        for (var i = _history.Count - 1; i >= 0 && rows.Count < MaxRows; i--)
        {
            var fight = _history[i];
            var rate  = rates[i];
            var boss  = fight.Boss.Length > 0 ? fight.Boss : T("Side.Unknown");
            rows.Add(new DamageMeterRow($"#{fight.Number} {boss}", 0, $"{Length(fight.Length)} · {Short(totals[i])} ({Short(rate)})",
                (float)(rate / Math.Max(1d, best)), OtherColor));
        }

        node.SetRows(rows, true);
    }

    /// <summary>One past fight, with each player's rate compared to the last time against the same boss.</summary>
    private void ShowDetail(SidePanelNode node)
    {
        if (_detail is not { } fight)
        {
            SetSide(SideView.History);
            return;
        }

        var before = fight.Boss.Length == 0 ? null : _history.LastOrDefault(other => other.Number < fight.Number && other.Boss == fight.Boss);
        var mode   = Settings.Mode;
        var title  = $"#{fight.Number} {(fight.Boss.Length > 0 ? fight.Boss : T("Side.Unknown"))} · {Length(fight.Length)}";
        if (before is not null)
        {
            var now = fight.Players.Values.Sum(stat => AmountOf(stat, mode)) / SecondsOf(fight);
            var old = before.Players.Values.Sum(stat => AmountOf(stat, mode)) / SecondsOf(before);
            title += $" · {T("Side.Versus")} #{before.Number} {Delta(now, old)}";
        }

        node.SetHeader($"< {title}", true);
        node.SetRows(RowsOf(fight, mode, MaxRows, before), false);
    }

    /// <summary>
    /// The damage of one player of the fight by action, the biggest first: each with its icon, the total, how many hits
    /// and its share of the player's damage. Damage over time is one line. A click on the title closes it.
    /// </summary>
    private void ShowBreakdown(SidePanelNode node)
    {
        if (!Settings.BreakdownOnClick || _fight is not { } fight || !fight.Players.TryGetValue(_breakdownId, out var stat))
        {
            SetSide(SideView.None);
            return;
        }

        var name = stat.IsLimitBreak ? T("LimitBreak") : stat.Name;
        node.SetHeader($"< {name} · {T("Side.Breakdown")}", true);

        var actions = stat.Actions.Where(pair => pair.Value.Damage > 0).OrderByDescending(pair => pair.Value.Damage).ToList();
        var total   = actions.Sum(pair => pair.Value.Damage);
        var rows    = new List<DamageMeterRow>();
        if (total > 0)
        {
            var top   = actions[0].Value.Damage;
            var sheet = Svc.Data.GetExcelSheet<LuminaAction>();
            foreach (var (actionId, (damage, hits)) in actions.Take(MaxRows))
            {
                var row   = actionId is TickAction or 0 ? null : sheet?.GetRowOrDefault(actionId);
                var label = actionId == TickAction ? T("Side.Dot")
                          : row?.Name.ExtractText() is { Length: > 0 } actionName
                              ? row.Value.ActionCategory.RowId == 1 ? $"{actionName} ({T("Side.Auto")})" : actionName
                              : "?";
                var share = (damage * 100d / total).ToString("0.0", CultureInfo.InvariantCulture);

                rows.Add(new DamageMeterRow(label, stat.JobId, $"{Short(damage)} ({hits}x, {share}%)",
                    (float)((double)damage / top), ColorOf(stat.JobId), IconId: row?.Icon ?? 0u));
            }
        }

        node.SetRows(rows, false);
    }

    /// <summary>1.55M, 320.93M, 12.4K: the way damage meters write big numbers.</summary>
    private static string Short(double value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000     => (value / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 10_000        => (value / 1_000).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _                => ((long)value).ToString(CultureInfo.InvariantCulture),
    };

    private Vector4 ColorOf(uint jobId)
    {
        if (_jobColors.TryGetValue(jobId, out var color)) return color;

        color = OtherColor;
        if (Svc.Data.GetExcelSheet<LuminaClassJob>().TryGetRow(jobId, out var job))
        {
            var abbreviation = job.Abbreviation.ExtractText();
            color = job.Role switch
            {
                1 => TankColor,
                4 => HealerColor,
                2 => MeleeColor,
                3 => abbreviation is "BRD" or "MCH" or "DNC" or "ARC" ? RangedColor : CasterColor,
                _ => OtherColor,
            };
        }

        return _jobColors[jobId] = color;
    }
}
