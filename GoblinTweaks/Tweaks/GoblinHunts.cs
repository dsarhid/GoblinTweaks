using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Game;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using GoblinTweaks.Core;
using GoblinTweaks.Hunts;
using GoblinTweaks.Localization;
using GoblinTweaks.Services;
using GoblinTweaks.UI;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.UiOverlay;
using Lumina.Excel.Sheets;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// A small table of the hunts (A and S ranks) and FATEs Sonar announces, newest first, with a button that opens the map
/// with the flag and one that teleports to the nearest aetheryte (or to Ul'dah when the hunt is on another world).
/// </summary>
/// <remarks>
/// A helper of Sonar, not a replacement: it reads Sonar's chat reports and, by reflection, the list Sonar holds in memory
/// (players nearby, HP from afar), plus the HP of the hunt from the game when it is nearby. If Sonar changes and its memory
/// cannot be read, that source switches off quietly and the table keeps what the chat brings. It never connects anywhere.
/// </remarks>
[Tweak(TweakCategory.Other)]
public sealed unsafe class GoblinHunts : Tweak<GoblinHunts.Options>
{
    public sealed class Options
    {
        /// <summary>Position of the table, in pixels from the centre of the screen to its top-left corner.</summary>
        public int OffsetX { get; set; } = -760;
        public int OffsetY { get; set; } = -200;

        /// <summary>How many lines the table shows at most; the wheel scrolls the rest.</summary>
        public int Rows { get; set; } = 8;

        public bool Visible { get; set; } = true;

        /// <summary>A locked table cannot be dragged.</summary>
        public bool Locked { get; set; }

        public HuntFilter Filter { get; set; } = HuntFilter.All;

        /// <summary>Play a sound when a new hunt or FATE appears (one the filter lets through).</summary>
        public bool SoundAlert { get; set; } = true;

        /// <summary>Which kinds sound, each on its own (only while <see cref="SoundAlert"/> is on).</summary>
        public bool SoundS { get; set; } = true;
        public bool SoundA { get; set; } = true;
        public bool SoundFate { get; set; } = true;

        /// <summary>The game's chat sound effect, &lt;se.1&gt; to &lt;se.16&gt;.</summary>
        public int SoundId { get; set; } = 6;

        /// <summary>Opacity of the backgrounds, in percent.</summary>
        public int Opacity { get; set; } = 62;

        /// <summary>Size of the letters, in points.</summary>
        public int FontSize { get; set; } = 14;

        /// <summary>Size of the whole table, in percent.</summary>
        public int Scale { get; set; } = 100;
    }

    private const string SonarSender = "Sonar";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private static readonly ClientLanguage[] Languages = [ClientLanguage.Japanese, ClientLanguage.English, ClientLanguage.German, ClientLanguage.French];
    private static readonly string[] HelpTopics = ["Overview", "Table", "Buttons", "List", "Sources"];

    private HuntList? _list;
    private SonarReader? _reader;
    private OverlayNodes? _overlay;
    private HuntTableNode? _node;
    private HuntsSettingsAddon? _addon;
    private TextHelpAddon? _help;
    private readonly List<HuntEntry> _shown = [];
    private readonly Dictionary<uint, string> _zoneNames = [];
    private readonly Dictionary<uint, string[]> _npcNames = [];
    private int _scroll;
    private bool _primed;
    private bool _dirty = true;
    private bool _chatWarned;
    private DateTime _nextPoll = DateTime.MinValue;
    private DateTime? _saveAt;
    private Vector2? _placed;

    /// <summary>Options of this tweak, for the native settings window. Call <see cref="Changed"/> after editing them.</summary>
    internal Options Config => Settings;

    /// <summary>Localized text of this tweak, for the native windows.</summary>
    internal string Text(string key) => T(key);

    protected internal override void Enable()
    {
        _list = new HuntList();
        _reader = new SonarReader();
        _primed = false;
        _chatWarned = false;
        _scroll = 0;
        _placed = null;
        _dirty = true;
        _nextPoll = DateTime.MinValue;

        _overlay = new OverlayNodes();
        _node = new HuntTableNode
        {
            OnToggleFilter = () =>
            {
                Settings.Filter = HuntFilters.Next(Settings.Filter);
                _scroll = 0;
                Changed();
            },
            OnToggleLock = () =>
            {
                Settings.Locked = !Settings.Locked;
                Changed();
            },
            OnOpenSettings = OpenSettings,
            OnClose = () =>
            {
                Settings.Visible = false;
                Changed();
            },
            OnMap = OpenMap,
            OnTeleport = Teleport,
            OnScroll = direction =>
            {
                _scroll = Math.Max(0, _scroll - direction);
                _dirty = true;
            },
        };
        _overlay.AddNode(_node.Handle);
        _overlay.AddNode(_node);
        ApplyTooltips();

        _addon = NewAddon();

        Svc.Chat.ChatMessage += OnChat;
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Chat.ChatMessage -= OnChat;
        Svc.Framework.Update -= OnUpdate;

        _addon?.Dispose();
        _addon = null;
        _help?.Dispose();
        _help = null;

        // Takes its nodes off the shared overlay and frees them; the overlay itself stays.
        _overlay?.Dispose();
        _overlay = null;
        _node = null;

        _reader?.Reset();
        _reader = null;
        _list = null;
        _shown.Clear();
        FlushSettings();
    }

    protected override IReadOnlyList<string> CommandNames => ["/ghunt", "/goblinhunts"];

    /// <summary>Shows or hides the table.</summary>
    protected override void OnCommand()
    {
        Settings.Visible = !Settings.Visible;
        Changed();
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, OpenHelp);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(Loc.Get("Window.Settings"), TMain("Open.Help"), OpenSettings),
        new(TMain("Button.ResetPosition"), TMain("ResetPosition.Help"), ResetPosition),
    ];

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;
        Rebuild(ref _addon, NewAddon);
        RebuildHelp();
        ApplyTooltips();
        _dirty = true;
    }

    private void ApplyTooltips() => _node?.SetTooltips(
        T("Tooltip.Filter"), T("Tooltip.Lock"), T("Tooltip.Settings"), T("Tooltip.Close"), T("Tooltip.Map"), T("Tooltip.Teleport"));

    private void OpenSettings() => _addon?.Toggle();

    private HuntsSettingsAddon NewAddon() => new()
    {
        InternalName = "GtkHunts",
        Title        = $"{T("Title")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size         = new Vector2(520f, 480f),
        Tweak        = this,
    };

    private void OpenMap(int line)
    {
        if (line < 0 || line >= _shown.Count) return;
        HuntTravel.OpenMap(_shown[line]);
    }

    private void Teleport(int line)
    {
        if (line < 0 || line >= _shown.Count || !Svc.PlayerState.IsLoaded) return;
        var e = _shown[line];
        if (e.Dead) return;

        var world = Svc.PlayerState.CurrentWorld.RowId;
        HuntTravel.SetFlag(e);   // the flag waits on the map when you land
        var result = HuntTravel.Teleport(e, world, Svc.ClientState.TerritoryType, Svc.ClientState.Instance);
        var notice = result switch
        {
            TravelResult.TeleportedOtherWorld or TravelResult.AlreadyInUldah => HuntTravel.OtherRegion(e.WorldId, world)
                ? string.Format(T("Notice.OtherRegion"), e.Name, e.WorldName)
                : string.Format(T("Notice.OtherWorld"), e.Name, e.WorldName),
            TravelResult.TeleportedOtherInstance => string.Format(T("Notice.OtherInstance"), e.Name, e.InstanceId),
            TravelResult.NoAttunedAetheryte      => string.Format(T("Notice.NoAetheryte"), ZoneName(e.ZoneId)),
            TravelResult.Refused                 => T("Notice.Refused"),
            _                                    => null,
        };
        if (notice == null) return;
        Svc.Toasts.ShowNormal(notice);
        Svc.Chat.Print(notice, Name);
    }

    internal void OpenHelp()
    {
        _help ??= NewHelp();
        _help.Toggle();
    }

    private TextHelpAddon NewHelp() => new()
    {
        InternalName = "GtkHuntsHelp",
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

    /// <summary>Whether a new entry of this kind sounds: the alert on, its kind's switch on, and the filter showing it.</summary>
    private bool WantsSound(HuntKind kind)
        => Settings.SoundAlert
           && HuntSounds.Wants(kind, Settings.SoundS, Settings.SoundA, Settings.SoundFate)
           && HuntFilters.Accepts(Settings.Filter, kind);

    /// <summary>Plays the alert sound chosen in the settings.</summary>
    private void PlaySound() => PreviewSound();

    /// <summary>Plays the chosen sound even with the alert off: the settings window lets the user hear it.</summary>
    internal void PreviewSound() => UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(Settings.SoundId, 1, 16));

    internal void ResetPosition()
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

    // ── Sources ───────────────────────────────────────────────────────────────

    /// <summary>A report of Sonar in the chat: text, map link, instance icon, &lt;World&gt;, and "was just killed" or the FATE status.</summary>
    private void OnChat(IHandleableChatMessage message)
    {
        try
        {
            if (_list is not { } list || message.Sender.TextValue != SonarSender) return;

            var payloads = message.Message.Payloads;
            var linkAt = payloads.FindIndex(p => p is MapLinkPayload);
            if (linkAt < 0) return;
            var link = (MapLinkPayload)payloads[linkAt];

            var before = new StringBuilder();
            for (var i = 0; i < linkAt; i++)
                if (payloads[i] is TextPayload { Text: { } text }) before.Append(text);

            var terminator = RawPayload.LinkTerminator.Data;
            var afterStart = linkAt + 1;
            for (var i = linkAt + 1; i < payloads.Count; i++)
            {
                if (payloads[i] is RawPayload raw && raw.Data.AsSpan().SequenceEqual(terminator))
                {
                    afterStart = i + 1;
                    break;
                }
            }
            var after = new StringBuilder();
            for (var i = afterStart; i < payloads.Count; i++)
                if (payloads[i] is TextPayload { Text: { } text }) after.Append(text);

            if (SonarChatParser.Parse(before.ToString(), after.ToString()) is not { } msg) return;

            var worldId = WorldIdOf(msg.WorldName);
            if (worldId == 0) return;

            var report = new HuntReport(msg.Kind, msg.Name, worldId, msg.WorldName, link.TerritoryType.RowId, link.Map.RowId,
                msg.InstanceId, link.XCoord, link.YCoord, msg.Percent, null, msg.Dead, null, HuntSources.Chat);
            var created = list.Upsert(report, DateTime.UtcNow);
            _dirty = true;
            if (created is { Dead: false } && WantsSound(created.Kind)) PlaySound();
        }
        catch (Exception ex)
        {
            // Sonar writes these messages: one it changed must not switch the tweak off. Skip it and say so once.
            if (_chatWarned) return;
            _chatWarned = true;
            Svc.Log.Warning($"GoblinHunts: could not read a Sonar chat message ({ex.Message}); such messages are skipped.");
        }
    }

    private static uint WorldIdOf(string name)
    {
        foreach (var world in Svc.Data.GetExcelSheet<World>())
            if (string.Equals(world.Name.ExtractText(), name, StringComparison.OrdinalIgnoreCase)) return world.RowId;
        return 0;
    }

    private void OnUpdate(IFramework _)
    {
        try
        {
            if (_saveAt is { } saveAt && DateTime.UtcNow >= saveAt) FlushSettings();
            if (_node is not { } node || _list is not { } list || _reader is not { } reader) return;

            PlacePanel(node);

            var now = DateTime.UtcNow;
            if (now >= _nextPoll)
            {
                _nextPoll = now + Poll;
                ReadSonar(list, reader, now);
                ReadLiveHp(list, now);
                list.Expire(now);
                _dirty = true;
            }

            if (!_dirty) return;
            _dirty = false;
            Redraw(node, list);
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private void ReadSonar(HuntList list, SonarReader reader, DateTime now)
    {
        var reports = reader.Read(now);
        if (reports != null)
        {
            // What Sonar already held when the tweak started is not news: no sound for it.
            var created = list.ReplaceMemory(reports, now, announce: _primed);
            _primed = true;
            // Sonar's view also gains old entries on a world change or a login: only what it found just now is news.
            if (created.Any(e => !e.Dead && HuntList.IsNews(e, now) && WantsSound(e.Kind))) PlaySound();
        }
        else if (reader.Status == SonarReadStatus.Failed)
        {
            list.MemoryLost();
        }
        else
        {
            // Sonar not loaded: what came from its memory now follows the list's own timers.
            list.MemoryLost();
            _primed = false;
        }
    }

    /// <summary>The HP of the hunts that are near you, straight from the game.</summary>
    private void ReadLiveHp(HuntList list, DateTime now)
    {
        if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded) return;
        var world = Svc.PlayerState.CurrentWorld.RowId;
        var zone = Svc.ClientState.TerritoryType;
        var instance = Svc.ClientState.Instance;

        List<HuntEntry>? here = null;
        foreach (var e in list.Visible(HuntFilter.All))
        {
            if (e.Kind == HuntKind.Fate || e.Dead || e.WorldId != world || e.ZoneId != zone || e.InstanceId != instance) continue;
            (here ??= []).Add(e);
        }
        if (here == null) return;

        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc npc || npc.BattleNpcKind != BattleNpcSubKind.Combatant || npc.MaxHp == 0) continue;
            var names = NamesOf(npc);
            foreach (var e in here)
            {
                if (!names.Contains(e.Key.Name)) continue;
                list.SetLivePercent(e.Key, npc.CurrentHp * 100f / npc.MaxHp, now);
            }
        }
    }

    /// <summary>The name of an enemy in every language of the client, upper case (Sonar may name it in any of them).</summary>
    private string[] NamesOf(IBattleNpc npc)
    {
        if (_npcNames.TryGetValue(npc.NameId, out var names)) return names;
        var set = new HashSet<string> { npc.Name.TextValue.Trim().ToUpperInvariant() };
        foreach (var lang in Languages)
        {
            var row = Svc.Data.GetExcelSheet<BNpcName>(lang).GetRowOrDefault(npc.NameId);
            var text = row?.Singular.ExtractText();
            if (!string.IsNullOrWhiteSpace(text)) set.Add(text.Trim().ToUpperInvariant());
        }
        names = [.. set];
        _npcNames[npc.NameId] = names;
        return names;
    }

    // ── The table ────────────────────────────────────────────────────────────

    private void Redraw(HuntTableNode node, HuntList list)
    {
        var currentWorld = Svc.PlayerState.IsLoaded ? Svc.PlayerState.CurrentWorld.RowId : 0;
        var worldName = Svc.PlayerState.IsLoaded ? Svc.PlayerState.CurrentWorld.ValueNullable?.Name.ExtractText() ?? string.Empty : string.Empty;
        node.Title = worldName.Length > 0 ? $"{T("Title")} ({worldName})" : T("Title");
        node.FilterLabel = HuntFilters.Label(Settings.Filter);

        var all = list.Visible(Settings.Filter);
        var rows = Math.Clamp(Settings.Rows, 1, HuntTableNode.MaxRows);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, all.Count - rows));

        _shown.Clear();
        _shown.AddRange(all.Skip(_scroll).Take(rows));
        node.SetRows([.. _shown.Select(e => RowOf(e, currentWorld))], _scroll, all.Count);
    }

    private HuntRow RowOf(HuntEntry e, uint currentWorld)
    {
        var state = HuntList.StateOf(e);
        var (badge, badgeColor) = e.Kind switch
        {
            HuntKind.S => ("S", HuntTableNode.BadgeS),
            HuntKind.A => ("A", HuntTableNode.BadgeA),
            _          => ("F", HuntTableNode.BadgeF),
        };
        var bar = state switch
        {
            HuntRowState.Dead   => HuntTableNode.BarRed,
            HuntRowState.Pulled => HuntTableNode.BarOrange,
            HuntRowState.Fate   => HuntTableNode.BarPurple,
            _                   => HuntTableNode.BarGreen,
        };
        var fraction = state is HuntRowState.Dead or HuntRowState.Untouched ? 1f : e.Percent / 100f;
        var percent = e.Dead && e.Kind != HuntKind.Fate ? 0f : e.Percent;
        var zone = ZoneName(e.ZoneId);
        if (e.InstanceId is > 0 and < 10) zone += " " + (char)(0xE0B0 + e.InstanceId);

        return new HuntRow(badge, badgeColor, e.Players is { } p ? $"[{p}]" : string.Empty, e.Name,
            ((int)MathF.Floor(percent)).ToString(CultureInfo.InvariantCulture) + "%", zone, e.WorldName,
            currentWorld != 0 && e.WorldId != currentWorld, fraction, bar, e.Dead, !e.Dead);
    }

    private string ZoneName(uint zoneId)
    {
        if (_zoneNames.TryGetValue(zoneId, out var name)) return name;
        name = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(zoneId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
        _zoneNames[zoneId] = name;
        return name;
    }

    /// <summary>Puts the table where it was left, and takes its new place when the user has dragged it.</summary>
    private void PlacePanel(HuntTableNode node)
    {
        var device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance();
        if (device == null) return;

        var anchor = new Vector2(device->Width / 2f, device->Height / 2f);
        node.Shown    = Settings.Visible && Svc.ClientState.IsLoggedIn;
        node.Locked   = Settings.Locked;
        node.Opacity  = Settings.Opacity / 100f;
        node.FontSize = Settings.FontSize;
        node.Zoom     = Settings.Scale / 100f;

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
    }
}
