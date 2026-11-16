using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GoblinTweaks.Crafting;
using GoblinTweaks.UI;

namespace GoblinTweaks.Services;

/// <summary>
/// The map and teleport buttons of the Crafting Materials totals "By source": the gathering areas drawn on the map as
/// the Gathering Log draws them, the teleport to the nearest attuned aetheryte, and the change to the gathering class's
/// gearset once the teleport has landed.
/// </summary>
internal sealed unsafe class GatherTravel(Action<string> notify) : IDisposable
{
    /// <summary>The style the Gathering Log gives its area markers.</summary>
    private const uint MarkerStyle = 4;

    private const uint MinerJob = 16, BotanistJob = 17, FisherJob = 18;

    private JobSwitch?                _pending;
    private IReadOnlyList<GatherArea> _pendingAreas = [];
    private GatherClass               _pendingClass;
    private DateTime                  _pendingSince;
    private bool                      _subscribed;

    /// <summary>Opens the map of the areas' zone with the areas drawn on it.</summary>
    public static void OpenMap(IReadOnlyList<GatherArea> areas, string title)
    {
        var onMap = OnFirstMap(areas);
        if (onMap.Count == 0) return;
        var agent = AgentMap.Instance();
        if (agent == null) return;

        PlaceMarkers(onMap);
        agent->OpenMap(onMap[0].MapId, onMap[0].ZoneId, title, MapType.GatheringLog);
    }

    /// <summary>
    /// Draws the areas on the map and teleports to the attuned aetheryte nearest to them; once landed in their zone, the
    /// gearset of <paramref name="cls"/> is put on. A new teleport replaces a change still waiting.
    /// </summary>
    public void Teleport(IReadOnlyList<GatherArea> areas, GatherClass cls, string zoneName)
    {
        Cancel();
        var onMap = OnFirstMap(areas);
        if (onMap.Count == 0) return;

        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            notify(CraftLoc.Get("travel.refused"));
            return;
        }

        PlaceMarkers(onMap);

        var first = onMap[0];
        var points = new List<(float X, float Y)>(onMap.Count);
        if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Map>().GetRowOrDefault(first.MapId) is { } map)
            foreach (var a in onMap)
                points.Add((TravelMath.WorldToMapCoord(a.X, map.SizeFactor, map.OffsetX), TravelMath.WorldToMapCoord(a.Y, map.SizeFactor, map.OffsetY)));

        if (HuntTravel.NearestAttuned(telepo, first.ZoneId, first.MapId, points) is not { } aetheryte)
        {
            notify(CraftLoc.Fmt("travel.noaetheryte", zoneName));
            return;
        }
        if (!telepo->Teleport(aetheryte, 0))
        {
            notify(CraftLoc.Get("travel.refused"));
            return;
        }

        _pending      = new JobSwitch(first.ZoneId);
        _pendingAreas = onMap;
        _pendingClass = cls;
        _pendingSince = DateTime.UtcNow;
        if (!_subscribed)
        {
            Svc.Framework.Update += OnUpdate;
            _subscribed = true;
        }
    }

    /// <summary>Forgets a job change still waiting for the teleport.</summary>
    public void Cancel()
    {
        _pending      = null;
        _pendingAreas = [];
        if (!_subscribed) return;
        Svc.Framework.Update -= OnUpdate;
        _subscribed = false;
    }

    public void Dispose() => Cancel();

    private void OnUpdate(IFramework framework)
    {
        if (_pending is not { } pending) return;

        var between = Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51];
        var ready   = Control.GetLocalPlayer() != null
                   && !Svc.Condition[ConditionFlag.InCombat]
                   && !Svc.Condition[ConditionFlag.Casting]
                   && !Svc.Condition[ConditionFlag.Occupied];
        var step = pending.Step(between, Svc.ClientState.TerritoryType, ready, (DateTime.UtcNow - _pendingSince).TotalSeconds);
        if (step == JobSwitchStep.Wait) return;

        var areas = _pendingAreas;
        var cls   = _pendingClass;
        Cancel();
        if (step != JobSwitchStep.Switch) return;

        // The load may have cleared the map's temporary markers: draw the areas again, then change job.
        PlaceMarkers(areas);
        ChangeJob(cls);
    }

    /// <summary>Puts on the gearset of the class (the highest item level one), unless the player already is that class.</summary>
    private void ChangeJob(GatherClass cls)
    {
        var job = cls switch { GatherClass.Miner => MinerJob, GatherClass.Botanist => BotanistJob, _ => FisherJob };
        if (Svc.PlayerState.ClassJob.RowId == job) return;

        var module = RaptureGearsetModule.Instance();
        if (module == null) return;

        var sets = new List<(int Id, uint ClassJob, int ItemLevel)>();
        for (var i = 0; i < module->Entries.Length; i++)
        {
            if (!module->IsValidGearset(i)) continue;
            ref var entry = ref module->Entries[i];
            sets.Add((i, entry.ClassJob, entry.ItemLevel));
        }

        if (TravelMath.PickGearset(sets, job) is not { } id)
        {
            notify(CraftLoc.Fmt("travel.nogearset", ClassName(cls)));
            return;
        }
        module->EquipGearset(id, 0);
    }

    internal static string ClassName(GatherClass cls) => cls switch
    {
        GatherClass.Miner    => CraftLoc.Get("class.miner"),
        GatherClass.Botanist => CraftLoc.Get("class.botanist"),
        _                    => CraftLoc.Get("class.fisher"),
    };

    /// <summary>The areas on the same map as the first one (the map that opens), at most as many as the map holds.</summary>
    private static List<GatherArea> OnFirstMap(IReadOnlyList<GatherArea> areas)
    {
        var list = new List<GatherArea>(areas.Count);
        if (areas.Count == 0) return list;
        var agent = AgentMap.Instance();
        var max = agent == null ? 0 : agent->TempMapMarkers.Length;
        foreach (var a in areas)
            if (a.MapId == areas[0].MapId && list.Count < max) list.Add(a);
        return list;
    }

    /// <summary>
    /// Replaces the map's temporary markers with the areas, as the Gathering Log does: centre and radius in world units
    /// (ExportedGatheringPoint), the gathering icon and the "Lv. 35 Mineral Deposit" label.
    /// </summary>
    private static void PlaceMarkers(IReadOnlyList<GatherArea> areas)
    {
        var agent = AgentMap.Instance();
        if (agent == null) return;

        agent->TempMapMarkerCount = 0;
        foreach (var a in areas)
        {
            agent->AddGatheringTempMarker((int)MathF.Round(a.X), (int)MathF.Round(a.Y), (int)MathF.Round(a.Radius), a.IconId, MarkerStyle, a.Label);
            Svc.Log.Debug($"[GatherTravel] marker map={a.MapId} zone={a.ZoneId} x={a.X:F1} y={a.Y:F1} r={a.Radius:F0} icon={a.IconId} '{a.Label}'");
        }
    }
}
