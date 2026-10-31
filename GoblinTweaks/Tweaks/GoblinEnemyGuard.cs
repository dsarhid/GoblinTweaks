using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.UiOverlay;
using TerritoryRow = Lumina.Excel.Sheets.TerritoryType;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Crystalline Conflict: a Guard icon over every enemy, bright while they have it and with the clock hand and
/// the seconds left while it is on cooldown.
/// </summary>
/// <remarks>
/// The game never tells a client the cooldowns of other players. What it does tell is the statuses of everyone
/// around, so a Guard is seen when its status appears on an enemy, and its cooldown is counted from the moment
/// it was used. An enemy never seen using it shows it ready. No hooks: statuses are read once a frame.
/// </remarks>
[Tweak(TweakCategory.PvP)]
public sealed unsafe class GoblinEnemyGuard : Tweak
{
    private const uint GuardIcon = 9581;          // the icon of the Guard action
    private const float GuardRecast = 30f;
    private const float GuardDuration = 4f;       // how long the status lasts when nothing ends it early
    private static readonly uint[] GuardStatuses = [3054, 3673];

    // TerritoryIntendedUse rows of Crystalline Conflict: matches, and custom matches.
    private static readonly uint[] CrystallineConflictUses = [37, 28];

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
    private readonly Dictionary<ushort, bool> _isCrystallineConflict = [];

    protected internal override void Enable()
    {
        _overlay = new OverlayNodes();
        _territory = 0;
        _inMatch = false;
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;

        // Takes its nodes off the shared overlay and frees them; the overlay itself stays.
        _overlay?.Dispose();
        _overlay = null;
        _nodes.Clear();
        _usedAt.Clear();
        _guarding.Clear();
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

    protected override IReadOnlyList<string> CommandNames => ["/geg", "/genemyguard"];

    protected override void OnCommand() => ToggleHelpWindow();

    private void OnUpdate(IFramework _)
    {
        try
        {
            CheckTerritory();
            if (_inMatch) Track(); else HideAll();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>A new zone starts with no Guard seen: the enemies of the last match are not the ones of this one.</summary>
    private void CheckTerritory()
    {
        var territory = (ushort)Svc.ClientState.TerritoryType;
        if (territory == _territory) return;

        _territory = territory;
        _inMatch = IsCrystallineConflict(territory);
        _mostSeen = 0;
        _usedAt.Clear();
        _guarding.Clear();
        HideAll();
    }

    private bool IsCrystallineConflict(ushort territory)
    {
        if (!_isCrystallineConflict.TryGetValue(territory, out var result))
        {
            result = Svc.Data.GetExcelSheet<TerritoryRow>().TryGetRow(territory, out var row)
                && row.IsPvpZone && CrystallineConflictUses.Contains(row.TerritoryIntendedUse.RowId);
            _isCrystallineConflict[territory] = result;
        }

        return result;
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
