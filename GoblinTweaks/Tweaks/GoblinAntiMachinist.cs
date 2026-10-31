using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// PvP: while an enemy machinist's Marksman's Spite is on its way to you, every action button shows and
/// uses Guard, so whatever you press in that second and a half saves you. Optionally uses Guard by itself,
/// and warns when a machinist targets you or when Guard cannot save you.
/// </summary>
/// <remarks>
/// How the danger is seen is in <see cref="MarksmansSpiteWatch"/>. The buttons are not edited: the game asks
/// which action each one stands for every frame, and the answer is Guard while the danger lasts, so the
/// hotbars saved on the server never change.
/// </remarks>
[Tweak(TweakCategory.PvP)]
public sealed unsafe class GoblinAntiMachinist : Tweak<GoblinAntiMachinist.Options>
{
    public sealed class Options
    {
        /// <summary>Every action button shows and uses Guard while the danger lasts.</summary>
        public bool SwapButtons { get; set; } = true;

        /// <summary>Uses Guard by itself as soon as the danger is seen. Off by default: it acts without a key press.</summary>
        public bool AutoGuard { get; set; }

        /// <summary>Warns when an enemy machinist targets you.</summary>
        public bool WarnTargeted { get; set; } = true;

        /// <summary>Warns when Marksman's Spite is on its way and Guard cannot be used in time.</summary>
        public bool WarnNoGuard { get; set; } = true;

        /// <summary>The warnings also go to the chat, besides the screen.</summary>
        public bool WarnInChat { get; set; }

        /// <summary>Uses Purify by itself when a Stun, Deep Freeze, Miracle of Nature or Silence is put on you. Off by default.</summary>
        public bool AutoPurify { get; set; }

        /// <summary>Purify by itself for Heavy and Bind too, which only slow or stop your movement.</summary>
        public bool PurifyMovement { get; set; }
    }

    private const uint Purify = 29056;

    // Controls Purify removes, by their English names (read in English whatever the client's language): those that
    // stop every action, Silence, and those that only slow or stop movement.
    private static readonly string[] BlockingNames = ["Stun", "Deep Freeze", "Miracle of Nature"];
    private static readonly string[] SilenceNames  = ["Silence"];
    private static readonly string[] MovementNames = ["Heavy", "Bind"];

    // Purify is not tried again for this long after asking for it: the game may still be using it.
    private static readonly TimeSpan PurifyRetry = TimeSpan.FromMilliseconds(300);

    private HashSet<uint> _blocking = [];
    private HashSet<uint> _silence = [];
    private HashSet<uint> _movement = [];
    private DateTime _nextPurify = DateTime.MinValue;

    private const uint Guard = 29054;
    // A second Guard of the game (2.5 s recast), used where the usual one is missing.
    private const uint OtherGuard = 29735;

    private const byte MachinistJob = 31;

    // Buttons change only if Guard is ready, or will be within this time.
    private const float MaxGuardWait = 3f;

    // Who is targeting you is looked at this often, and the same machinist is not warned about again for a while.
    private static readonly TimeSpan TargetScanInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TargetWarnAgain    = TimeSpan.FromSeconds(15);

    // UseAction's target for "yourself".
    private const ulong SelfTarget = 0xE0000000;

    private readonly MarksmansSpiteWatch _watch = new(nameof(GoblinAntiMachinist));
    private Hook<ActionManager.Delegates.GetAdjustedActionId>? _adjustHook;
    private Hook<ActionEffectHandler.Delegates.Receive>? _effectHook;
    private SimpleSettingsAddon? _settingsAddon;
    private Exception? _failure;

    // Read by the button hook many times a frame: worked out once per frame in OnUpdate.
    private bool _active;
    private uint _guard;

    // Why Guard could not be offered, logged (and warned) once per danger.
    private string _block = string.Empty;
    private bool _blockLogged;

    // Auto Guard: done once its status is on you, asked for again until then.
    private bool _autoGuardDone;
    private int _guardAsks;
    private DateTime _nextGuard = DateTime.MinValue;
    private static readonly TimeSpan GuardRetry = TimeSpan.FromMilliseconds(150);

    // Machinists targeting you, and when each was last warned about.
    private DateTime _nextTargetScan = DateTime.MinValue;
    private readonly HashSet<uint> _targeting = [];
    private readonly HashSet<uint> _targetingNow = [];
    private readonly Dictionary<uint, DateTime> _warnedAt = [];

    protected internal override void Enable()
    {
        var adjust = ActionManager.Addresses.GetAdjustedActionId.Value;
        var receive = ActionEffectHandler.Addresses.Receive.Value;
        if (adjust == 0 || receive == 0)
            throw new InvalidOperationException("The game functions for action buttons or action effects were not found (game updated?).");

        _failure = null;
        _active = false;
        _autoGuardDone = false;
        _blocking = StatusIds(BlockingNames);
        _silence  = StatusIds(SilenceNames);
        _movement = StatusIds(MovementNames);
        _targeting.Clear();
        _warnedAt.Clear();
        _watch.Reset();

        _adjustHook = Svc.GameInterop.HookFromAddress<ActionManager.Delegates.GetAdjustedActionId>((nint)adjust, OnGetAdjustedActionId);
        _effectHook = Svc.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>((nint)receive, OnActionEffect);
        _adjustHook.Enable();
        _effectHook.Enable();

        _settingsAddon = NewSettingsAddon();
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;

        _active = false;
        _adjustHook?.Dispose();
        _adjustHook = null;
        _effectHook?.Dispose();
        _effectHook = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(Loc.Get("Window.Settings"), TMain("Settings.Help"), ToggleSettings),
    ];

    private void ToggleSettings()
    {
        if (_settingsAddon is not { } window) return;
        if (window.IsOpen) window.Close(); else window.Open();
    }

    private SimpleSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkAntiMachinistSettings",
        Title           = $"{T("Name")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size            = new System.Numerics.Vector2(580f, 420f),
        RespectCloseAll = false,
        Tweak           = this,
        Heading         = T("Settings.Heading"),
        Toggles         = () =>
        [
            Option("SwapButtons",  () => Settings.SwapButtons,  on => Settings.SwapButtons  = on),
            Option("AutoGuard",    () => Settings.AutoGuard,    on => Settings.AutoGuard    = on),
            Option("WarnTargeted", () => Settings.WarnTargeted, on => Settings.WarnTargeted = on),
            Option("WarnNoGuard",  () => Settings.WarnNoGuard,  on => Settings.WarnNoGuard  = on),
            Option("WarnInChat",   () => Settings.WarnInChat,   on => Settings.WarnInChat   = on),
            Option("AutoPurify",     () => Settings.AutoPurify,     on => Settings.AutoPurify     = on),
            Option("PurifyMovement", () => Settings.PurifyMovement, on => Settings.PurifyMovement = on),
        ],
        HelpClick       = ToggleHelpWindow,
        HelpTooltip     = Loc.GetIn(UiLanguage, "Window.Help"),
    };

    /// <summary>Every harmful status with one of these English names: the game has many of each (PvE, PvP, modes).</summary>
    private static HashSet<uint> StatusIds(string[] names)
        => [.. Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Status>(Dalamud.Game.ClientLanguage.English)
            .Where(status => status.StatusCategory == 2 && names.Contains(status.Name.ToString()))
            .Select(status => status.RowId)];

    /// <summary>The controls Purify removes that are on you now: those that stop every action, and any it is asked to remove.</summary>
    private (bool Blocking, bool ToPurify) ControlsOn(BattleChara* player)
    {
        var statuses = player->GetStatusManager();
        if (statuses == null) return (false, false);

        bool blocking = false, toPurify = false;
        foreach (ref readonly var status in statuses->Status)
        {
            var id = status.StatusId;
            if (id == 0) continue;

            if (_blocking.Contains(id)) blocking = toPurify = true;
            else if (_silence.Contains(id) || (Settings.PurifyMovement && _movement.Contains(id))) toPurify = true;
        }

        return (blocking, toPurify);
    }

    /// <summary>Uses Purify by itself, if it can go off now.</summary>
    private void TryPurify(string why)
    {
        var now = DateTime.UtcNow;
        var actions = ActionManager.Instance();
        if (now < _nextPurify || actions == null) return;
        if (actions->GetActionStatus(ActionType.Action, Purify) != 0) return;

        _nextPurify = now + PurifyRetry;
        if (actions->UseAction(ActionType.Action, Purify, SelfTarget))
            Svc.Log.Info($"GoblinAntiMachinist: used Purify by itself ({why}).");
    }

    private TweakToggle Option(string key, Func<bool> get, Action<bool> set)
        => new(T(key), T(key + ".Help"), get, on =>
        {
            set(on);
            SaveSettings();
        });

    protected override void OnLanguageChanged()
    {
        if (State == TweakState.Enabled) Rebuild(ref _settingsAddon, NewSettingsAddon);
    }

    protected override IReadOnlyList<string> CommandNames => ["/gam", "/gamachinist"];

    protected override void OnCommand() => ToggleHelpWindow();

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
            _watch.Update();
            var danger = _watch.Incoming;

            // Guard already on (used by you or by this tweak): nothing to offer and nothing to warn about, even
            // though Guard itself is now on cooldown. Guard used by itself only counts once its status is on you:
            // the game can accept the request and still not use it (while stunned), which let a Spite through.
            var player = (BattleChara*)Control.GetLocalPlayer();
            var hasGuard = PvP.HasGuard((GameObject*)player);
            if (danger && hasGuard && !_autoGuardDone)
            {
                _autoGuardDone = true;
                if (_guardAsks > 0) Svc.Log.Info($"GoblinAntiMachinist: Guard is on, after asking for it {_guardAsks} time(s).");
            }

            var guarded = danger && (_autoGuardDone || hasGuard);
            var canGuard = danger && !guarded && GuardReady(out _guard);
            _active = canGuard && Settings.SwapButtons;

            if (!danger)
            {
                _blockLogged = false;
                _autoGuardDone = false;
                _guardAsks = 0;
            }
            else if (guarded)
            {
                _blockLogged = true;
            }
            else if (!canGuard && !_blockLogged)
            {
                _blockLogged = true;
                Svc.Log.Info($"GoblinAntiMachinist: in danger but Guard cannot be offered: {_block}.");
                if (Settings.WarnNoGuard) Warn(T("Warn.NoGuard"));
            }

            // Controls: with Marksman's Spite on its way and Guard to be used by itself, a control that stops Guard is
            // removed first, at once; otherwise Purify by itself, when asked for.
            var (blocking, toPurify) = Svc.ClientState.IsPvP && player != null ? ControlsOn(player) : (false, false);
            if (danger && Settings.AutoGuard && !_autoGuardDone && blocking) TryPurify("before Guard, against Marksman's Spite");
            else if (Settings.AutoPurify && toPurify) TryPurify("control on you");

            // Not while a control stops it: the request would be taken and dropped. Purify goes first.
            if (danger && Settings.AutoGuard && !_autoGuardDone && !blocking) TryAutoGuard();

            if (Settings.WarnTargeted) WatchTargeting();
        }
        catch (Exception ex)
        {
            _active = false;
            ReportFailure(ex);
        }
    }

    /// <summary>The Guard this mode has, if it can be used now or within <see cref="MaxGuardWait"/>.</summary>
    private bool GuardReady(out uint guard)
    {
        guard = 0;
        _block = "the game is not ready";
        var actions = ActionManager.Instance();
        if (actions == null || _adjustHook == null) return false;

        var statuses = new List<string>(2);
        foreach (var id in GuardIds(actions))
        {
            // Without the cooldown: 0 means this mode has it and nothing else stops it (stun, mech...).
            var status = actions->GetActionStatus(ActionType.Action, id, checkRecastActive: false, checkCastingActive: false);
            if (status != 0)
            {
                statuses.Add($"Guard {id} status {status}");
                continue;
            }

            var wait = actions->IsRecastTimerActive(ActionType.Action, id)
                ? actions->GetRecastTime(ActionType.Action, id) - actions->GetRecastTimeElapsed(ActionType.Action, id)
                : 0f;
            if (wait >= MaxGuardWait)
            {
                _block = $"Guard {id} has {wait:0.0} s of cooldown left";
                return false;
            }

            guard = id;
            return true;
        }

        _block = $"no Guard can be used ({string.Join(", ", statuses)})";
        return false;
    }

    private ReadOnlySpan<uint> GuardIds(ActionManager* actions) => (uint[])[_adjustHook!.Original(actions, Guard), OtherGuard];

    /// <summary>
    /// Asks for Guard by itself, the moment it can be used, and again every <see cref="GuardRetry"/> until its status
    /// is on you or the danger is over.
    /// </summary>
    private void TryAutoGuard()
    {
        var now = DateTime.UtcNow;
        var actions = ActionManager.Instance();
        if (now < _nextGuard || actions == null || _adjustHook == null) return;

        foreach (var id in GuardIds(actions))
        {
            // Every check this time, cooldown and animation lock included: only a Guard that goes off now.
            if (actions->GetActionStatus(ActionType.Action, id) != 0) continue;

            _nextGuard = now + GuardRetry;
            if (!actions->UseAction(ActionType.Action, id, SelfTarget)) continue;

            if (++_guardAsks == 1) Svc.Log.Info($"GoblinAntiMachinist: asked for Guard {id} by itself.");
            return;
        }
    }

    /// <summary>Warns once when an enemy machinist starts targeting you, and not again for the same one for a while.</summary>
    private void WatchTargeting()
    {
        var now = DateTime.UtcNow;
        if (now < _nextTargetScan) return;
        _nextTargetScan = now + TargetScanInterval;

        var player = (GameObject*)Control.GetLocalPlayer();
        var manager = GameObjectManager.Instance();
        if (!Svc.ClientState.IsPvP || player == null || manager == null)
        {
            _targeting.Clear();
            return;
        }

        _targetingNow.Clear();
        foreach (var entry in manager->Objects.IndexSorted)
        {
            var obj = entry.Value;
            if (obj == null || obj == player || !PvP.IsEnemy(obj)) continue;

            var character = (Character*)obj;
            if (character->CharacterData.ClassJob != MachinistJob) continue;
            if (character->TargetId.ObjectId != player->EntityId) continue;

            var id = obj->EntityId;
            _targetingNow.Add(id);
            if (_targeting.Contains(id) || (_warnedAt.TryGetValue(id, out var last) && now - last < TargetWarnAgain)) continue;

            _warnedAt[id] = now;
            Warn(string.Format(T("Warn.Targeted"), character->NameString));
        }

        _targeting.Clear();
        _targeting.UnionWith(_targetingNow);
    }

    /// <summary>A warning on screen, and in the chat too when asked for.</summary>
    private void Warn(string text)
    {
        Svc.Toasts.ShowError(text);
        if (Settings.WarnInChat) Svc.Chat.Print(text, Name);
    }

    /// <summary>
    /// The game asking which action a button stands for. Runs inside the game's own code, many times a frame: it must never throw.
    /// </summary>
    private uint OnGetAdjustedActionId(ActionManager* actions, uint actionId)
    {
        var adjusted = _adjustHook!.Original(actions, actionId);
        return _active && _guard != 0 ? _guard : adjusted;
    }

    /// <summary>An action resolved by the server reaching the client. Runs inside the game's own code: it must never throw.</summary>
    private void OnActionEffect(uint casterEntityId, Character* caster, System.Numerics.Vector3* targetPos, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            _watch.OnActionEffect(casterEntityId, caster, header, targetEntityIds);
        }
        catch (Exception ex)
        {
            // Reported from OnUpdate: disabling the tweak here would free the hook that is running.
            _failure ??= ex;
        }

        _effectHook!.Original(casterEntityId, caster, targetPos, header, effects, targetEntityIds);
    }
}
