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
    }

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

    // Auto Guard: used once per danger.
    private bool _autoGuardDone;

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
        ],
    };

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
            var canGuard = danger && GuardReady(out _guard);
            _active = canGuard && Settings.SwapButtons;

            if (!danger)
            {
                _blockLogged = false;
                _autoGuardDone = false;
            }
            else if (!canGuard && !_blockLogged)
            {
                _blockLogged = true;
                Svc.Log.Info($"GoblinAntiMachinist: in danger but Guard cannot be offered: {_block}.");
                if (Settings.WarnNoGuard) Warn(T("Warn.NoGuard"));
            }

            if (danger && Settings.AutoGuard && !_autoGuardDone) TryAutoGuard();

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

    /// <summary>Uses Guard by itself, the moment it can be used; tried again every frame while the danger lasts.</summary>
    private void TryAutoGuard()
    {
        var actions = ActionManager.Instance();
        if (actions == null || _adjustHook == null) return;

        foreach (var id in GuardIds(actions))
        {
            // Every check this time, cooldown and animation lock included: only a Guard that goes off now.
            if (actions->GetActionStatus(ActionType.Action, id) != 0) continue;
            if (!actions->UseAction(ActionType.Action, id, SelfTarget)) continue;

            _autoGuardDone = true;
            Svc.Log.Info($"GoblinAntiMachinist: used Guard {id} by itself.");
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
            if (obj == null || obj == player || obj->ObjectKind != ObjectKind.Pc) continue;

            var character = (Character*)obj;
            if (!((BattleChara*)obj)->IsHostile || character->CharacterData.ClassJob != MachinistJob) continue;
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
