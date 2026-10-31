using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;
using GoblinTweaks.UI.Nodes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// PvP (Crystalline Conflict, Frontline, Rival Wings, Wolves' Den duels): over every enemy's name plate, a row with
/// Guard and the actions of their job chosen in the settings, bright while ready and with the clock hand and the
/// seconds left while on cooldown; their Limit Break for a while after they use it.
/// </summary>
/// <remarks>
/// The game never tells a client the cooldowns of other players. What it does tell is the statuses of everyone
/// around and the actions they use: a Guard is seen when its status appears on an enemy, any other action when its
/// effect reaches the client, and its cooldown (from the game data) is counted from that moment. An action never
/// seen used shows ready. A Limit Break has no cooldown, it depends on a gauge the game does not show for others, so
/// it only shows for a while after it is used.
/// </remarks>
[Tweak(TweakCategory.PvP)]
public sealed unsafe class GoblinEnemyGuard : Tweak<GoblinEnemyGuard.Options>
{
    public sealed class Options
    {
        /// <summary>The icons over every enemy, in every PvP area.</summary>
        public bool ShowIcons { get; set; } = true;

        /// <summary>The actions followed besides Guard (Action rows), chosen by job in the settings.</summary>
        public HashSet<uint> TrackedActions { get; set; } = [.. PvPActions.Defaults];

        /// <summary>Size of each icon, in pixels.</summary>
        public int IconSize { get; set; } = 32;

        /// <summary>Charge time of a Limit Break whose description gives none and can't be measured, in seconds.</summary>
        public int LimitBreakSeconds { get; set; } = 60;

        /// <summary>The row goes from the character's model (its feet) instead of over their name plate.</summary>
        public bool AnchorOnCharacter { get; set; }

        /// <summary>Moves the row right (positive) or left, in screen pixels.</summary>
        public int OffsetX { get; set; }

        /// <summary>Moves the row down (positive) or up, in screen pixels.</summary>
        public int OffsetY { get; set; }

        /// <summary>
        /// The rows go in a fixed place on screen, one under another with each enemy's name, instead of over each
        /// enemy: they never move with the camera.
        /// </summary>
        public bool FixedOnScreen { get; set; }

        /// <summary>Where the fixed rows start (their icons), in screen pixels from the centre of the screen.</summary>
        public int FixedX { get; set; } = 300;
        public int FixedY { get; set; } = -250;

        /// <summary>Warns when you use your Limit Break on a target that has Guard.</summary>
        public bool WarnLimitBreak { get; set; } = true;

        /// <summary>Stops your Limit Break on a target that has Guard; pressing it again soon after uses it anyway.</summary>
        public bool BlockLimitBreak { get; set; }

        /// <summary>The warning also goes to the chat, besides the screen.</summary>
        public bool WarnInChat { get; set; }
    }

    // A Limit Break stopped is let through if pressed again within this time.
    private static readonly TimeSpan PressAgainWindow = TimeSpan.FromSeconds(2);

    // UseAction: an action of the kind Action.
    private const byte ActionKindAction = 1;

    // One use can reach the client as two effects (Marksman's Spite: about 1.4 s apart): the first one counts.
    private const float SameUseWithin = 2f;

    private Hook<ActionManager.Delegates.UseAction>? _useActionHook;
    private Hook<ActionEffectHandler.Delegates.Receive>? _effectHook;
    private Exception? _failure;

    /// <summary>How far enemies' Limit Breaks have charged, from your own gauge.</summary>
    private readonly PvPLimitBreakClock _limitBreakClock = new(nameof(GoblinEnemyGuard));

    /// <summary>When each enemy last used each followed cooldown (entity id, group id), and which action of it.</summary>
    private readonly Dictionary<(uint Enemy, uint Action), DateTime> _actionUsedAt = [];
    private readonly Dictionary<(uint Enemy, uint Action), uint> _lastMember = [];

    // The icons of one enemy, worked out before they are placed in a row.
    private readonly List<(uint Icon, float Passed, float Duration)> _row = [];
    private SimpleSettingsAddon? _settingsAddon;
    private readonly Dictionary<uint, bool> _isLimitBreak = [];
    private uint _stoppedAction;
    private DateTime _stoppedAt = DateTime.MinValue;

    private const uint GuardIcon = 9581;          // the icon of the Guard action
    private const float GuardRecast = 30f;
    private static readonly uint[] GuardStatuses = PvP.GuardStatuses;

    private const float IconGap = 2f;             // between the icons of a row, in the overlay's pixels
    private const float LiftAboveHead = 2f;       // gap between the name plate and the icon, in screen pixels
    private const float FallbackHeight = 2.4f;    // over the feet, in yalms, when there is no name plate

    // Where the middle of each enemy's name plate is (entity id), from its left edge, averaged: see AboveNamePlate.
    private const float CentreSmoothingSeconds = 0.3f;
    private const float CentreJumpPixels = 60f;
    private readonly Dictionary<uint, (float Centre, DateTime At)> _plateCentre = [];

    private OverlayNodes? _overlay;

    // Reused from frame to frame and match to match: the n-th enemy on the list takes the n-th node.
    private readonly List<EnemyGuardNode> _nodes = [];
    private int _used;

    // The fixed rows on screen: the nearest enemies, each with their name.
    private const int MaxFixedRows = 8;
    private const float RowGap = 4f;              // between fixed rows, in the overlay's pixels
    private const float NameGap = 6f;             // between a name and its icons
    private readonly List<(uint Id, nint Enemy, float Distance)> _fixedRows = [];
    private readonly List<EnemyNameNode> _names = [];
    private int _namesUsed;

    /// <summary>When each enemy last used Guard (entity id).</summary>
    private readonly Dictionary<uint, DateTime> _usedAt = [];

    /// <summary>The enemies that had the Guard status last frame, to see it start.</summary>
    private readonly HashSet<uint> _guarding = [];
    private readonly HashSet<uint> _guardingNow = [];
    private readonly HashSet<uint> _seen = [];

    // The icons are placed when the name plates are, after the game has moved the camera and the characters this
    // frame. When that has not happened for this long (no name plates), they are placed from the frame update.
    private const string NamePlateAddon = "NamePlate";
    private static readonly TimeSpan PlatesQuietFor = TimeSpan.FromMilliseconds(250);
    private DateTime _platesUpdatedAt = DateTime.MinValue;

    private ushort _territory;
    private bool _inMatch;
    private int _mostSeen;
    private string? _placementSource;

    protected internal override void Enable()
    {
        _overlay = new OverlayNodes();
        _territory = 0;
        _inMatch = false;
        _stoppedAction = 0;
        _failure = null;
        _ = PvPActions.ById; // read the game data now, not inside the game's code

        // Only the Limit Break warning needs this hook: if it can't be made, the icons still work without it.
        try
        {
            var useAction = ActionManager.Addresses.UseAction.Value;
            if (useAction == 0) throw new InvalidOperationException("the game function for using actions was not found (game updated?)");

            _useActionHook = Svc.GameInterop.HookFromAddress<ActionManager.Delegates.UseAction>((nint)useAction, OnUseAction);
            _useActionHook.Enable();
        }
        catch (Exception ex)
        {
            _useActionHook?.Dispose();
            _useActionHook = null;
            Svc.Log.Warning(ex, "GoblinEnemyGuard: the Limit Break warning is off this session; the icons work as usual.");
        }

        // Only the actions other than Guard need this one: without it, Guard still shows.
        try
        {
            var receive = ActionEffectHandler.Addresses.Receive.Value;
            if (receive == 0) throw new InvalidOperationException("the game function for action effects was not found (game updated?)");

            _effectHook = Svc.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>((nint)receive, OnActionEffect);
            _effectHook.Enable();
        }
        catch (Exception ex)
        {
            _effectHook?.Dispose();
            _effectHook = null;
            Svc.Log.Warning(ex, "GoblinEnemyGuard: only Guard is followed this session; the other actions can't be seen.");
        }

        _settingsAddon = NewSettingsAddon();
        _platesUpdatedAt = DateTime.MinValue;
        Svc.Framework.Update += OnUpdate;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, NamePlateAddon, OnNamePlatesUpdated);
    }

    protected internal override void Disable()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, NamePlateAddon, OnNamePlatesUpdated);
        Svc.Framework.Update -= OnUpdate;

        _useActionHook?.Dispose();
        _useActionHook = null;
        _effectHook?.Dispose();
        _effectHook = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;

        // Takes its nodes off the shared overlay and frees them; the overlay itself stays.
        _overlay?.Dispose();
        _overlay = null;
        _nodes.Clear();
        _names.Clear();
        _plateCentre.Clear();
        _usedAt.Clear();
        _actionUsedAt.Clear();
        _lastMember.Clear();
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
        Size            = new System.Numerics.Vector2(620f, 640f),
        RespectCloseAll = false,
        Tweak           = this,
        Heading         = T("Settings.Heading"),
        Toggles         = () =>
        [
            Option("ShowIcons",       () => Settings.ShowIcons,       on => Settings.ShowIcons       = on),
            Option("WarnLimitBreak",  () => Settings.WarnLimitBreak,  on => Settings.WarnLimitBreak  = on),
            Option("BlockLimitBreak", () => Settings.BlockLimitBreak, on => Settings.BlockLimitBreak = on),
            Option("WarnInChat",      () => Settings.WarnInChat,      on => Settings.WarnInChat      = on),
            Option("AnchorOnCharacter", () => Settings.AnchorOnCharacter, on => Settings.AnchorOnCharacter = on),
            Option("FixedOnScreen",   () => Settings.FixedOnScreen,   on => Settings.FixedOnScreen   = on),
        ],
        Numbers         = () =>
        [
            new(T("IconSize"), T("IconSize.Help"), 20, 64, 2, () => Settings.IconSize, value =>
            {
                Settings.IconSize = value;
                SaveSettings();
            }),
            new(T("OffsetX"), T("OffsetX.Help"), -400, 400, 2, () => Settings.OffsetX, value =>
            {
                Settings.OffsetX = value;
                SaveSettings();
            }),
            new(T("OffsetY"), T("OffsetY.Help"), -400, 400, 2, () => Settings.OffsetY, value =>
            {
                Settings.OffsetY = value;
                SaveSettings();
            }),
            new(T("FixedX"), T("FixedX.Help"), -2000, 2000, 10, () => Settings.FixedX, value =>
            {
                Settings.FixedX = value;
                SaveSettings();
            }),
            new(T("FixedY"), T("FixedY.Help"), -1200, 1200, 10, () => Settings.FixedY, value =>
            {
                Settings.FixedY = value;
                SaveSettings();
            }),
            new(T("LimitBreakSeconds"), T("LimitBreakSeconds.Help"), 10, 180, 5, () => Settings.LimitBreakSeconds, value =>
            {
                Settings.LimitBreakSeconds = value;
                SaveSettings();
            }),
        ],
        Groups          = ActionGroups,
        HelpClick       = ToggleHelpWindow,
        HelpTooltip     = Loc.GetIn(UiLanguage, "Window.Help"),
    };

    /// <summary>One section per job with a switch per action that can be followed, after a section explaining them.</summary>
    private IReadOnlyList<SettingsGroup> ActionGroups()
    {
        var groups = new List<SettingsGroup> { new(T("Settings.Actions"), T("Settings.Actions.Note"), []) };

        foreach (var (job, actions) in PvPActions.ByJob.OrderBy(pair => PvPActions.JobNames[pair.Key]))
        {
            var toggles = actions.Select(action => new SettingsIconToggle(
                action.Icon,
                action.IsLimitBreak ? string.Format(T("Action.LimitBreak"), action.Name) : string.Format(T("Action.Cooldown"), action.Name, action.Recast),
                string.IsNullOrEmpty(action.Description) ? action.Name : $"{action.Name}\n{action.Description}",
                () => Settings.TrackedActions.Contains(action.Id),
                on =>
                {
                    if (on) Settings.TrackedActions.Add(action.Id); else Settings.TrackedActions.Remove(action.Id);
                    SaveSettings();
                })).ToList();

            groups.Add(new SettingsGroup(PvPActions.JobNames[job], null, [], toggles));
        }

        return groups;
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
                && row.IsPvP && PvPActions.LimitBreakCategories.Contains(row.ActionCategory.RowId);
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
        return PvP.IsEnemy(target) && PvP.HasGuard(target) ? ((Character*)target)->NameString : null;
    }

    /// <summary>A warning on screen, and in the chat too when asked for.</summary>
    private void Warn(string text)
    {
        Svc.Toasts.ShowError(text);
        if (Settings.WarnInChat) Svc.Chat.Print(text, Name);
    }

    protected override IReadOnlyList<string> CommandNames => ["/geg", "/genemyguard"];

    protected override void OnCommand() => ToggleSettings();

    /// <summary>
    /// An action resolved by the server reaching the client. Runs inside the game's own code: it must never throw.
    /// A followed action starts its cooldown for whoever used it.
    /// </summary>
    private void OnActionEffect(uint casterEntityId, Character* caster, Vector3* targetPos, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            if (_inMatch && header != null && (byte)header->ActionType == ActionKindAction
                && PvPActions.ByMember.TryGetValue(header->ActionId, out var action) && Settings.TrackedActions.Contains(action.Id))
            {
                // Any action of the group starts the group's cooldown, and the next one of the chain comes up.
                var key = (casterEntityId, action.Id);
                var now = DateTime.UtcNow;
                if (!_actionUsedAt.TryGetValue(key, out var last) || (now - last).TotalSeconds > Math.Min(SameUseWithin, action.Recast / 2f))
                {
                    _actionUsedAt[key] = now;
                    _lastMember[key] = header->ActionId;
                }
            }
        }
        catch (Exception ex)
        {
            // Reported from OnUpdate: disabling the tweak here would free the hook that is running.
            _failure ??= ex;
        }

        _effectHook!.Original(casterEntityId, caster, targetPos, header, effects, targetEntityIds);
    }

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
            var now = DateTime.UtcNow;
            CheckTerritory();
            if (_inMatch) _limitBreakClock.Update(now);

            if (!_inMatch || !Settings.ShowIcons) HideAll();
            else if (now - _platesUpdatedAt > PlatesQuietFor) Track();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>
    /// Places the icons right after the game has placed the name plates. The frame update comes before the game
    /// moves the camera and the characters, so icons placed there were a frame behind the name plate they go over,
    /// and shook whenever either moved.
    /// </summary>
    private void OnNamePlatesUpdated(AddonEvent type, AddonArgs args)
    {
        if (!_inMatch || !Settings.ShowIcons || _failure != null) return;

        try
        {
            if (_platesUpdatedAt == DateTime.MinValue)
                Svc.Log.Info("GoblinEnemyGuard: icons placed with the name plates, after the camera moves.");

            _platesUpdatedAt = DateTime.UtcNow;
            Track();
        }
        catch (Exception ex)
        {
            // Reported from OnUpdate, out of the game's window code.
            _failure ??= ex;
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
        _placementSource = null;
        _usedAt.Clear();
        _actionUsedAt.Clear();
        _lastMember.Clear();
        _plateCentre.Clear();
        _limitBreakClock.Reset();
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
        _fixedRows.Clear();
        _used = 0;
        _namesUsed = 0;

        foreach (var entry in manager->Objects.IndexSorted)
        {
            var obj = entry.Value;
            if (obj == null || obj == player || !PvP.IsEnemy(obj)) continue;

            var enemy = (BattleChara*)obj;

            var id = obj->EntityId;
            _seen.Add(id);
            WatchGuard(enemy, id, player, now);
            if (((Character*)enemy)->IsDead()) continue;

            if (Settings.FixedOnScreen) _fixedRows.Add((id, (nint)enemy, Vector3.DistanceSquared(obj->Position, player->Position)));
            else PlaceOver(enemy, id, now);
        }

        if (Settings.FixedOnScreen) PlaceFixed(now);

        _guarding.Clear();
        _guarding.UnionWith(_guardingNow);

        for (var i = _used; i < _nodes.Count; i++)
            _nodes[i].IsVisible = false;
        for (var i = _namesUsed; i < _names.Count; i++)
            _names[i].IsVisible = false;

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

            // The 30 s cooldown starts the moment Guard is used, which is when its status appears: the count always
            // starts at 30. The status' time left is not used to date it back: the game gives 0 for other players,
            // which read as "used 4 s ago" (the whole Guard) and started the count at 26.
            _usedAt[id] = now;

            var distance = Vector3.Distance(((GameObject*)enemy)->Position, player->Position);
            Svc.Log.Info($"GoblinEnemyGuard: {((Character*)enemy)->NameString} used Guard {distance:0} yalms away (status time left {status.RemainingTime:0.00} s).");
            return;
        }
    }

    /// <summary>Puts the row of an enemy over them on screen, from their name plate or their model.</summary>
    private void PlaceOver(BattleChara* enemy, uint id, DateTime now)
    {
        var obj = (GameObject*)enemy;
        if (!Anchor(obj, now, out var spot, out var source)) return;

        BuildRow(enemy, id, now);
        spot += new Vector2(Settings.OffsetX, Settings.OffsetY);

        var size = (float)Settings.IconSize;
        var first = TakeNodes();

        // The overlay is drawn at the scale of the game's interface, while the spot is in screen pixels.
        var scale = OverlayScale(_nodes[first]);
        var width = _row.Count * size + (_row.Count - 1) * IconGap;

        // On whole pixels: an icon between two pixels shimmers as the spot moves a fraction of one.
        var left = MathF.Round(spot.X / scale - width / 2f);
        var top  = MathF.Round((spot.Y - LiftAboveHead) / scale - size);
        DrawRow(first, left, top, size);


        if (_placementSource != source)
        {
            _placementSource = source;
            Svc.Log.Info($"GoblinEnemyGuard: icons over {((Character*)enemy)->NameString} placed from the {source}, at {spot.X:0},{spot.Y:0}, overlay scale {scale:0.00}.");
        }
    }

    /// <summary>
    /// The rows in a fixed place on screen, one under another, each with the enemy's name at its left: the nearest
    /// enemies, in a steady order (by entity) so a row does not jump from place to place as they move.
    /// </summary>
    private void PlaceFixed(DateTime now)
    {
        if (_fixedRows.Count == 0) return;

        _fixedRows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (_fixedRows.Count > MaxFixedRows) _fixedRows.RemoveRange(MaxFixedRows, _fixedRows.Count - MaxFixedRows);
        _fixedRows.Sort((a, b) => a.Id.CompareTo(b.Id));

        var screen = ScreenCentre();
        var size = (float)Settings.IconSize;

        for (var r = 0; r < _fixedRows.Count; r++)
        {
            var (id, address, _) = _fixedRows[r];
            var enemy = (BattleChara*)address;

            BuildRow(enemy, id, now);
            var first = TakeNodes();
            var scale = OverlayScale(_nodes[first]);
            var left = MathF.Round((screen.X + Settings.FixedX) / scale);
            var top  = MathF.Round((screen.Y + Settings.FixedY) / scale + r * (size + RowGap));
            DrawRow(first, left, top, size);

            NextName().Show(((Character*)enemy)->NameString, left - NameGap, top, size);
        }

        if (_placementSource != "fixed place")
        {
            _placementSource = "fixed place";
            Svc.Log.Info($"GoblinEnemyGuard: icons in a fixed place on screen, {Settings.FixedX},{Settings.FixedY} from the centre.");
        }
    }

    /// <summary>Takes a node for each icon of <see cref="_row"/>; returns the first one's index.</summary>
    private int TakeNodes()
    {
        var first = _used;
        for (var i = 0; i < _row.Count; i++) NextNode();
        return first;
    }

    /// <summary>Shows the icons of <see cref="_row"/> on the nodes from <paramref name="first"/>, left to right.</summary>
    private void DrawRow(int first, float left, float top, float size)
    {
        for (var i = 0; i < _row.Count; i++)
        {
            var (icon, passed, duration) = _row[i];
            var node = _nodes[first + i];
            node.SetIcon(icon);
            node.SetSize(size);
            node.Position = new Vector2(left + i * (size + IconGap), top);

            if (passed >= duration) node.ShowReady();
            else node.ShowCooldown(passed / duration, duration - passed);

            node.IsVisible = true;
        }
    }

    private static Vector2 ScreenCentre()
    {
        var device = Device.Instance();
        return device == null ? Vector2.Zero : new Vector2(device->Width / 2f, device->Height / 2f);
    }

    /// <summary>
    /// The icons of an enemy's row, into <see cref="_row"/>: Guard, then the followed cooldowns of their job (one
    /// icon per cooldown, the next action of a chain), then their Limit Break. Each one ready or on cooldown.
    /// </summary>
    private void BuildRow(BattleChara* enemy, uint id, DateTime now)
    {
        _row.Clear();
        _row.Add((GuardIcon, _usedAt.TryGetValue(id, out var guardAt) ? (float)(now - guardAt).TotalSeconds : float.MaxValue, GuardRecast));

        if (PvPActions.ByJob.TryGetValue(((Character*)enemy)->CharacterData.ClassJob, out var actions))
        {
            foreach (var action in actions)
            {
                if (!Settings.TrackedActions.Contains(action.Id)) continue;

                var key = (id, action.Id);
                var used = _actionUsedAt.TryGetValue(key, out var usedAt);
                if (!action.IsLimitBreak)
                {
                    // Actions sharing a cooldown are one icon: the one that comes next in the chain.
                    var icon = action.NextIcon(_lastMember.GetValueOrDefault(key));
                    _row.Add((icon, used ? (float)(now - usedAt).TotalSeconds : float.MaxValue, action.Recast));
                    continue;
                }

                // A Limit Break charges by itself, in the time its description gives, since it was used or since the
                // match started.
                var charge = action.ChargeSeconds > 0f ? action.ChargeSeconds : _limitBreakClock.ChargeSeconds(Settings.LimitBreakSeconds);
                var progress = _limitBreakClock.Progress(used ? usedAt : null, now, charge);
                _row.Add((action.Icon, progress * charge, charge));
            }
        }
    }

    /// <summary>
    /// The screen point the icon's bottom centre goes at: just over the enemy's name plate. The game keeps, for every
    /// character with a name plate, which plate is theirs (RaptureAtkModule's name plate entries, in the same order
    /// as the plates the NamePlate window draws) and where it is drawn: the top of the name text is used, centred
    /// over the character. When the plate is not shown (name plates turned off, too far), a fixed height over the
    /// feet (GetHeight() gives 0 for players).
    /// </summary>
    private bool Anchor(GameObject* obj, DateTime now, out Vector2 spot, out string source)
    {
        if (!Settings.AnchorOnCharacter) return AboveNamePlate(obj, now, out spot, out source);

        source = "character";
        return Svc.GameGui.WorldToScreen((Vector3)obj->Position, out spot);
    }

    /// <remarks>
    /// The row moves with the name plate, pixel for pixel, in both directions. Where the game projects a point of the
    /// world on screen (WorldToScreen) is not in step with what it draws while the camera turns: icons placed from
    /// the feet shook, by 2 to 6 pixels a frame, against the model and the plate. The feet only say where the
    /// middle of the plate is (its node starts at its left edge), averaged over a third of a second so their
    /// shaking does not reach the row.
    /// </remarks>
    private bool AboveNamePlate(GameObject* obj, DateTime now, out Vector2 spot, out string source)
    {
        spot = default;
        if (!Svc.GameGui.WorldToScreen((Vector3)obj->Position, out var feet))
        {
            source = "nowhere";
            return false;
        }

        var id = obj->EntityId;
        var plate = NamePlateOf(id);
        if (plate != null && plate->RootComponentNode != null)
        {
            var left = plate->RootComponentNode->AtkResNode.ScreenX;
            var top = plate->NameText->AtkResNode.ScreenY;
            if (top > 0f && top < feet.Y)
            {
                spot = new Vector2(left + SmoothedCentre(id, feet.X - left, now), top);
                source = "name plate";
                return true;
            }
        }

        _plateCentre.Remove(id);
        source = "feet";
        return Svc.GameGui.WorldToScreen((Vector3)obj->Position + new Vector3(0f, FallbackHeight, 0f), out spot);
    }

    /// <summary>
    /// How far right of the plate's left edge the character is, averaged. A change too big to be shaking (the plate
    /// coming back, a new name) is taken at once.
    /// </summary>
    private float SmoothedCentre(uint id, float centre, DateTime now)
    {
        if (_plateCentre.TryGetValue(id, out var last) && MathF.Abs(centre - last.Centre) < CentreJumpPixels)
        {
            var seconds = (float)Math.Clamp((now - last.At).TotalSeconds, 0d, 1d);
            centre = last.Centre + (centre - last.Centre) * (1f - MathF.Exp(-seconds / CentreSmoothingSeconds));
        }

        _plateCentre[id] = (centre, now);
        return centre;
    }

    /// <summary>The name plate the game is drawing now for a character, or null when it shows none.</summary>
    private static AddonNamePlate.NamePlateObject* NamePlateOf(uint entityId)
    {
        var module = RaptureAtkModule.Instance();
        var units = RaptureAtkUnitManager.Instance();
        if (module == null || units == null) return null;

        var addon = (AddonNamePlate*)units->GetAddonByName("NamePlate");
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->NamePlateObjectArray == null) return null;

        var entries = module->NamePlateInfoEntries;
        var count = Math.Min(entries.Length, AddonNamePlate.NumNamePlateObjects);
        for (var i = 0; i < count; i++)
        {
            if (entries[i].ObjectId.ObjectId != entityId) continue;

            var plate = &addon->NamePlateObjectArray[i];
            return plate->IsVisible && plate->NameText != null ? plate : null;
        }

        return null;
    }

    /// <summary>The scale the overlay holding the node is drawn at (1 when it can't be told).</summary>
    private static float OverlayScale(EnemyGuardNode node)
    {
        var units = RaptureAtkUnitManager.Instance();
        var addon = units == null ? null : units->GetAddonByNode((AtkResNode*)node);
        return addon == null || addon->Scale <= 0.01f ? 1f : addon->Scale;
    }

    private EnemyGuardNode NextNode()
    {
        if (_used == _nodes.Count)
        {
            var node = new EnemyGuardNode(GuardIcon, Settings.IconSize);
            _overlay!.AddNode(node);
            _nodes.Add(node);
        }

        return _nodes[_used++];
    }

    private EnemyNameNode NextName()
    {
        if (_namesUsed == _names.Count)
        {
            var node = new EnemyNameNode();
            _overlay!.AddNode(node);
            _names.Add(node);
        }

        return _names[_namesUsed++];
    }

    private void HideAll()
    {
        foreach (var node in _nodes)
            node.IsVisible = false;
        foreach (var name in _names)
            name.IsVisible = false;
    }
}
