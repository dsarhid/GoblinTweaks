using System.Globalization;
using System.Numerics;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Hooking;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GoblinTweaks.Core;
using GoblinTweaks.UI;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.UiOverlay;
using ClassJobCategory = Lumina.Excel.Sheets.ClassJobCategory;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Scrolling battle text in the style of the World of Warcraft addon MikScrollingBattleText:
/// the damage and healing you deal and receive scroll in configurable areas around your
/// character, instead of floating over whoever was hit. A third area under the character
/// announces effects that start or end and actions that come off cooldown.
/// </summary>
/// <remarks>
/// Combat events are read by hooking the game function that creates every fly text
/// (<see cref="BattleLog.AddToScreenLogWithScreenLogKind"/>). Its address comes from
/// FFXIVClientStructs, so the tweak carries no signature of its own. The events shown here are
/// not forwarded to the game, so its own fly text does not show them a second time.
/// </remarks>
[Tweak(TweakCategory.Interface)]
public sealed unsafe class GoblinBattleText : Tweak<GoblinBattleText.Options>
{
    /// <summary>
    /// Fonts of the game, and the italic style it can draw one of them in.
    /// New ones go at the end: the saved settings store the position in this list.
    /// </summary>
    public enum BattleTextFont { Jupiter, Axis, TrumpGothic, Miedinger, TrumpGothicItalic }

    /// <summary>Path of the messages of an area. Static: they do not scroll, they stay in place and fade.</summary>
    public enum BattleTextStyle { Straight, CurvedLeft, CurvedRight, Static }

    /// <summary>How the messages of one event move, whatever the area they are in.</summary>
    public enum BattleTextMotion { Up, Down, Static }

    /// <summary>The scroll areas: right of the character, left of it, and centred under it.</summary>
    public enum BattleTextArea { Outgoing, Incoming, Center }

    /// <summary>The pieces a message is made of; the user chooses their order and which ones show.</summary>
    public enum BattleTextPart { Icon, Type, Name, Number }

    /// <summary>The tabs of the settings window, in the order they are shown; each one can be put back to its defaults.</summary>
    internal enum BattleTextTab { General, Outgoing, Incoming, Center, Events, Cooldowns, Highlights, Colors }

    /// <summary>What a highlighted message does when it appears.</summary>
    /// <remarks>New ones go at the end: the saved settings store the position in this list.</remarks>
    public enum BattleTextAnimation
    {
        None, Pop, Shake, Pulse, Flash, Slam, Quake, Bounce, Swing, Blaze, Thunder,
        Stomp, Stretch, Zoom, Explode, Heartbeat, Spin, Rattle, Drop, Launch, Dash, Recoil, Ignite, Ember, Frost,
        Venom, Blood, Radiance, Shadow, Strobe, Shockwave, Crush, Tornado, Meteor, Earthquake, Fury,
    }

    /// <summary>The messages that can be given a look of their own: the special kinds of hit, and the cooldown alert.</summary>
    public enum BattleTextHighlight { Critical, DirectHit, CriticalDirectHit, CooldownReady }

    /// <summary>The kinds of event; each one is sent to the area the user chooses.</summary>
    public enum BattleTextEvent
    {
        DamageDealt,
        DamageTaken,
        HealDealt,
        HealTaken,
        Mp,
        BuffOnMe,
        BuffOnOthers,
        DebuffOnMe,
        DebuffOnEnemy,
        Cooldown,
    }

    public sealed class AreaOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Position of the area point, in pixels from the centre of the screen.</summary>
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }

        /// <summary>Distance, in pixels, a message scrolls before it disappears.</summary>
        public int Height { get; set; } = 260;

        public int FontSize { get; set; } = 23;

        /// <summary>Time a message takes to scroll the whole area, in tenths of a second.</summary>
        public int DurationTenths { get; set; } = 30;

        public BattleTextStyle Style { get; set; }

        /// <summary>Which point of a message sits on the area point: 0 its left end, 50 its middle, 100 its right end.</summary>
        public int TextAnchor { get; set; }

        /// <summary>Damage and healing below this amount is not shown.</summary>
        public int MinAmount { get; set; }

        /// <summary>Messages shown at the same time; when one more arrives, the oldest goes.</summary>
        public int MaxMessages { get; set; } = BattleTextAreaNode.MaxMessages;

        /// <summary>Left to right order of the parts of the messages of this area.</summary>
        public List<BattleTextPart> Order { get; set; } = [.. DefaultOrder];

        /// <summary>Parts of a message that are not shown in this area.</summary>
        public List<BattleTextPart> Hidden { get; set; } = [BattleTextPart.Name];
    }

    public sealed class EventOptions
    {
        public bool Enabled { get; set; } = true;

        public BattleTextArea Area { get; set; }

        /// <summary>Null in files saved before this option existed: <see cref="Downwards"/> is used then.</summary>
        public BattleTextMotion? Motion { get; set; }

        /// <summary>Replaced by <see cref="Motion"/>; only read from old files.</summary>
        public bool Downwards { get; set; }
    }

    /// <summary>The look of one kind of highlighted message. Turned off, the message looks like any other of its area.</summary>
    public sealed class HighlightOptions
    {
        public bool Enabled { get; set; } = true;

        public BattleTextFont Font { get; set; } = BattleTextFont.Jupiter;

        public int FontSize { get; set; } = 23;

        public BattleTextAnimation Animation { get; set; }

        /// <summary>Strength of the animation, in percent of its normal one.</summary>
        public int Intensity { get; set; } = 100;

        /// <summary>For hits: color of the damage you deal. For the cooldown alert: color of the "ready now!" text.</summary>
        public Vector4 Color { get; set; } = new(1f, 1f, 1f, 1f);
    }

    public sealed class ColorOptions
    {
        public Vector4 OutgoingDamage { get; set; } = DefaultColors.OutgoingDamage;
        public Vector4 IncomingDamage { get; set; } = DefaultColors.IncomingDamage;
        public Vector4 Heal { get; set; } = DefaultColors.Heal;
        public Vector4 Miss { get; set; } = DefaultColors.Miss;
        public Vector4 Mp { get; set; } = DefaultColors.Mp;
        public Vector4 Buff { get; set; } = DefaultColors.Buff;
        public Vector4 BuffEnd { get; set; } = DefaultColors.BuffEnd;
        public Vector4 Debuff { get; set; } = DefaultColors.Debuff;
        public Vector4 DebuffEnd { get; set; } = DefaultColors.DebuffEnd;
        public Vector4 Cooldown { get; set; } = DefaultColors.Cooldown;
    }

    public static class DefaultColors
    {
        public static readonly Vector4 OutgoingDamage    = new(1f, 1f, 1f, 1f);
        public static readonly Vector4 OutgoingCrit      = new(1f, 0.86f, 0.25f, 1f);
        public static readonly Vector4 OutgoingDirectHit = new(1f, 0.72f, 0.42f, 1f);
        public static readonly Vector4 OutgoingCritDirectHit = new(1f, 0.55f, 0.20f, 1f);
        public static readonly Vector4 IncomingDamage    = new(1f, 0.36f, 0.30f, 1f);
        public static readonly Vector4 Heal              = new(0.50f, 1f, 0.55f, 1f);
        public static readonly Vector4 Miss              = new(0.80f, 0.80f, 0.80f, 1f);
        public static readonly Vector4 Mp                = new(0.45f, 0.70f, 1f, 1f);
        public static readonly Vector4 Buff              = new(0.95f, 0.90f, 0.55f, 1f);
        public static readonly Vector4 Debuff            = new(0.45f, 0.85f, 0.90f, 1f);
        public static readonly Vector4 BuffEnd           = new(0.72f, 0.66f, 0.50f, 1f);
        public static readonly Vector4 DebuffEnd         = new(0.50f, 0.62f, 0.68f, 1f);
        public static readonly Vector4 Cooldown          = new(1f, 0.30f, 0.25f, 1f);
        public static readonly Vector4 CooldownText      = new(1f, 1f, 1f, 1f);
    }

    public sealed class Options
    {
        /// <summary>Merge the hits of one action on several targets into a single message.</summary>
        public bool MergeHits { get; set; } = true;

        public bool Abbreviate { get; set; }

        /// <summary>Count what your pet or chocobo deals as yours.</summary>
        public bool IncludePets { get; set; } = true;

        /// <summary>Also announce when a buff or debuff ends, not only when it starts.</summary>
        public bool ShowFading { get; set; } = true;

        public BattleTextFont Font { get; set; } = BattleTextFont.Jupiter;

        /// <summary>Per event: whether it shows, in which area and in which direction. Filled with <see cref="DefaultEvent"/>.</summary>
        public Dictionary<BattleTextEvent, EventOptions> Events { get; set; } = [];

        /// <summary>Order of the parts of a cooldown alert: icon, action name and the "ready now!" text (the Number part).</summary>
        public List<BattleTextPart> CooldownOrder { get; set; } = [.. DefaultCooldownOrder];

        public List<BattleTextPart> CooldownHidden { get; set; } = [];

        /// <summary>Actions whose cooldown is not announced.</summary>
        public List<uint> CooldownsOff { get; set; } = [];

        // Populate: values missing from a saved file keep the defaults given here, instead of the bare ones of the class.
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public HighlightOptions Critical { get; set; } = DefaultHighlight(BattleTextHighlight.Critical);

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public HighlightOptions DirectHit { get; set; } = DefaultHighlight(BattleTextHighlight.DirectHit);

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public HighlightOptions CriticalDirectHit { get; set; } = DefaultHighlight(BattleTextHighlight.CriticalDirectHit);

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public HighlightOptions CooldownReady { get; set; } = DefaultHighlight(BattleTextHighlight.CooldownReady);

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public ColorOptions Colors { get; set; } = new();

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public AreaOptions Outgoing { get; set; } = new() { OffsetX = 150, Style = BattleTextStyle.CurvedRight, TextAnchor = 0 };

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public AreaOptions Incoming { get; set; } = new() { OffsetX = -150, Style = BattleTextStyle.CurvedLeft, TextAnchor = 100 };

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public AreaOptions Center { get; set; } = new() { OffsetY = 150, Height = 110, TextAnchor = 50, Style = BattleTextStyle.Static, MaxMessages = 5 };
    }

    private static readonly BattleTextPart[] DefaultOrder = [BattleTextPart.Icon, BattleTextPart.Type, BattleTextPart.Name, BattleTextPart.Number];

    private static readonly BattleTextPart[] DefaultCooldownOrder = [BattleTextPart.Icon, BattleTextPart.Name, BattleTextPart.Number];

    private const string Command = "/gbt";
    private const byte ActionKindAction = 1;
    private const uint AutoAttackCategory = 1;      // ActionCategory row of auto-attacks
    private const uint DamageTypeIconBase = 60010;  // + 1 physical, + 2 magical, + 3 unique: the icons of the game's own fly text
    private const int  DamageTypeCount = 3;
    private const int  GlobalCooldownGroup = 57;
    private const int  MinCooldown100ms = 50;       // shorter recasts are not worth an alert

    private static readonly TimeSpan SaveDelay       = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan CooldownPoll    = TimeSpan.FromMilliseconds(200);

    private readonly List<(BattleTextArea Area, BattleTextMessage Message)> _pending = [];
    private readonly Dictionary<uint, (string Name, uint IconId)> _actions = [];
    private readonly Dictionary<uint, (string Name, uint IconId)> _statuses = [];

    // Cooldown alerts: the announced actions of the current job, and the ones cooling down right now.
    private readonly List<uint> _watched = [];
    private readonly HashSet<uint> _coolingDown = [];
    private (uint Job, int Level) _watchedFor;
    private DateTime _nextCooldownPoll = DateTime.MinValue;

    private readonly BattleTextAreaNode?[] _areas = new BattleTextAreaNode?[3];

    private Hook<BattleLog.Delegates.AddToScreenLogWithScreenLogKind>? _hook;
    private OverlayController? _overlay;
    private BattleTextAddon? _addon;
    private Exception? _failure;
    private DateTime? _saveAt;
    private DateTime _nextPreview = DateTime.MinValue;
    private int _previewStep;

    /// <summary>Options of this tweak, for the native settings window. Call <see cref="Changed"/> after editing them.</summary>
    internal Options Config => Settings;

    /// <summary>While true, sample messages are shown so the areas can be positioned.</summary>
    internal bool Preview { get; set; }

    /// <summary>Localized text of this tweak, for the native window.</summary>
    internal string Text(string key) => T(key);

    internal static HighlightOptions DefaultHighlight(BattleTextHighlight kind) => kind switch
    {
        BattleTextHighlight.Critical          => new() { FontSize = 31, Animation = BattleTextAnimation.Pop,   Color = DefaultColors.OutgoingCrit },
        BattleTextHighlight.DirectHit         => new() { FontSize = 25, Animation = BattleTextAnimation.None,  Color = DefaultColors.OutgoingDirectHit },
        BattleTextHighlight.CriticalDirectHit => new() { FontSize = 34, Animation = BattleTextAnimation.Slam,  Color = DefaultColors.OutgoingCritDirectHit },
        _                                     => new() { FontSize = 25, Animation = BattleTextAnimation.Pulse, Color = DefaultColors.CooldownText },
    };

    internal HighlightOptions Highlight(BattleTextHighlight kind) => kind switch
    {
        BattleTextHighlight.Critical          => Settings.Critical,
        BattleTextHighlight.DirectHit         => Settings.DirectHit,
        BattleTextHighlight.CriticalDirectHit => Settings.CriticalDirectHit,
        _                                     => Settings.CooldownReady,
    };

    /// <summary>Where each event goes until the user says otherwise.</summary>
    private static EventOptions DefaultEvent(BattleTextEvent type) => type switch
    {
        BattleTextEvent.DamageDealt or BattleTextEvent.HealDealt => new() { Area = BattleTextArea.Outgoing, Motion = BattleTextMotion.Up },
        BattleTextEvent.DamageTaken or BattleTextEvent.HealTaken or BattleTextEvent.Mp => new() { Area = BattleTextArea.Incoming, Motion = BattleTextMotion.Up },
        BattleTextEvent.Cooldown => new() { Area = BattleTextArea.Center, Motion = BattleTextMotion.Down, Enabled = false },
        _ => new() { Area = BattleTextArea.Center, Motion = BattleTextMotion.Down },
    };

    internal EventOptions Event(BattleTextEvent type)
    {
        if (!Settings.Events.TryGetValue(type, out var options))
            Settings.Events[type] = options = DefaultEvent(type);
        return options;
    }

    internal AreaOptions Area(BattleTextArea area) => area switch
    {
        BattleTextArea.Outgoing => Settings.Outgoing,
        BattleTextArea.Incoming => Settings.Incoming,
        _                       => Settings.Center,
    };

    protected internal override void Enable()
    {
        var address = BattleLog.Addresses.AddToScreenLogWithScreenLogKind.Value;
        if (address == 0)
            throw new InvalidOperationException("The game function that creates fly text was not found (game updated?).");

        _failure = null;
        _pending.Clear();
        _watchedFor = default;
        _coolingDown.Clear();
        Repair();

        _overlay = new OverlayController();
        foreach (var area in Enum.GetValues<BattleTextArea>())
        {
            var node = new BattleTextAreaNode { Options = Settings, Area = Area(area), Format = Format };
            _areas[(int)area] = node;
            _overlay.AddNode(node);
        }

        _addon = new BattleTextAddon
        {
            InternalName = "GtkBattleText",
            Title        = T("Title"),
            Size         = new Vector2(780f, 720f),
            Tweak        = this,
        };

        _hook = Svc.GameInterop.HookFromAddress<BattleLog.Delegates.AddToScreenLogWithScreenLogKind>((nint)address, OnScreenLog);
        _hook.Enable();

        Svc.Commands.AddHandler(Command, new CommandInfo((_, _) => _addon?.Toggle()) { HelpMessage = T("Command.Help") });
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Commands.RemoveHandler(Command);

        _hook?.Dispose();
        _hook = null;

        _addon?.Close();
        _addon = null;
        Preview = false;

        // Disposing the controller frees the area nodes it owns.
        _overlay?.Dispose();
        _overlay = null;
        Array.Clear(_areas);

        _pending.Clear();
        FlushSettings();
    }

    public override void DrawSettings()
    {
        // Open the native window (icon hints that a separate window opens).
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ExternalLinkAlt, T("Open")))
            _addon?.Open();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("Open.Help"));

        ImGui.SameLine();
        ImGui.TextColored(Palette.Muted, string.Format(T("Command"), Command));
    }

    /// <summary>The options were edited: they apply at once and are saved once the user stops changing them.</summary>
    internal void Changed() => _saveAt = DateTime.UtcNow + SaveDelay;

    /// <summary>Turns the cooldown alert of one action on or off.</summary>
    internal void SetCooldown(uint actionId, bool announce)
    {
        Settings.CooldownsOff.Remove(actionId);
        if (!announce)
            Settings.CooldownsOff.Add(actionId);

        _watchedFor = default; // rebuild the list of announced cooldowns
        Changed();
    }

    internal void FlushSettings()
    {
        if (_saveAt is null) return;
        _saveAt = null;
        SaveSettings();
    }

    /// <summary>
    /// Puts the settings of one tab back to their defaults. The objects the window and the areas hold
    /// are kept and refilled, so both see the change without being rebuilt.
    /// </summary>
    internal void ResetTab(BattleTextTab tab)
    {
        var defaults = new Options();
        switch (tab)
        {
            case BattleTextTab.General:
                Settings.MergeHits       = defaults.MergeHits;
                Settings.Abbreviate      = defaults.Abbreviate;
                Settings.IncludePets     = defaults.IncludePets;
                Settings.ShowFading      = defaults.ShowFading;
                Settings.Font            = defaults.Font;
                break;

            case BattleTextTab.Outgoing:
                Copy(defaults.Outgoing, Settings.Outgoing);
                break;

            case BattleTextTab.Incoming:
                Copy(defaults.Incoming, Settings.Incoming);
                break;

            case BattleTextTab.Center:
                Copy(defaults.Center, Settings.Center);
                break;

            case BattleTextTab.Events:
                foreach (var type in Enum.GetValues<BattleTextEvent>())
                {
                    if (type != BattleTextEvent.Cooldown)
                        Copy(DefaultEvent(type), Event(type));
                }
                break;

            case BattleTextTab.Cooldowns:
                Copy(DefaultEvent(BattleTextEvent.Cooldown), Event(BattleTextEvent.Cooldown));
                Refill(Settings.CooldownOrder, defaults.CooldownOrder);
                Refill(Settings.CooldownHidden, defaults.CooldownHidden);
                Settings.CooldownsOff.Clear();
                _watchedFor = default; // rebuild the list of announced cooldowns
                break;

            case BattleTextTab.Highlights:
                foreach (var kind in Enum.GetValues<BattleTextHighlight>())
                {
                    var (from, to) = (DefaultHighlight(kind), Highlight(kind));
                    to.Enabled   = from.Enabled;
                    to.Font      = from.Font;
                    to.FontSize  = from.FontSize;
                    to.Animation = from.Animation;
                    to.Intensity = from.Intensity;
                    to.Color     = from.Color;
                }
                break;

            case BattleTextTab.Colors:
                var colors = Settings.Colors;
                colors.OutgoingDamage    = DefaultColors.OutgoingDamage;
                colors.IncomingDamage    = DefaultColors.IncomingDamage;
                colors.Heal              = DefaultColors.Heal;
                colors.Miss              = DefaultColors.Miss;
                colors.Mp                = DefaultColors.Mp;
                colors.Buff              = DefaultColors.Buff;
                colors.Debuff            = DefaultColors.Debuff;
                colors.BuffEnd           = DefaultColors.BuffEnd;
                colors.DebuffEnd         = DefaultColors.DebuffEnd;
                colors.Cooldown          = DefaultColors.Cooldown;
                break;
        }

        Changed();
    }

    private static void Copy(AreaOptions from, AreaOptions to)
    {
        to.Enabled        = from.Enabled;
        to.OffsetX        = from.OffsetX;
        to.OffsetY        = from.OffsetY;
        to.Height         = from.Height;
        to.FontSize       = from.FontSize;
        to.DurationTenths = from.DurationTenths;
        to.Style          = from.Style;
        to.TextAnchor     = from.TextAnchor;
        to.MinAmount      = from.MinAmount;
        to.MaxMessages    = from.MaxMessages;
        Refill(to.Order, from.Order);
        Refill(to.Hidden, from.Hidden);
    }

    private static void Copy(EventOptions from, EventOptions to)
    {
        to.Enabled = from.Enabled;
        to.Area    = from.Area;
        to.Motion  = from.Motion ?? BattleTextMotion.Up;
    }

    private static void Refill<T>(List<T> list, IEnumerable<T> items)
    {
        var copy = items.ToList();
        list.Clear();
        list.AddRange(copy);
    }

    /// <summary>Completes what a saved file may lack: every event, and an order with each part exactly once.</summary>
    private void Repair()
    {
        // A font that is no longer offered falls back to the default one.
        if (!Enum.IsDefined(Settings.Font))
            Settings.Font = BattleTextFont.Jupiter;

        foreach (var kind in Enum.GetValues<BattleTextHighlight>())
        {
            var highlight = Highlight(kind);
            if (!Enum.IsDefined(highlight.Font))
                highlight.Font = BattleTextFont.Jupiter;
        }

        foreach (var type in Enum.GetValues<BattleTextEvent>())
        {
            var options = Event(type);
            options.Motion ??= options.Downwards ? BattleTextMotion.Down : BattleTextMotion.Up;
        }

        foreach (var area in Enum.GetValues<BattleTextArea>())
        {
            var options = Area(area);
            if (options.Order.Count != DefaultOrder.Length || DefaultOrder.Any(part => !options.Order.Contains(part)))
                Refill(options.Order, DefaultOrder);
        }

        if (Settings.CooldownOrder.Count != DefaultCooldownOrder.Length || DefaultCooldownOrder.Any(part => !Settings.CooldownOrder.Contains(part)))
            Settings.CooldownOrder = [.. DefaultCooldownOrder];
    }

    // ── Reading combat events ───────────────────────────────────────────────────

    /// <summary>Runs on the game thread, inside the game's own code: it must never throw.</summary>
    private void OnScreenLog(BattleChara* target, BattleChara* source, int screenLogKind, byte option, byte actionKind, uint actionId, int value1, int value2, int value3)
    {
        var hide = false;
        try
        {
            hide = Capture(target, source, (FlyTextKind)screenLogKind, option, actionKind, actionId, value1, value3);
        }
        catch (Exception ex)
        {
            // Reported from OnUpdate: disabling the tweak here would free the hook that is running.
            _failure ??= ex;
        }

        if (!hide)
            _hook!.Original(target, source, screenLogKind, option, actionKind, actionId, value1, value2, value3);
    }

    /// <summary>Queues the event for its scroll area. Returns false when the event is not one this tweak shows.</summary>
    private bool Capture(BattleChara* target, BattleChara* source, FlyTextKind kind, byte option, byte actionKind, uint actionId, int amount, int damageType)
    {
        var player = (GameObject*)Control.GetLocalPlayer();
        var from   = (GameObject*)source;
        var to     = (GameObject*)target;
        if (player == null || from == null || to == null) return false;

        bool damage = false, heal = false, crit = false, directHit = false, autoAttack = false;
        string? label = null;
        switch (kind)
        {
            case FlyTextKind.MpRegen:
                return to == player && amount > 0 && Queue(BattleTextEvent.Mp, new BattleTextMessage
                {
                    Amount = amount,
                    Prefix = "+",
                    Suffix = " " + T("Label.Mp"),
                    Color  = Settings.Colors.Mp,
                });
            case FlyTextKind.Buff or FlyTextKind.Debuff:
                return CaptureStatus(player, from, to, kind == FlyTextKind.Buff, fading: false, (uint)amount);
            case FlyTextKind.BuffFading or FlyTextKind.DebuffFading:
                return CaptureStatus(player, from, to, kind == FlyTextKind.BuffFading, fading: true, (uint)amount);
            case FlyTextKind.AutoAttackOrDot:
                damage = autoAttack = true;
                break;
            case FlyTextKind.AutoAttackOrDotDh:
                damage = autoAttack = directHit = true;
                break;
            case FlyTextKind.AutoAttackOrDotCrit:
                damage = autoAttack = crit = true;
                break;
            case FlyTextKind.AutoAttackOrDotCritDh:
                damage = autoAttack = crit = directHit = true;
                break;
            case FlyTextKind.Damage:
                damage = true;
                break;
            case FlyTextKind.DamageDh:
                damage = directHit = true;
                break;
            case FlyTextKind.DamageCrit:
                damage = crit = true;
                break;
            case FlyTextKind.DamageCritDh:
                damage = crit = directHit = true;
                break;
            case FlyTextKind.Healing:
                heal = true;
                break;
            case FlyTextKind.HealingCrit:
                heal = crit = true;
                break;
            case FlyTextKind.Miss or FlyTextKind.NamedMiss:
                label = T("Label.Miss");
                break;
            case FlyTextKind.Dodge or FlyTextKind.NamedDodge:
                label = T("Label.Dodge");
                break;
            case FlyTextKind.Invulnerable:
                label = T("Label.Invulnerable");
                break;
            default:
                return false;
        }

        // The tick of a damage over time shares its kind with auto-attacks, and comes with whoever suffers it as its own source.
        var tick    = autoAttack && option == (byte)ScreenLogOption.None && actionKind == 0;
        var ownTick = tick && from == to && to->ObjectKind == ObjectKind.BattleNpc;
        var mine    = from == player || ownTick || IsMyPet(player, from);

        bool outgoing;
        if (to == player) outgoing = false;
        else if (mine)    outgoing = true;
        else              return false;

        // Misses and dodges go with the damage of their direction.
        var type = heal ? (outgoing ? BattleTextEvent.HealDealt : BattleTextEvent.HealTaken)
                        : (outgoing ? BattleTextEvent.DamageDealt : BattleTextEvent.DamageTaken);
        var options = Event(type);
        var area    = Area(options.Area);
        if (!options.Enabled || !area.Enabled) return false;
        if (label is null && amount < area.MinAmount) return true;

        var action = actionKind == ActionKindAction ? LookupAction(actionId) : default;

        // What a monster, a boss or any other NPC does to you shows no action icon; what another player does (PvP) does.
        var fromNpc = !outgoing && !mine && from->ObjectKind != ObjectKind.Pc;

        var suffix =(ScreenLogOption)option switch
        {
            ScreenLogOption.Blocked  => T("Suffix.Blocked"),
            ScreenLogOption.Parried  => T("Suffix.Parried"),
            ScreenLogOption.Resisted => T("Suffix.Resisted"),
            _                        => string.Empty,
        };

        var colors  = Settings.Colors;
        var message = new BattleTextMessage
        {
            Amount     = amount,
            Label      = label,
            ActionName = action.Name,
            IconId     = autoAttack || fromNpc ? 0 : action.IconId,
            TypeIconId = damage && damageType is >= 1 and <= DamageTypeCount ? DamageTypeIconBase + (uint)damageType : 0,
            Prefix     = heal ? "+" : damage && !outgoing ? "-" : string.Empty,
            Suffix     = suffix,
            Color      = label is not null ? colors.Miss
                       : heal              ? colors.Heal
                       : !outgoing         ? colors.IncomingDamage
                       : colors.OutgoingDamage,
            MergeKey   = Settings.MergeHits && label is null && actionId != 0
                       ? ((ulong)actionId << 2) | (heal ? 2u : 0u) | 1u
                       : 0,
        };

        MarkHit(message, crit, directHit, recolor: damage && outgoing);
        return Queue(type, message);
    }

    /// <summary>
    /// Marks a critical and/or direct hit and gives it the look chosen for that kind of hit.
    /// The color of the highlight is only for the damage you deal: damage taken and healing keep theirs.
    /// </summary>
    private void MarkHit(BattleTextMessage message, bool crit, bool directHit, bool recolor)
    {
        if (!crit && !directHit) return;

        message.Crit = crit;
        message.Mark = crit && directHit ? "!!" : crit ? "!" : string.Empty;

        var highlight = Highlight(crit && directHit ? BattleTextHighlight.CriticalDirectHit
                                : crit              ? BattleTextHighlight.Critical
                                : BattleTextHighlight.DirectHit);
        if (!highlight.Enabled) return;

        message.Look = new BattleTextLook(highlight.Font, highlight.FontSize, highlight.Animation, highlight.Intensity);
        if (recolor)
            message.Color = highlight.Color;
    }

    /// <summary>A buff or debuff that starts or ends, on you or put by you on someone else.</summary>
    private bool CaptureStatus(GameObject* player, GameObject* from, GameObject* to, bool buff, bool fading, uint statusId)
    {
        if (fading && !Settings.ShowFading) return false;

        BattleTextEvent type;
        if (to == player)
        {
            type = buff ? BattleTextEvent.BuffOnMe : BattleTextEvent.DebuffOnMe;
        }
        else
        {
            // A debuff ending on an enemy comes with the enemy as its own source; the game only shows yours.
            var mine = from == player || IsMyPet(player, from)
                       || (!buff && from == to && to->ObjectKind == ObjectKind.BattleNpc);
            if (!mine) return false;

            type = buff ? BattleTextEvent.BuffOnOthers : BattleTextEvent.DebuffOnEnemy;
        }

        var status = LookupStatus(statusId);
        if (status.Name is null) return false;

        return Queue(type, new BattleTextMessage
        {
            Label  = (fading ? "- " : "+ ") + status.Name,
            IconId = status.IconId,
            Color  = EffectColor(buff, fading),
            IconIsStatus = true,
        });
    }

    /// <summary>An effect that starts and one that ends have their own colors, besides the + and - before the name.</summary>
    private Vector4 EffectColor(bool buff, bool fading)
    {
        var colors = Settings.Colors;
        return buff ? (fading ? colors.BuffEnd : colors.Buff) : (fading ? colors.DebuffEnd : colors.Debuff);
    }

    private bool IsMyPet(GameObject* player, GameObject* other)
        => Settings.IncludePets && other->ObjectKind == ObjectKind.BattleNpc && other->OwnerId == player->EntityId;

    /// <summary>Sends a message to the area chosen for its event. Returns false when the event or the area is turned off.</summary>
    private bool Queue(BattleTextEvent type, BattleTextMessage message)
    {
        var options = Event(type);
        if (!options.Enabled || !Area(options.Area).Enabled) return false;

        message.Downwards = options.Motion == BattleTextMotion.Down;
        message.Static    = options.Motion == BattleTextMotion.Static;
        _pending.Add((options.Area, message));
        return true;
    }

    private (string? Name, uint IconId) LookupAction(uint actionId)
    {
        if (actionId == 0) return default;

        if (!_actions.TryGetValue(actionId, out var action))
        {
            // Auto-attacks show no icon, whoever makes them: an enemy's come as a normal hit of an "Attack" action.
            action = Svc.Data.GetExcelSheet<LuminaAction>().TryGetRow(actionId, out var row)
                ? (row.Name.ExtractText(), row.ActionCategory.RowId == AutoAttackCategory ? 0u : row.Icon)
                : (string.Empty, 0u);
            _actions[actionId] = action;
        }

        return (action.Name.Length == 0 ? null : action.Name, action.IconId);
    }

    private (string? Name, uint IconId) LookupStatus(uint statusId)
    {
        if (statusId == 0) return default;

        if (!_statuses.TryGetValue(statusId, out var status))
        {
            status = Svc.Data.GetExcelSheet<LuminaStatus>().TryGetRow(statusId, out var row)
                ? (row.Name.ExtractText(), row.Icon)
                : (string.Empty, 0u);
            _statuses[statusId] = status;
        }

        return (status.Name.Length == 0 ? null : status.Name, status.IconId);
    }

    /// <summary>The amount part of a message: the number with its sign and marks, or the label that replaces it.</summary>
    private string Format(BattleTextMessage message)
    {
        if (message.Label is not null)
            return message.Label;

        var amount = message.Amount;
        var number = !Settings.Abbreviate || amount < 10_000 ? amount.ToString("#,0", CultureInfo.InvariantCulture)
                   : amount < 1_000_000                      ? (amount / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K"
                   :                                           (amount / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";

        var hits = message.Hits > 1 ? $" x{message.Hits}" : string.Empty;
        return $"{message.Prefix}{number}{message.Mark}{hits}{message.Suffix}";
    }

    // ── Cooldown alerts ─────────────────────────────────────────────────────────

    /// <summary>
    /// The actions of the current job, at its level, whose cooldown can be announced.
    /// Actions sharing a recast group (an action and its upgrade) count once, as the highest level one.
    /// Must be called on the game thread.
    /// </summary>
    internal List<(uint Id, string Name, uint IconId)> CooldownActions()
    {
        var result  = new List<(uint Id, string Name, uint IconId)>();
        var manager = ActionManager.Instance();
        if (manager == null || !Svc.PlayerState.IsLoaded) return result;

        // ClassJobCategory has one boolean column per job, named after its abbreviation.
        var job    = Svc.PlayerState.ClassJob.ValueNullable;
        var column = job is null ? null : typeof(ClassJobCategory).GetProperty(job.Value.Abbreviation.ExtractText());
        if (column is null) return result;

        var level   = Svc.PlayerState.Level;
        var byGroup = new SortedDictionary<int, LuminaAction>();
        foreach (var action in Svc.Data.GetExcelSheet<LuminaAction>())
        {
            if (!action.IsPlayerAction || action.IsPvP) continue;
            if (action.ClassJobLevel == 0 || action.ClassJobLevel > level) continue;
            if (action.Recast100ms < MinCooldown100ms) continue;
            if (action.ClassJobCategory.ValueNullable is not { } category || column.GetValue(category) is not true) continue;

            var group = manager->GetRecastGroup((int)ActionType.Action, action.RowId);
            if (group < 0 || group == GlobalCooldownGroup) continue;

            if (!byGroup.TryGetValue(group, out var known) || action.ClassJobLevel > known.ClassJobLevel)
                byGroup[group] = action;
        }

        foreach (var action in byGroup.Values.OrderBy(action => action.ClassJobLevel))
        {
            var name = action.Name.ExtractText();
            if (name.Length > 0)
                result.Add((action.RowId, name, action.Icon));
        }

        return result;
    }

    private void PollCooldowns()
    {
        if (!Event(BattleTextEvent.Cooldown).Enabled)
        {
            _watchedFor = default;
            return;
        }

        if (DateTime.UtcNow < _nextCooldownPoll) return;
        _nextCooldownPoll = DateTime.UtcNow + CooldownPoll;

        var manager = ActionManager.Instance();
        if (manager == null || !Svc.PlayerState.IsLoaded) return;

        var watchFor = (Svc.PlayerState.ClassJob.RowId, (int)Svc.PlayerState.Level);
        if (watchFor != _watchedFor)
        {
            _watchedFor = watchFor;
            _watched.Clear();
            _watched.AddRange(CooldownActions().Select(action => action.Id).Where(id => !Settings.CooldownsOff.Contains(id)));
            _coolingDown.IntersectWith(_watched);
        }

        foreach (var actionId in _watched)
        {
            if (manager->IsRecastTimerActive(ActionType.Action, actionId))
                _coolingDown.Add(actionId);
            else if (_coolingDown.Remove(actionId))
                Queue(BattleTextEvent.Cooldown, CooldownMessage(LookupAction(actionId)));
        }
    }

    /// <summary>"Icon, NAME ready now!" by default, with the name in its own color and the look chosen for the alert.</summary>
    private BattleTextMessage CooldownMessage((string? Name, uint IconId) action)
    {
        var highlight = Settings.CooldownReady;
        return new BattleTextMessage
        {
            ActionName = action.Name,
            NameColor  = Settings.Colors.Cooldown,
            Order      = Settings.CooldownOrder,
            Hidden     = Settings.CooldownHidden,
            Label      = T("Cooldown.Ready"),
            IconId     = action.IconId,
            Color      = highlight.Enabled ? highlight.Color : DefaultColors.CooldownText,
            Look       = highlight.Enabled ? new BattleTextLook(highlight.Font, highlight.FontSize, highlight.Animation, highlight.Intensity) : null,
        };
    }

    // ── Every frame ─────────────────────────────────────────────────────────────

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

            if (_overlay is null) return;

            PollCooldowns();

            if (Preview && DateTime.UtcNow >= _nextPreview)
                AddPreview();

            foreach (var (area, message) in _pending)
                _areas[(int)area]?.Add(message);
            _pending.Clear();

            var anchor = Anchor();
            foreach (var area in Enum.GetValues<BattleTextArea>())
            {
                if (_areas[(int)area] is not { } node) continue;

                var options = Area(area);
                if (!options.Enabled)
                    node.Clear();

                node.Position = anchor + new Vector2(options.OffsetX, options.OffsetY);
                SetLanes(node, area, options);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>
    /// Tells an area which kinds of movement its events use, so messages that move differently get
    /// their own space instead of crossing: see <see cref="BattleTextAreaNode.SplitDirections"/>.
    /// </summary>
    private void SetLanes(BattleTextAreaNode node, BattleTextArea area, AreaOptions options)
    {
        bool up = false, down = false, still = false;
        if (options.Style != BattleTextStyle.Static)
        {
            foreach (var (_, settings) in Settings.Events)
            {
                if (!settings.Enabled || settings.Area != area) continue;

                switch (settings.Motion)
                {
                    case BattleTextMotion.Down:   down = true; break;
                    case BattleTextMotion.Static: still = true; break;
                    default:                      up = true; break;
                }
            }
        }

        node.SplitDirections = up && down;
        node.StaticOutside   = still && (up || down);
    }

    /// <summary>The point the areas are placed around: the centre of the screen, where the character normally stands.</summary>
    private static Vector2 Anchor()
    {
        var device = Device.Instance();
        return device == null ? Vector2.Zero : new Vector2(device->Width / 2f, device->Height / 2f);
    }

    /// <summary>Sample messages, one kind of event after another, while the settings window is open.</summary>
    private void AddPreview()
    {
        _nextPreview = DateTime.UtcNow + PreviewInterval;

        var colors = Settings.Colors;
        var action = LookupAction(9);  // Fast Blade: any action with a name and an icon
        var status = LookupStatus(50); // Sprint: any status with a name and an icon

        BattleTextMessage Hit(bool outgoing, bool crit, bool auto, bool directHit = false)
        {
            var message = new BattleTextMessage
            {
                Amount     = crit ? 31_870 : directHit ? 15_900 : 12_345,
                ActionName = action.Name,
                IconId     = auto ? 0 : action.IconId,
                TypeIconId = DamageTypeIconBase + (uint)(outgoing ? 1 : 2),
                Prefix     = outgoing ? string.Empty : "-",
                Color      = outgoing ? colors.OutgoingDamage : colors.IncomingDamage,
            };
            MarkHit(message, crit, directHit, recolor: outgoing);
            return message;
        }

        BattleTextMessage Heal() => new()
        {
            Amount     = 8_420,
            ActionName = action.Name,
            IconId     = action.IconId,
            Prefix     = "+",
            Color      = colors.Heal,
        };

        BattleTextMessage Effect(bool buff, bool fading) => new()
        {
            Label  = (fading ? "- " : "+ ") + status.Name,
            IconId = status.IconId,
            Color  = EffectColor(buff, fading),
            IconIsStatus = true,
        };

        switch (_previewStep++ % 6)
        {
            case 0:
                Queue(BattleTextEvent.DamageDealt, Hit(outgoing: true, crit: false, auto: false));
                Queue(BattleTextEvent.DamageTaken, Hit(outgoing: false, crit: false, auto: false));
                Queue(BattleTextEvent.BuffOnMe, Effect(buff: true, fading: false));
                break;
            case 1:
                Queue(BattleTextEvent.DamageDealt, Hit(outgoing: true, crit: false, auto: true));
                Queue(BattleTextEvent.HealTaken, Heal());
                break;
            case 2:
                Queue(BattleTextEvent.DamageDealt, Hit(outgoing: true, crit: true, auto: false));
                Queue(BattleTextEvent.DamageTaken, Hit(outgoing: false, crit: true, auto: false));
                Queue(BattleTextEvent.DebuffOnEnemy, Effect(buff: false, fading: false));
                break;
            case 3:
                Queue(BattleTextEvent.HealDealt, Heal());
                Queue(BattleTextEvent.Mp, new BattleTextMessage { Amount = 1_000, Prefix = "+", Suffix = " " + T("Label.Mp"), Color = colors.Mp });
                break;
            case 4:
                Queue(BattleTextEvent.DamageDealt, Hit(outgoing: true, crit: false, auto: false, directHit: true));
                Queue(BattleTextEvent.DebuffOnMe, Effect(buff: false, fading: false));
                Queue(BattleTextEvent.Cooldown, CooldownMessage(action));
                break;
            default:
                Queue(BattleTextEvent.DamageDealt, Hit(outgoing: true, crit: true, auto: false, directHit: true));
                Queue(BattleTextEvent.DamageTaken, Hit(outgoing: false, crit: false, auto: true));
                Queue(BattleTextEvent.BuffOnOthers, Effect(buff: true, fading: false));
                if (Settings.ShowFading)
                    Queue(BattleTextEvent.BuffOnMe, Effect(buff: true, fading: true));
                break;
        }
    }
}
