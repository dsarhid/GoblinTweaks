using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// PvP: while an enemy machinist's Marksman's Spite is on its way to you, every action button shows and
/// uses Guard, so whatever you press in that second and a half saves you.
/// </summary>
/// <remarks>
/// How the danger is seen is in <see cref="MarksmansSpiteWatch"/>. The buttons are not edited: the game asks
/// which action each one stands for every frame, and the answer is Guard while the danger lasts, so the
/// hotbars saved on the server never change.
/// </remarks>
[Tweak(TweakCategory.PvP)]
public sealed unsafe class GoblinAntiMachinist : Tweak
{
    private const uint Guard = 29054;
    // A second Guard of the game (2.5 s recast), used where the usual one is missing.
    private const uint OtherGuard = 29735;

    // Buttons change only if Guard is ready, or will be within this time.
    private const float MaxGuardWait = 3f;

    private readonly MarksmansSpiteWatch _watch = new(nameof(GoblinAntiMachinist));
    private Hook<ActionManager.Delegates.GetAdjustedActionId>? _adjustHook;
    private Hook<ActionEffectHandler.Delegates.Receive>? _effectHook;
    private Exception? _failure;

    // Read by the button hook many times a frame: worked out once per frame in OnUpdate.
    private bool _active;
    private uint _guard;

    // Why Guard could not be offered, logged once per danger.
    private string _block = string.Empty;
    private bool _blockLogged;

    protected internal override void Enable()
    {
        var adjust = ActionManager.Addresses.GetAdjustedActionId.Value;
        var receive = ActionEffectHandler.Addresses.Receive.Value;
        if (adjust == 0 || receive == 0)
            throw new InvalidOperationException("The game functions for action buttons or action effects were not found (game updated?).");

        _failure = null;
        _active = false;
        _watch.Reset();

        _adjustHook = Svc.GameInterop.HookFromAddress<ActionManager.Delegates.GetAdjustedActionId>((nint)adjust, OnGetAdjustedActionId);
        _effectHook = Svc.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>((nint)receive, OnActionEffect);
        _adjustHook.Enable();
        _effectHook.Enable();

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
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

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
            _active = danger && GuardReady(out _guard);

            if (!danger) _blockLogged = false;
            else if (!_active && !_blockLogged)
            {
                _blockLogged = true;
                Svc.Log.Info($"GoblinAntiMachinist: in danger but the buttons stay as they are: {_block}.");
            }
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
        foreach (var id in (ReadOnlySpan<uint>)[_adjustHook.Original(actions, Guard), OtherGuard])
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
