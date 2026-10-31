using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;
using GoblinTweaks.UI.Nodes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// PvP (Crystalline Conflict, Frontline, Rival Wings, Wolves' Den duels): a Guard icon over every enemy, bright while they have it and with the clock hand and
/// the seconds left while it is on cooldown.
/// </summary>
/// <remarks>
/// The game never tells a client the cooldowns of other players. What it does tell is the statuses of everyone
/// around, so a Guard is seen when its status appears on an enemy, and its cooldown is counted from the moment
/// it was used. An enemy never seen using it shows it ready. No hooks: statuses are read once a frame.
/// </remarks>
[Tweak(TweakCategory.PvP)]
public sealed unsafe class GoblinEnemyGuard : Tweak<GoblinEnemyGuard.Options>
{
    public sealed class Options
    {
        /// <summary>The Guard icon over every enemy, in every PvP area.</summary>
        public bool ShowIcons { get; set; } = true;

        /// <summary>Warns when you use your Limit Break on a target that has Guard.</summary>
        public bool WarnLimitBreak { get; set; } = true;

        /// <summary>Stops your Limit Break on a target that has Guard; pressing it again soon after uses it anyway.</summary>
        public bool BlockLimitBreak { get; set; }

        /// <summary>The warning also goes to the chat, besides the screen.</summary>
        public bool WarnInChat { get; set; }
    }

    // Limit Break, in the ActionCategory sheet.
    private const uint LimitBreakCategory = 9;

    // A Limit Break stopped is let through if pressed again within this time.
    private static readonly TimeSpan PressAgainWindow = TimeSpan.FromSeconds(2);

    private Hook<ActionManager.Delegates.UseAction>? _useActionHook;
    private SimpleSettingsAddon? _settingsAddon;
    private readonly Dictionary<uint, bool> _isLimitBreak = [];
    private uint _stoppedAction;
    private DateTime _stoppedAt = DateTime.MinValue;

    private const uint GuardIcon = 9581;          // the icon of the Guard action
    private const float GuardRecast = 30f;
    private const float GuardDuration = 4f;       // how long the status lasts when nothing ends it early
    private static readonly uint[] GuardStatuses = [3054, 3673];

    private const float IconSize = 36f;
    private const float LiftAboveHead = 56f;      // over the name plate, in pixels

    private OverlayNodes? _overlay;

    // Reused from frame to frame and match to match: the n-th enemy on the list takes the n-th node.
    private readonly List<EnemyGuardNode> _nodes = [];
    private int _used;

    /// <summary>When each enemy last used Guard (entity id).</summary>
    private readonly Dictionary<uint, DateTime> _usedAt = [];

    /// <summary>The enemies that had the Guard status last frame, to see it start.</summary>
    private readonly HashSet<uint> _guarding = [];
    private readonly HashSet<uint> _guardingNow = [];
    private readonly HashSet<uint> _seen = [];

    private ushort _territory;
    private bool _inMatch;
    private int _mostSeen;

    protected internal override void Enable()
    {
        var useAction = ActionManager.Addresses.UseAction.Value;
        if (useAction == 0)
            throw new InvalidOperationException("The game function for using actions was not found (game updated?).");

        _overlay = new OverlayNodes();
        _territory = 0;
        _inMatch = false;
        _stoppedAction = 0;

        _useActionHook = Svc.GameInterop.HookFromAddress<ActionManager.Delegates.UseAction>((nint)useAction, OnUseAction);
        _useActionHook.Enable();

        _settingsAddon = NewSettingsAddon();
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;

        _useActionHook?.Dispose();
        _useActionHook = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;

        // Takes its nodes off the shared overlay and frees them; the overlay itself stays.
        _overlay?.Dispose();
        _overlay = null;
        _nodes.Clear();
        _usedAt.Clear();
        _guarding.Clear();
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
        InternalName    = "GtkEnemyGuardSettings",
        Title           = $"{T("Name")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size            = new System.Numerics.Vector2(580f, 380f),
        RespectCloseAll = false,
        Tweak           = this,
        Heading         = T("Settings.Heading"),
        Toggles         = () =>
        [
            Option("ShowIcons",       () => Settings.ShowIcons,       on => Settings.ShowIcons       = on),
            Option("WarnLimitBreak",  () => Settings.WarnLimitBreak,  on => Settings.WarnLimitBreak  = on),
            Option("BlockLimitBreak", () => Settings.BlockLimitBreak, on => Settings.BlockLimitBreak = on),
            Option("WarnInChat",      () => Settings.WarnInChat,      on => Settings.WarnInChat      = on),
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

    /// <summary>
    /// The game using an action (a button pressed, a macro). Runs inside the game's own code: it must never throw.
    /// A Limit Break on a target that has Guard is warned about, and stopped when asked, unless pressed again soon.
    /// </summary>
    private bool OnUseAction(ActionManager* actions, ActionType type, uint actionId, ulong targetId, uint extraParam,
        ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        try
        {
            if ((Settings.WarnLimitBreak || Settings.BlockLimitBreak) && type == ActionType.Action
                && Svc.ClientState.IsPvP && IsLimitBreak(actionId) && TargetHasGuard(targetId) is { } name)
            {
                var now = DateTime.UtcNow;
                var pressedAgain = _stoppedAction == actionId && now - _stoppedAt < PressAgainWindow;

                if (Settings.BlockLimitBreak && !pressedAgain)
                {
                    _stoppedAction = actionId;
                    _stoppedAt     = now;
                    Warn(string.Format(T("Warn.LimitBreakStopped"), name));
                    Svc.Log.Info($"GoblinEnemyGuard: stopped Limit Break {actionId} on {name}, who has Guard.");
                    return false;
                }

                _stoppedAction = 0;
                if (Settings.WarnLimitBreak && !pressedAgain) Warn(string.Format(T("Warn.LimitBreak"), name));
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, "GoblinEnemyGuard: checking a Limit Break failed; the action goes ahead.");
        }

        return _useActionHook!.Original(actions, type, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    private bool IsLimitBreak(uint actionId)
    {
        if (!_isLimitBreak.TryGetValue(actionId, out var result))
        {
            result = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Action>().TryGetRow(actionId, out var row)
                && row.IsPvP && row.ActionCategory.RowId == LimitBreakCategory;
            _isLimitBreak[actionId] = result;
        }

        return result;
    }

    /// <summary>The name of the enemy the action goes to, if it has Guard; null otherwise.</summary>
    private static string? TargetHasGuard(ulong targetId)
    {
        // A button aims at the current target; a macro or a call may name one.
        var target = targetId is 0 or 0xE0000000
            ? TargetSystem.Instance() is var system && system != null ? system->GetTargetObject() : null
            : GameObjectManager.Instance() is var manager && manager != null ? manager->Objects.GetObjectByGameObjectId(targetId) : null;
        if (target == null || target->ObjectKind != ObjectKind.Pc || !((BattleChara*)target)->IsHostile) return null;

        var statuses = ((BattleChara*)target)->GetStatusManager();
        if (statuses == null) return null;

        foreach (ref readonly var status in statuses->Status)
        {
            if (GuardStatuses.Contains(status.StatusId))
                return ((Character*)target)->NameString;
        }

        return null;
    }

    /// <summary>A warning on screen, and in the chat too when asked for.</summary>
    private void Warn(string text)
    {
        Svc.Toasts.ShowError(text);
        if (Settings.WarnInChat) Svc.Chat.Print(text, Name);
    }

    protected override IReadOnlyList<string> CommandNames => ["/geg", "/genemyguard"];

    protected override void OnCommand() => ToggleHelpWindow();

    private void OnUpdate(IFramework _)
    {
        try
        {
            CheckTerritory();
            if (_inMatch && Settings.ShowIcons) Track(); else HideAll();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>
    /// Every PvP area: Crystalline Conflict, Frontline, Rival Wings and the Wolves' Den Pier, where duels are fought
    /// (Dalamud counts it as PvP too). Only hostile players get an icon, so on the pier that is the one you duel.
    /// A new zone starts with no Guard seen: the enemies of the last match are not the ones of this one.
    /// </summary>
    private void CheckTerritory()
    {
        _inMatch = Svc.ClientState.IsPvP;

        var territory = (ushort)Svc.ClientState.TerritoryType;
        if (territory == _territory) return;

        _territory = territory;
        Svc.Log.Info($"GoblinEnemyGuard: zone {territory}, PvP {_inMatch}.");
        _mostSeen = 0;
        _usedAt.Clear();
        _guarding.Clear();
        HideAll();
    }

    private void Track()
    {
        var player = (GameObject*)Control.GetLocalPlayer();
        var manager = GameObjectManager.Instance();
        if (player == null || manager == null || _overlay == null)
        {
            HideAll();
            return;
        }

        var now = DateTime.UtcNow;
        _guardingNow.Clear();
        _seen.Clear();
        _used = 0;

        foreach (var entry in manager->Objects.IndexSorted)
        {
            var obj = entry.Value;
            if (obj == null || obj == player || obj->ObjectKind != ObjectKind.Pc) continue;

            var enemy = (BattleChara*)obj;
            if (!enemy->IsHostile) continue;

            var id = obj->EntityId;
            _seen.Add(id);
            WatchGuard(enemy, id, player, now);
            Place(enemy, id, now);
        }

        _guarding.Clear();
        _guarding.UnionWith(_guardingNow);

        for (var i = _used; i < _nodes.Count; i++)
            _nodes[i].IsVisible = false;

        // To see whether the client keeps every enemy loaded, wherever they are on the map.
        if (_seen.Count > _mostSeen)
        {
            _mostSeen = _seen.Count;
            Svc.Log.Info($"GoblinEnemyGuard: {_mostSeen} enemies loaded at once in this match so far.");
        }
    }

    /// <summary>The moment the Guard status appears on an enemy, their cooldown starts.</summary>
    private void WatchGuard(BattleChara* enemy, uint id, GameObject* player, DateTime now)
    {
        var statuses = enemy->GetStatusManager();
        if (statuses == null) return;

        foreach (ref readonly var status in statuses->Status)
        {
            if (!GuardStatuses.Contains(status.StatusId)) continue;

            _guardingNow.Add(id);
            if (_guarding.Contains(id)) return;

            // Seen late (they were out of sight), it started before now by what has already passed.
            var passed = Math.Clamp(GuardDuration - status.RemainingTime, 0f, GuardDuration);
            _usedAt[id] = now - TimeSpan.FromSeconds(passed);

            var distance = Vector3.Distance(((GameObject*)enemy)->Position, player->Position);
            Svc.Log.Info($"GoblinEnemyGuard: {((Character*)enemy)->NameString} used Guard {distance:0} yalms away.");
            return;
        }
    }

    /// <summary>Puts the icon over the head of an enemy on screen, ready or on cooldown.</summary>
    private void Place(BattleChara* enemy, uint id, DateTime now)
    {
        var obj = (GameObject*)enemy;
        var head = (Vector3)obj->Position + new Vector3(0f, obj->GetHeight(), 0f);
        if (((Character*)enemy)->IsDead() || !Svc.GameGui.WorldToScreen(head, out var screen)) return;

        var node = NextNode();

        node.Position = screen - new Vector2(IconSize / 2f, IconSize + LiftAboveHead);

        var passed = _usedAt.TryGetValue(id, out var usedAt) ? (float)(now - usedAt).TotalSeconds : GuardRecast;
        if (passed >= GuardRecast) node.ShowReady();
        else node.ShowCooldown(passed / GuardRecast, GuardRecast - passed);

        node.IsVisible = true;
    }

    private EnemyGuardNode NextNode()
    {
        if (_used == _nodes.Count)
        {
            var node = new EnemyGuardNode(GuardIcon, IconSize);
            _overlay!.AddNode(node);
            _nodes.Add(node);
        }

        return _nodes[_used++];
    }

    private void HideAll()
    {
        foreach (var node in _nodes)
            node.IsVisible = false;
    }
}
