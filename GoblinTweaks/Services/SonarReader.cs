using System.Collections;
using System.Reflection;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using GoblinTweaks.Hunts;
using Lumina.Excel.Sheets;

namespace GoblinTweaks.Services;

internal enum SonarReadStatus { NotInstalled, Ok, Failed }

/// <summary>
/// Reads the hunts and FATEs Sonar holds in memory, by reflection (Sonar has no public API and lives in its
/// own load context). Built against SonarPlugin 15.757. Any change in Sonar makes <see cref="Read"/> return
/// null with <see cref="Status"/> = Failed; it never throws, never writes to Sonar, and keeps no Sonar object
/// past one call (only the plugin instance and reflection members are cached).
/// </summary>
internal sealed class SonarReader
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    // Sonar's default display timers, applied only when reading the raw trackers (the views apply the user's own).
    private const double RawAliveKeepMs = 3_600_000, RawDeadKeepMs = 300_000;
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly Dictionary<(Type, string), Func<object, object?>?> getters = [];
    private object? plugin;
    private DateTime retryAt;
    private bool warned;

    public SonarReadStatus Status { get; private set; } = SonarReadStatus.NotInstalled;

    public void Reset()
    {
        getters.Clear();
        plugin = null;
        retryAt = default;
        warned = false;
        Status = SonarReadStatus.NotInstalled;
    }

    public IReadOnlyList<HuntReport>? Read(DateTime now)
    {
        try
        {
            var exposed = Svc.PluginInterface.InstalledPlugins
                .FirstOrDefault(p => p.IsLoaded && p.InternalName.StartsWith("SonarPlugin", StringComparison.OrdinalIgnoreCase));
            if (exposed == null)
            {
                plugin = null;
                Status = SonarReadStatus.NotInstalled;
                return null;
            }

            if (Status == SonarReadStatus.Failed && now < retryAt) return null;

            var instance = FindInstance(exposed.InternalName) ?? throw new InvalidOperationException("plugin instance not found");
            if (!ReferenceEquals(instance, plugin))
            {
                getters.Clear();
                plugin = instance;
            }

            var reports = new List<HuntReport>();
            var (hunts, fates, raw) = Trackers(instance);
            ReadStates(hunts, isFate: false, raw, reports);
            ReadStates(fates, isFate: true, raw, reports);

            if (Status == SonarReadStatus.Failed)
                Svc.Log.Info("GoblinHunts: reading Sonar works again.");
            Status = SonarReadStatus.Ok;
            warned = false;
            return reports;
        }
        catch (Exception ex)
        {
            var reason = (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).Message;
            if (!warned)
            {
                Svc.Log.Warning($"GoblinHunts: could not read Sonar ({reason}); the table keeps what Sonar writes in the chat.");
                warned = true;
            }
            getters.Clear();
            plugin = null;
            Status = SonarReadStatus.Failed;
            retryAt = now + RetryDelay;
            return null;
        }
    }

    /// <summary>Dalamud's own LocalPlugin keeps the plugin object in a private field; the public API only gives names.</summary>
    private static object? FindInstance(string internalName)
    {
        var dalamud = typeof(IDalamudPluginInterface).Assembly;
        var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager") ?? throw new MissingMemberException("PluginManager");
        var service = dalamud.GetType("Dalamud.Service`1")?.MakeGenericType(managerType) ?? throw new MissingMemberException("Service<T>");
        var manager = service.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null)
                      ?? throw new MissingMemberException("PluginManager.Get");
        if (managerType.GetProperty("InstalledPlugins")?.GetValue(manager) is not IEnumerable plugins)
            throw new MissingMemberException("InstalledPlugins");

        foreach (var local in plugins)
        {
            if (local == null) continue;
            var t = local.GetType();
            if (t.GetProperty("InternalName")?.GetValue(local) as string != internalName) continue;
            if (t.GetProperty("IsLoaded")?.GetValue(local) is not true) continue;
            for (var type = t; type != null; type = type.BaseType)
            {
                var field = type.GetField("instance", Inst | BindingFlags.DeclaredOnly);
                if (field?.GetValue(local) is { } value) return value;
            }
        }
        return null;
    }

    /// <summary>
    /// Sonar's main-window views (they apply the user's filters and display timers), resolved from its DryIoc
    /// container; if that fails, the raw trackers of its client.
    /// </summary>
    private (object Hunts, object Fates, bool Raw) Trackers(object instance)
    {
        try
        {
            var viewsType = instance.GetType().Assembly.GetType("SonarPlugin.Trackers.RelayTrackerViews");
            var container = Get(instance, "_container");
            if (viewsType != null && container != null && Resolve(container, viewsType) is { } views
                && Get(views, "Hunts") is { } hv && Get(views, "Fates") is { } fv)
                return (hv, fv, false);
        }
        catch (Exception ex)
        {
            Svc.Log.Debug($"GoblinHunts: Sonar views unavailable ({ex.Message}); using raw trackers.");
        }

        var client = Get(instance, "Client") ?? throw new MissingMemberException("Client");
        var trackers = Get(client, "Trackers") ?? throw new MissingMemberException("Trackers");
        return (Get(trackers, "Hunts") ?? throw new MissingMemberException("Trackers.Hunts"),
                Get(trackers, "Fates") ?? throw new MissingMemberException("Trackers.Fates"),
                true);
    }

    private static object? Resolve(object container, Type serviceType)
    {
        var methods = container.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Concat(container.GetType().GetInterfaces().SelectMany(i => i.GetMethods()));
        foreach (var m in methods)
        {
            if (m.Name != "Resolve" && !m.Name.EndsWith(".Resolve", StringComparison.Ordinal)) continue;
            var ps = m.GetParameters();
            if (ps.Length != 2 || ps[0].ParameterType != typeof(Type) || !ps[1].ParameterType.IsEnum) continue;
            var ifUnresolved = Enum.GetNames(ps[1].ParameterType).Contains("ReturnDefault")
                ? Enum.Parse(ps[1].ParameterType, "ReturnDefault")
                : Enum.ToObject(ps[1].ParameterType, 0);
            return m.Invoke(container, [serviceType, ifUnresolved]);
        }
        return null;
    }

    private void ReadStates(object tracker, bool isFate, bool raw, List<HuntReport> into)
    {
        var data = Get(tracker, "Data") ?? throw new MissingMemberException("Data");
        if (Get(data, "States") is not IEnumerable states) throw new MissingMemberException("Data.States");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var pair in states)
        {
            if (pair == null) continue;
            var state = Get(pair, "Value") ?? pair;
            var relay = Require(state, "Relay");
            var info = relay == null ? null : Require(relay, "Info");
            if (relay == null || info == null) continue;

            HuntKind kind;
            if (isFate) kind = HuntKind.Fate;
            else
            {
                switch (Get(info, "Rank")?.ToString())
                {
                    case "A": kind = HuntKind.A; break;
                    case "S" or "SS" or "SSMinion": kind = HuntKind.S; break;
                    default: continue;
                }
            }

            var alive = Call(relay, "IsAlive") is true;
            var lastFound = ToDouble(Get(state, "LastFound"));
            if (raw)
            {
                var since = alive ? ToDouble(Get(state, "LastSeen")) : ToDouble(Get(state, "LastKilled"));
                if (since > 0 && nowMs - ToMs(since) > (alive ? RawAliveKeepMs : RawDeadKeepMs)) continue;
            }

            // A FATE or hunt Sonar's database does not know yet has an empty name: nothing to show.
            var name = Get(info, "Name")?.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Identity members are required: if a Sonar update renames one, the read fails and the source switches off,
            // instead of filling rows with world 0 that never join their chat report.
            var worldId = ToUInt(Require(relay, "WorldId"));
            var zoneId = ToUInt(Require(relay, "ZoneId"));
            var instanceId = ToUInt(Require(relay, "InstanceId"));
            var (mapId, x, y) = MapPosition(zoneId, Require(relay, "Coords"));

            float? percent = isFate ? (float)ToDouble(Get(relay, "Progress")) : (float)ToDouble(Get(relay, "HpPercent"));
            if (!isFate && !alive) percent = 0f;
            int? players = Get(relay, "Players") is { } p ? (int)ToDouble(p) : null;

            into.Add(new HuntReport(kind, name.Trim(), worldId, WorldName(worldId), zoneId, mapId, instanceId, x, y,
                percent, players, !alive, lastFound > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ToMs(lastFound)).UtcDateTime : null,
                HuntSources.Memory));
        }
    }

    /// <summary>Sonar keeps game coordinates; its chat links build a MapLinkPayload from them ×1000, which does the map conversion.</summary>
    private (uint MapId, float X, float Y) MapPosition(uint zoneId, object? coords)
    {
        var mapId = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(zoneId)?.Map.RowId ?? 0;
        if (coords == null || mapId == 0) return (mapId, 0, 0);
        var rawX = (int)(ToDouble(Get(coords, "X")) * 1000);
        var rawY = (int)(ToDouble(Get(coords, "Y")) * 1000);
        var link = new MapLinkPayload(zoneId, mapId, rawX, rawY);
        return (mapId, link.XCoord, link.YCoord);
    }

    private static string WorldName(uint worldId)
        => Svc.Data.GetExcelSheet<World>().GetRowOrDefault(worldId)?.Name.ExtractText() ?? "";

    /// <summary>Sonar's timestamps are Unix milliseconds; accept seconds too in case that changes.</summary>
    private static double ToMs(double t) => t < 100_000_000_000 ? t * 1000 : t;

    private static double ToDouble(object? v) => v switch
    {
        null => 0,
        IConvertible c => c.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
        _ => 0,
    };

    private static uint ToUInt(object? v) => (uint)Math.Max(0, ToDouble(v));

    private static object? Call(object target, string method)
        => target.GetType().GetMethod(method, Inst, Type.EmptyTypes)?.Invoke(target, null);

    /// <summary>A property or field by name, looked up on the class chain and then on its interfaces (for explicit implementations).</summary>
    /// <summary>Like <see cref="Get"/>, but a member that does not exist (Sonar changed) fails the read.</summary>
    private object? Require(object target, string name)
    {
        var value = Get(target, name);
        if (value == null && getters[(target.GetType(), name)] == null)
            throw new MissingMemberException(target.GetType().Name, name);
        return value;
    }

    private object? Get(object target, string name)
    {
        var type = target.GetType();
        if (!getters.TryGetValue((type, name), out var getter))
        {
            getter = BuildGetter(type, name);
            getters[(type, name)] = getter;
        }
        return getter?.Invoke(target);
    }

    private static Func<object, object?>? BuildGetter(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var prop = t.GetProperties(Inst | BindingFlags.DeclaredOnly)
                .FirstOrDefault(p => p.GetIndexParameters().Length == 0 && (p.Name == name || p.Name.EndsWith("." + name, StringComparison.Ordinal)));
            if (prop != null) return o => prop.GetValue(o);
            var field = t.GetField(name, Inst | BindingFlags.DeclaredOnly);
            if (field != null) return o => field.GetValue(o);
        }
        foreach (var i in type.GetInterfaces())
        {
            var prop = i.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (prop != null && prop.GetIndexParameters().Length == 0) return o => prop.GetValue(o);
        }
        return null;
    }
}
