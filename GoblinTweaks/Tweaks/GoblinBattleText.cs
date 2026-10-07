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
/// Scrolling battle text: the damage and healing you deal and receive scroll in configurable areas around your
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
public sealed unsafe class GoblinBattleText : Tweak<GoblinBattleText.Store>
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

    /// <summary>How the damage you take says how much of it was mitigated: not at all, "(-30%)" or "(-30% mitigated)".</summary>
    public enum BattleTextMitigation { Off, Percent, PercentAndWord }

    /// <summary>The scroll areas: right of the character, left of it, and centred under it.</summary>
    public enum BattleTextArea { Outgoing, Incoming, Center }

    /// <summary>The pieces a message is made of; the user chooses their order and which ones show.</summary>
    public enum BattleTextPart { Icon, Type, Name, Number }

    /// <summary>The tabs of the settings window, in the order they are shown; each one can be put back to its defaults.</summary>
    internal enum BattleTextTab { General, Areas, Events, Highlights, Cooldowns }

    /// <summary>What a highlighted message does when it appears.</summary>
    /// <remarks>New ones go at the end: the saved settings store the position in this list.</remarks>
    public enum BattleTextAnimation
    {
        None, Pop, Shake, Pulse, Flash, Slam, Quake, Bounce, Swing, Blaze, Thunder,
        Stomp, Stretch, Zoom, Explode, Heartbeat, Spin, Rattle, Drop, Launch, Dash, Recoil, Ignite, Ember, Frost,
        Venom, Blood, Radiance, Shadow, Strobe, Shockwave, Crush, Tornado, Meteor, Earthquake, Fury,
    }

    /// <summary>The kinds of animation, to choose one without going through all of them.</summary>
    internal enum BattleTextAnimationGroup { Gentle, Impacts, Size, Rotation, Movement, Light }

    /// <summary>Which way the gradient of a positional alert goes.</summary>
    public enum BattleTextGradientDirection { Horizontal, Vertical }

    /// <summary>The messages that can be given a look of their own: the special kinds of hit, and the cooldown alert.</summary>
    public enum BattleTextHighlight { Critical, DirectHit, CriticalDirectHit, CooldownReady, PositionalHit, PositionalMiss }

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

        /// <summary>An action of yours that deals no damage and heals nothing, like a dash: the game shows no text for it.</summary>
        ActionUsed,

        /// <summary>An action of yours that hits harder from the rear or the flank: whether you hit it from there.</summary>
        Positional,
    }

    /// <summary>What a status does every few seconds, if anything.</summary>
    private enum OverTime : byte { None, Damage, Healing }

    /// <summary>Whose hit a critical or direct one is: each can have its own color.</summary>
    private enum HitSource { Dealt, Taken, Heal }

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

        /// <summary>For hits: color of the damage you take.</summary>
        public Vector4 ColorTaken { get; set; } = DefaultColors.IncomingDamage;

        /// <summary>For critical hits: color of healing, the only other thing that can be critical.</summary>
        public Vector4 ColorHeal { get; set; } = DefaultColors.Heal;

        /// <summary>For the positional alerts: the text goes from <see cref="Color"/> to <see cref="ColorEnd"/>, letter by letter.</summary>
        public bool Gradient { get; set; }

        /// <summary>For the positional alerts: left to right, or top to bottom.</summary>
        public BattleTextGradientDirection GradientDirection { get; set; }

        /// <summary>For the positional alerts: the color the gradient ends on.</summary>
        public Vector4 ColorEnd { get; set; } = new(1f, 1f, 1f, 1f);
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
        public Vector4 Action { get; set; } = DefaultColors.Action;
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
        public static readonly Vector4 Action            = new(0.80f, 0.86f, 1f, 1f);
        public static readonly Vector4 PositionalHit     = new(0.55f, 1f, 0.60f, 1f);
        public static readonly Vector4 PositionalMiss    = new(1f, 0.35f, 0.30f, 1f);
    }

    /// <summary>
    /// What is saved: the options themselves, which are those of a character that has not been seen yet (and of
    /// the title screen), and the options of each character that has, by its id.
    /// </summary>
    /// <remarks>
    /// It is the options with one thing more, so the file of someone who used the tweak before characters had
    /// their own reads as it is: what they had becomes what every character of theirs starts from.
    /// </remarks>
    public sealed class Store : Options
    {
        public Dictionary<ulong, Options> Characters { get; set; } = [];
    }

    public class Options
    {
        /// <summary>Merge the hits of one action on several targets into a single message.</summary>
        public bool MergeHits { get; set; } = true;

        /// <summary>The same for healing: one action that heals several targets shows a single total.</summary>
        public bool MergeHeals { get; set; } = true;

        /// <summary>Merge the misses, dodges and invulnerable hits of one action: "Miss x3".</summary>
        public bool MergeMisses { get; set; }

        /// <summary>Merge the blocked, parried and resisted hits of one action, each kind with its own kind.</summary>
        public bool MergeDefended { get; set; } = true;

        /// <summary>Merge the effects that did not take (immune, fully resisted) of one action: "Leg Sweep Immune x3".</summary>
        public bool MergeNoEffect { get; set; }

        /// <summary>Merge the ticks of one damage or healing over time on several targets: "132 Higanbana x3".</summary>
        public bool MergeTicks { get; set; }

        /// <summary>Healing you give says who it heals. With <see cref="MergeHeals"/>, only where there is one target: the ticks.</summary>
        public bool ShowHealTargets { get; set; }

        public bool Abbreviate { get; set; }

        /// <summary>Count what your pet or chocobo deals as yours.</summary>
        public bool IncludePets { get; set; } = true;

        /// <summary>Whether damage you take says how much of it your damage reduction effects took off, and how.</summary>
        public BattleTextMitigation Mitigation { get; set; } = BattleTextMitigation.PercentAndWord;

        /// <summary>A hit that was blocked, parried or resisted says so after its number.</summary>
        public bool ShowDefended { get; set; } = true;

        /// <summary>Also announce when a buff or debuff ends, not only when it starts.</summary>
        public bool ShowFading { get; set; } = true;

        /// <summary>
        /// Say whether a positional was hit in the message of the damage of its action, instead of in an alert of its own.
        /// </summary>
        public bool PositionalInline { get; set; }

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
        public HighlightOptions PositionalHit { get; set; } = DefaultHighlight(BattleTextHighlight.PositionalHit);

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public HighlightOptions PositionalMiss { get; set; } = DefaultHighlight(BattleTextHighlight.PositionalMiss);

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

    /// <summary>How far from the centre of the screen an area can be placed, in pixels.</summary>
    internal const int OffsetLimitX = 900;
    internal const int OffsetLimitY = 600;

    private const string Command = "/gbt";
    private const byte ActionKindAction = 1;
    private const uint AutoAttackCategory = 1;      // ActionCategory row of auto-attacks
    private const uint AttackAction = 7;            // the auto-attack of a melee job
    private const uint ShotAction   = 8;            // and that of a ranged one
    private const uint DamageTypeIconBase = 60010;  // + 1 physical, + 2 magical, + 3 unique: the icons of the game's own fly text
    private const int  DamageTypeCount = 3;
    private const int  GlobalCooldownGroup = 57;       // as the game counts recast groups, from 0; its sheets count from 1
    private const int  MinCooldown100ms = 50;       // shorter recasts are not worth an alert
    private const byte BeneficialStatus  = 1;       // StatusCategory of buffs
    private const byte DetrimentalStatus = 2;       // StatusCategory of debuffs
    private const int  MaxTrackedDebuffs = 512;

    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DebuffWatch   = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DebuffGrace   = TimeSpan.FromSeconds(1);   // for a new debuff to show in the list

    private static readonly TimeSpan SaveDelay       = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan PreviewSoon     = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan CooldownPoll    = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CooldownSettle  = TimeSpan.FromSeconds(2);

    private readonly List<(BattleTextArea Area, BattleTextMessage Message)> _pending = [];
    private readonly Dictionary<uint, (string Name, uint IconId)> _actions = [];
    private readonly Dictionary<uint, (string Name, uint IconId)> _statuses = [];
    private readonly Dictionary<uint, OverTime> _overTime = [];
    private readonly HashSet<uint> _overTimeSeen = [];

    // What the actions of the game say their effects do, by the name of each effect in English, as read at one
    // level; and the same by status, as they are asked for. See ActionEffects.
    private readonly Dictionary<string, ActionEffects.Effect> _effects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, ActionEffects.Effect?> _statusEffects = [];
    private readonly HashSet<uint> _mitigationSeen = [];
    private int _effectsLevel = -1;

    // Debuffs of yours on enemies, as (enemy, status): each enemy's list of statuses says who put each one,
    // so they are watched there until they go. "Seen" is false until the list shows the debuff.
    // And the ones that arrived before that list could say whose they were.
    private readonly Dictionary<ulong, (bool Seen, DateTime Since)> _myDebuffs = [];
    private readonly List<ulong> _debuffKeys = [];
    private readonly List<(uint Target, uint StatusId, DateTime Until)> _unconfirmed = [];
    private DateTime _nextDebuffWatch = DateTime.MinValue;

    // Cooldown alerts: the announced actions of the current job, and the ones cooling down right now.
    private readonly List<uint> _watched = [];
    private readonly HashSet<uint> _coolingDown = [];
    private (uint Job, int Level) _watchedFor;
    private DateTime _nextCooldownPoll = DateTime.MinValue;
    private DateTime _cooldownsSettle = DateTime.MinValue;

    private readonly BattleTextAreaNode?[] _areas = new BattleTextAreaNode?[3];

    // While the settings window is open each area has a handle to drag it by, and where each one was last put.
    private readonly BattleTextHandleNode?[] _handles = new BattleTextHandleNode?[3];
    private readonly Vector2?[] _handlePlaced = new Vector2?[3];
    private BattleTextArea? _movedArea;

    private Hook<BattleLog.Delegates.AddToScreenLogWithScreenLogKind>? _hook;
    private Hook<ActionEffectHandler.Delegates.Receive>? _actionHook;
    private Hook<StatusManager.Delegates.ProcessHotDot>? _tickHook;

    // The tick the game is working out right now, while it does: the status it is of and who put it there,
    // when the game says so. It does for what ticks from the ground (Doton, Salted Earth...), which is on no
    // list of statuses of the enemy; for the rest it sends one sum with no status and no source.
    private (uint StatusId, uint SourceId)? _tickSource;

    private const uint NoEntity = 0xE0000000;
    private readonly Dictionary<uint, bool> _playerActions = [];
    private OverlayController? _overlay;
    private BattleTextAddon? _addon;
    private Exception? _failure;
    private DateTime? _saveAt;
    private DateTime _nextPreview = DateTime.MinValue;
    private int _previewStep;
    private BattleTextHighlight _previewHighlight;

    /// <summary>Options of this tweak, for the native settings window. Call <see cref="Changed"/> after editing them.</summary>
    internal Options Config => Settings;

    // The options in use: those of the character logged in, or the ones every character starts from.
    private Options? _character;
    private ulong _characterId;

    /// <summary>
    /// The options of the character logged in. Everything in this tweak reads these: each character has its
    /// own areas, events, looks, colors and cooldowns left out.
    /// </summary>
    private new Options Settings => _character ?? base.Settings;

    private static readonly System.Text.Json.JsonSerializerOptions CopyOptions = new() { IncludeFields = true };

    private static ulong CurrentCharacter => Svc.PlayerState.IsLoaded ? Svc.PlayerState.ContentId : 0;

    /// <summary>
    /// Takes the options of the character logged in. One seen for the first time starts with a copy of the
    /// ones every character starts from, and is on its own from then on.
    /// </summary>
    private void SelectCharacter()
    {
        _characterId = CurrentCharacter;
        _character   = null;
        if (_characterId == 0) return;

        var store = base.Settings;
        if (!store.Characters.TryGetValue(_characterId, out var options))
        {
            // As Options, so the copy does not carry the characters along. With fields, as the settings are
            // saved: the parts of a color are fields, and without them every color would come out empty.
            options = System.Text.Json.JsonSerializer.Deserialize<Options>(
                System.Text.Json.JsonSerializer.Serialize<Options>(store, CopyOptions), CopyOptions) ?? new Options();
            store.Characters[_characterId] = options;
            Changed();
        }

        _character = options;
    }

    /// <summary>While true, sample messages are shown so the areas can be positioned.</summary>
    internal bool Preview { get; set; }

    /// <summary>Called when the user finishes dragging an area by its handle, so the window shows its new position.</summary>
    internal Action<BattleTextArea>? AreaMoved { get; set; }

    internal static BattleTextAnimationGroup GroupOf(BattleTextAnimation animation) => animation switch
    {
        BattleTextAnimation.Slam or BattleTextAnimation.Quake or BattleTextAnimation.Earthquake or BattleTextAnimation.Stomp
            or BattleTextAnimation.Crush or BattleTextAnimation.Shockwave or BattleTextAnimation.Meteor or BattleTextAnimation.Fury
            => BattleTextAnimationGroup.Impacts,
        BattleTextAnimation.Bounce or BattleTextAnimation.Zoom or BattleTextAnimation.Stretch or BattleTextAnimation.Explode
            or BattleTextAnimation.Heartbeat
            => BattleTextAnimationGroup.Size,
        BattleTextAnimation.Swing or BattleTextAnimation.Rattle or BattleTextAnimation.Spin or BattleTextAnimation.Tornado
            => BattleTextAnimationGroup.Rotation,
        BattleTextAnimation.Drop or BattleTextAnimation.Launch or BattleTextAnimation.Dash or BattleTextAnimation.Recoil
            => BattleTextAnimationGroup.Movement,
        BattleTextAnimation.Blaze or BattleTextAnimation.Thunder or BattleTextAnimation.Ignite or BattleTextAnimation.Ember
            or BattleTextAnimation.Frost or BattleTextAnimation.Venom or BattleTextAnimation.Blood or BattleTextAnimation.Radiance
            or BattleTextAnimation.Shadow or BattleTextAnimation.Strobe
            => BattleTextAnimationGroup.Light,
        _ => BattleTextAnimationGroup.Gentle,
    };

    /// <summary>The animations of one kind, after "None", which every kind offers.</summary>
    internal static List<BattleTextAnimation> AnimationsOf(BattleTextAnimationGroup group)
        => [BattleTextAnimation.None, .. Enum.GetValues<BattleTextAnimation>().Where(animation => animation != BattleTextAnimation.None && GroupOf(animation) == group)];

    /// <summary>
    /// Gives one area the look of another: everything but where it is, its path and its alignment,
    /// which belong to the side of the character each area is on.
    /// </summary>
    internal void CopyAreaLook(BattleTextArea from, BattleTextArea to)
    {
        var (source, target) = (Area(from), Area(to));
        if (ReferenceEquals(source, target)) return;

        target.Height         = source.Height;
        target.FontSize       = source.FontSize;
        target.DurationTenths = source.DurationTenths;
        target.MinAmount      = source.MinAmount;
        target.MaxMessages    = source.MaxMessages;
        Refill(target.Order, source.Order);
        Refill(target.Hidden, source.Hidden);
        Changed();
    }

    /// <summary>The tab of the settings window on screen: the sample messages are the ones that tab is about.</summary>
    internal BattleTextTab PreviewTab { get; set; }

    /// <summary>
    /// The look of a highlighted message was chosen or edited in the window: a sample of it is shown as
    /// soon as the user stops changing it, and it is the kind the Highlights tab keeps showing.
    /// </summary>
    internal void PreviewHighlightSoon(BattleTextHighlight kind)
    {
        if (kind != BattleTextHighlight.CooldownReady)
            _previewHighlight = kind;
        _nextPreview = DateTime.UtcNow + PreviewSoon;
    }

    /// <summary>
    /// Whether a part can show in the messages of an area, given the events sent to it: only damage
    /// has a type, effects and MP have no action name, and MP has no icon either.
    /// </summary>
    internal bool PartApplies(BattleTextArea area, BattleTextPart part)
    {
        foreach (var type in Enum.GetValues<BattleTextEvent>())
        {
            // Cooldown and positional alerts have an order of their own.
            if (type is BattleTextEvent.Cooldown or BattleTextEvent.Positional || Event(type).Area != area) continue;

            var applies = type switch
            {
                BattleTextEvent.DamageDealt or BattleTextEvent.DamageTaken => true,
                BattleTextEvent.HealDealt or BattleTextEvent.HealTaken     => part != BattleTextPart.Type,
                BattleTextEvent.Mp                                         => part == BattleTextPart.Number,
                _                                                          => part is BattleTextPart.Icon or BattleTextPart.Number,
            };
            if (applies) return true;
        }

        return false;
    }

    /// <summary>Localized text of this tweak, for the native window.</summary>
    internal string Text(string key) => T(key);

    internal static HighlightOptions DefaultHighlight(BattleTextHighlight kind) => kind switch
    {
        BattleTextHighlight.Critical          => new() { FontSize = 31, Animation = BattleTextAnimation.Pop,   Color = DefaultColors.OutgoingCrit },
        BattleTextHighlight.DirectHit         => new() { FontSize = 25, Animation = BattleTextAnimation.None,  Color = DefaultColors.OutgoingDirectHit },
        BattleTextHighlight.CriticalDirectHit => new() { FontSize = 34, Animation = BattleTextAnimation.Slam,  Color = DefaultColors.OutgoingCritDirectHit },
        BattleTextHighlight.PositionalHit     => new() { FontSize = 25, Animation = BattleTextAnimation.Pop,   Color = DefaultColors.PositionalHit,  ColorEnd = new(0.25f, 0.90f, 0.95f, 1f) },
        BattleTextHighlight.PositionalMiss    => new() { FontSize = 30, Animation = BattleTextAnimation.Shake, Color = DefaultColors.PositionalMiss, ColorEnd = new(1f, 0.70f, 0.20f, 1f) },
        _                                     => new() { FontSize = 25, Animation = BattleTextAnimation.Pulse, Color = DefaultColors.CooldownText },
    };

    internal HighlightOptions Highlight(BattleTextHighlight kind) => kind switch
    {
        BattleTextHighlight.Critical          => Settings.Critical,
        BattleTextHighlight.DirectHit         => Settings.DirectHit,
        BattleTextHighlight.CriticalDirectHit => Settings.CriticalDirectHit,
        BattleTextHighlight.PositionalHit     => Settings.PositionalHit,
        BattleTextHighlight.PositionalMiss    => Settings.PositionalMiss,
        _                                     => Settings.CooldownReady,
    };

    /// <summary>Where each event goes until the user says otherwise.</summary>
    private static EventOptions DefaultEvent(BattleTextEvent type) => type switch
    {
        BattleTextEvent.DamageDealt or BattleTextEvent.HealDealt => new() { Area = BattleTextArea.Outgoing, Motion = BattleTextMotion.Up },
        BattleTextEvent.DamageTaken or BattleTextEvent.HealTaken or BattleTextEvent.Mp => new() { Area = BattleTextArea.Incoming, Motion = BattleTextMotion.Up },
        BattleTextEvent.Cooldown => new() { Area = BattleTextArea.Center, Motion = BattleTextMotion.Down, Enabled = false },
        BattleTextEvent.ActionUsed => new() { Area = BattleTextArea.Outgoing, Motion = BattleTextMotion.Up },
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

        SelectCharacter();

        _failure = null;
        _pending.Clear();
        _watchedFor = default;
        _coolingDown.Clear();
        _myDebuffs.Clear();
        _unconfirmed.Clear();
        _positionalVerdicts.Clear();
        _effectsLevel = -1;
        Repair();

        _overlay = new OverlayController();
        foreach (var area in Enum.GetValues<BattleTextArea>())
        {
            var node = new BattleTextAreaNode { Options = Settings, Area = Area(area), Format = Format };
            _areas[(int)area] = node;
            _overlay.AddNode(node);

            var moved  = area;
            var handle = new BattleTextHandleNode { Label = T($"AreaName.{area}") };
            handle.OnMoveComplete = _ => _movedArea = moved;
            _handles[(int)area]      = handle;
            _handlePlaced[(int)area] = null;
            _overlay.AddNode(handle);
        }

        _addon = new BattleTextAddon
        {
            InternalName = "GtkBattleText",
            Title        = T("Title"),
            Subtitle     = _characterId == 0 ? null : Svc.PlayerState.CharacterName,
            Size         = new Vector2(780f, 640f),
            Tweak        = this,
        };

        _hook = Svc.GameInterop.HookFromAddress<BattleLog.Delegates.AddToScreenLogWithScreenLogKind>((nint)address, OnScreenLog);
        _hook.Enable();

        // Actions that deal no damage make no fly text: they are read from the effects of each action instead.
        // Without the function, only that kind of event is missing.
        var actionAddress = ActionEffectHandler.Addresses.Receive.Value;
        if (actionAddress != 0)
        {
            _actionHook = Svc.GameInterop.HookFromAddress<ActionEffectHandler.Delegates.Receive>((nint)actionAddress, OnActionEffect);
            _actionHook.Enable();
        }

        // Without this one, a tick is only told apart by the statuses of whoever it is on.
        var tickAddress = StatusManager.Addresses.ProcessHotDot.Value;
        if (tickAddress != 0)
        {
            _tickHook = Svc.GameInterop.HookFromAddress<StatusManager.Delegates.ProcessHotDot>((nint)tickAddress, OnTick);
            _tickHook.Enable();
        }

        Svc.Commands.AddHandler(Command, new CommandInfo((_, _) => _addon?.Toggle()) { HelpMessage = T("Command.Help") });
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        Svc.Commands.RemoveHandler(Command);

        _hook?.Dispose();
        _hook = null;
        _actionHook?.Dispose();
        _actionHook = null;
        _tickHook?.Dispose();
        _tickHook = null;
        _tickSource = null;

        _addon?.Close();
        _addon = null;
        Preview = false;

        // A handle stops listening to the mouse before it goes.
        foreach (var handle in _handles)
            handle?.Shown = false;

        // Disposing the controller frees the nodes it owns.
        _overlay?.Dispose();
        _overlay = null;
        Array.Clear(_areas);
        Array.Clear(_handles);
        Array.Clear(_handlePlaced);
        _movedArea = null;
        AreaMoved  = null;

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
    /// <param name="area">The area on screen in the Areas tab, the only one that tab puts back.</param>
    internal void ResetTab(BattleTextTab tab, BattleTextArea area)
    {
        var defaults = new Options();
        var colors   = Settings.Colors;
        switch (tab)
        {
            case BattleTextTab.General:
                Settings.MergeHits   = defaults.MergeHits;
                Settings.MergeHeals  = defaults.MergeHeals;
                Settings.MergeMisses = defaults.MergeMisses;
                Settings.MergeDefended = defaults.MergeDefended;
                Settings.MergeNoEffect = defaults.MergeNoEffect;
                Settings.MergeTicks  = defaults.MergeTicks;
                Settings.ShowHealTargets = defaults.ShowHealTargets;
                Settings.Abbreviate  = defaults.Abbreviate;
                Settings.IncludePets = defaults.IncludePets;
                Settings.Mitigation  = defaults.Mitigation;
                Settings.ShowDefended = defaults.ShowDefended;
                Settings.Font       = defaults.Font;

                colors.OutgoingDamage = DefaultColors.OutgoingDamage;
                colors.IncomingDamage = DefaultColors.IncomingDamage;
                colors.Heal           = DefaultColors.Heal;
                colors.Miss           = DefaultColors.Miss;
                colors.Mp             = DefaultColors.Mp;
                colors.Buff           = DefaultColors.Buff;
                colors.Debuff         = DefaultColors.Debuff;
                colors.BuffEnd        = DefaultColors.BuffEnd;
                colors.DebuffEnd      = DefaultColors.DebuffEnd;
                colors.Action         = DefaultColors.Action;
                break;

            case BattleTextTab.Areas:
                Copy(area switch
                {
                    BattleTextArea.Outgoing => defaults.Outgoing,
                    BattleTextArea.Incoming => defaults.Incoming,
                    _                       => defaults.Center,
                }, Area(area));
                break;

            case BattleTextTab.Events:
                Settings.ShowFading = defaults.ShowFading;
                Settings.PositionalInline = defaults.PositionalInline;
                foreach (var type in Enum.GetValues<BattleTextEvent>())
                {
                    if (type != BattleTextEvent.Cooldown)
                        Copy(DefaultEvent(type), Event(type));
                }
                break;

            case BattleTextTab.Highlights:
                foreach (var kind in Enum.GetValues<BattleTextHighlight>())
                {
                    if (kind != BattleTextHighlight.CooldownReady)
                        Copy(DefaultHighlight(kind), Highlight(kind));
                }
                break;

            case BattleTextTab.Cooldowns:
                Copy(DefaultEvent(BattleTextEvent.Cooldown), Event(BattleTextEvent.Cooldown));
                Copy(DefaultHighlight(BattleTextHighlight.CooldownReady), Settings.CooldownReady);
                colors.Cooldown = DefaultColors.Cooldown;
                Refill(Settings.CooldownOrder, defaults.CooldownOrder);
                Refill(Settings.CooldownHidden, defaults.CooldownHidden);
                Settings.CooldownsOff.Clear();
                _watchedFor = default; // rebuild the list of announced cooldowns
                break;
        }

        Changed();
    }

    private static void Copy(HighlightOptions from, HighlightOptions to)
    {
        to.Enabled   = from.Enabled;
        to.Font      = from.Font;
        to.FontSize  = from.FontSize;
        to.Animation = from.Animation;
        to.Intensity  = from.Intensity;
        to.Color      = from.Color;
        to.ColorTaken = from.ColorTaken;
        to.ColorHeal  = from.ColorHeal;
        to.Gradient   = from.Gradient;
        to.GradientDirection = from.GradientDirection;
        to.ColorEnd   = from.ColorEnd;
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

    /// <summary>
    /// Gives back its color to anything left with none. A color with no red, green, blue or opacity is not one
    /// the window can set: it is what a color that was lost reads as, and it shows as black text.
    /// </summary>
    private void RepairColors()
    {
        var from = base.Settings;
        var (colors, source) = (Settings.Colors, from.Colors);

        static Vector4 Kept(Vector4 color, Vector4 fromBase, Vector4 byDefault)
            => color != default ? color : fromBase != default ? fromBase : byDefault;

        colors.OutgoingDamage = Kept(colors.OutgoingDamage, source.OutgoingDamage, DefaultColors.OutgoingDamage);
        colors.IncomingDamage = Kept(colors.IncomingDamage, source.IncomingDamage, DefaultColors.IncomingDamage);
        colors.Heal           = Kept(colors.Heal,           source.Heal,           DefaultColors.Heal);
        colors.Miss           = Kept(colors.Miss,           source.Miss,           DefaultColors.Miss);
        colors.Mp             = Kept(colors.Mp,             source.Mp,             DefaultColors.Mp);
        colors.Buff           = Kept(colors.Buff,           source.Buff,           DefaultColors.Buff);
        colors.BuffEnd        = Kept(colors.BuffEnd,        source.BuffEnd,        DefaultColors.BuffEnd);
        colors.Debuff         = Kept(colors.Debuff,         source.Debuff,         DefaultColors.Debuff);
        colors.DebuffEnd      = Kept(colors.DebuffEnd,      source.DebuffEnd,      DefaultColors.DebuffEnd);
        colors.Cooldown       = Kept(colors.Cooldown,       source.Cooldown,       DefaultColors.Cooldown);
        colors.Action         = Kept(colors.Action,         source.Action,         DefaultColors.Action);

        foreach (var kind in Enum.GetValues<BattleTextHighlight>())
        {
            var highlight = Highlight(kind);
            var byDefault = DefaultHighlight(kind);
            var fromBase  = kind switch
            {
                BattleTextHighlight.Critical          => from.Critical,
                BattleTextHighlight.DirectHit         => from.DirectHit,
                BattleTextHighlight.CriticalDirectHit => from.CriticalDirectHit,
                BattleTextHighlight.PositionalHit     => from.PositionalHit,
                BattleTextHighlight.PositionalMiss    => from.PositionalMiss,
                _                                     => from.CooldownReady,
            };

            highlight.Color      = Kept(highlight.Color,      fromBase.Color,      byDefault.Color);
            highlight.ColorTaken = Kept(highlight.ColorTaken, fromBase.ColorTaken, byDefault.ColorTaken);
            highlight.ColorHeal  = Kept(highlight.ColorHeal,  fromBase.ColorHeal,  byDefault.ColorHeal);
        }
    }

    /// <summary>Completes what a saved file may lack: every event, and an order with each part exactly once.</summary>
    private void Repair()
    {
        RepairColors();

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
        if (player == null || to == null) return false;

        // What happens to you may come without a source; what happens to others is only yours if it has one.
        if (from == null && to != player) return false;

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
            // HP drain is what an attack heals you for under an effect like Bloodbath: the game draws it as healing.
            case FlyTextKind.Healing or FlyTextKind.HpDrain:
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

            // An effect that did not take: the enemy is immune to it, resisted it or cannot be touched now.
            case FlyTextKind.DebuffNoEffect or FlyTextKind.HasNoEffect:
                return CaptureNoEffect(player, from, to, "Label.Immune", kind == FlyTextKind.DebuffNoEffect ? (uint)amount : 0, actionKind, actionId);
            case FlyTextKind.DebuffResisted or FlyTextKind.FullyResisted:
                return CaptureNoEffect(player, from, to, "Label.FullResist", kind == FlyTextKind.DebuffResisted ? (uint)amount : 0, actionKind, actionId);
            case FlyTextKind.DebuffInvulnerable:
                return CaptureNoEffect(player, from, to, "Label.Invulnerable", (uint)amount, actionKind, actionId);
            default:
                return false;
        }

        // The tick of a damage over time shares its kind with auto-attacks. What tells them apart is its source:
        // a tick comes with whoever suffers it as its own source, an auto-attack with whoever attacks.
        var tick    = autoAttack && option == (byte)ScreenLogOption.None && actionKind == 0 && from == to;
        var ownTick = tick && to->ObjectKind == ObjectKind.BattleNpc;

        // The tick of a healing over time comes the same way: no action, and whoever is healed as its own source.
        // On you it is healing you receive, whoever gave it. On someone else, your pet included, it is yours only
        // when they have a healing over time that you, or a pet of yours, put on them: their list of statuses says so.
        var healTick = heal && actionKind == 0 && from == to;

        // A tick comes with no action: the statuses behind it are looked for on whoever has them.
        var over = tick || healTick ? FindOverTime(player, to, heal ? OverTime.Healing : OverTime.Damage, mineOnly: to != player) : default;

        // The game sends one tick per target with the sum of everyone's: there is no telling yours apart.
        // On someone else, a tick is not yours when none of what ticks is; and when others have theirs there
        // too, your share is taken as one part per status, which is exact only if they are all as strong.
        var estimated = false;
        var ticking   = tick || healTick ? _tickSource : null;

        if (ticking is { SourceId: not (0 or NoEntity) } told)
        {
            // The game says whose tick it is: no guessing, and no sharing it out.
            var manager = GameObjectManager.Instance();
            var yours   = told.SourceId == player->EntityId
                          || IsMyPet(player, manager == null ? null : manager->Objects.GetObjectByEntityId(told.SourceId));
            if (to != player && !yours)
            {
                if (healTick) return false;

                var dealt = Event(BattleTextEvent.DamageDealt);
                return dealt.Enabled && Area(dealt.Area).Enabled;
            }

            var named = told.StatusId != 0 ? LookupStatus(told.StatusId) : default;
            over = (named.Name ?? over.Name, named.Name is null ? over.IconId : named.IconId, 1, 1);
        }
        else if ((tick || healTick) && to != player)
        {
            // Healing that is not yours is left to the game. Damage over time that is not yours is not shown at
            // all, here or by the game, as long as this tweak is the one showing the damage you deal.
            if (over.Mine == 0 && healTick) return false;

            // With nothing ticking in their list, it may be that the list is already empty: a tick that kills
            // shows after the statuses of the dead are gone. Then it is yours only if you had something
            // ticking there a moment ago; a tick is never taken as yours for want of knowing whose it is.
            if (over.Mine == 0 && over.Total == 0)
                over = TrackedOverTime(to);

            if (over.Mine == 0)
            {
                var dealt = Event(BattleTextEvent.DamageDealt);
                return dealt.Enabled && Area(dealt.Area).Enabled;
            }

            if (over.Mine > 0 && over.Total > over.Mine)
            {
                amount    = (int)((long)amount * over.Mine / over.Total);
                estimated = true;
            }
        }

        var mine    = from == player || ownTick || healTick || IsMyPet(player, from);

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

        // The attack an effect of yours answers a hit with (Vengeance, Damnation...) comes as a plain "Attack",
        // not as the action that gave the effect: it takes its name and icon. Your own auto-attacks come as
        // such, with their own action, and are left alone.
        uint countering = 0;
        if (damage && outgoing && from == player && !tick && action.Name is not null
            && (action.IconId == 0 || action.Name == LookupAction(AttackAction).Name)
            && (!autoAttack || actionId is not (AttackAction or ShotAction)))
        {
            countering = CounterOf(player);
            if (countering != 0 && LookupAction(countering) is { Name: not null } counter)
                action = counter;
            else
                countering = 0;
        }

        // How much of a hit on you your damage reduction took off, as far as the effects say. Not for a tick,
        // which was worked out when its effect was put on you.
        var mitigated = Settings.Mitigation != BattleTextMitigation.Off && damage && !outgoing && !tick && label is null
            ? MitigatedPercent(player, from, damageType)
            : 0;

        // Who a heal of yours is for, at the end of the message: after the action name when the area shows it,
        // else after the amount. Merged heals have several targets and say none.
        var healed = Settings.ShowHealTargets && heal && outgoing && (healTick || !Settings.MergeHeals) ? to->NameString : null;
        if (string.IsNullOrEmpty(healed)) healed = null;
        var healedAfterName = healed is not null && !area.Hidden.Contains(BattleTextPart.Name);

        // The verdict on the positional of this action, if it was left for this message: after the action name
        // when the area shows it, in the color of the verdict; else after the amount.
        string? verdict = null;
        HighlightOptions? verdictLook = null;
        if (damage && outgoing && actionKind == ActionKindAction && _positionalVerdicts.Remove(actionId, out var waiting)
            && DateTime.UtcNow - waiting.At < VerdictWindow)
        {
            verdict     = T($"Positional.{(waiting.Hit ? "Hit" : "Miss")}.{waiting.Side}");
            verdictLook = waiting.Hit ? Settings.PositionalHit : Settings.PositionalMiss;
        }

        var verdictAfterName = verdict is not null && action.Name is not null && !area.Hidden.Contains(BattleTextPart.Name);

        // An auto-attack says who makes it when it is not you: your chocobo or pet, or the enemy hitting you.
        // The game gives them as auto-attacks, or as a hit of an action of that kind, which has no icon here.
        var auto     = (autoAttack && !tick) || (actionKind == ActionKindAction && action.Name is not null && action.IconId == 0);
        // What a pet of yours does says so too, whatever it is: its actions end with its name, and its
        // auto-attacks, which come with no action, are called as yours are.
        var byPet    = !tick && !healTick && IsMyPet(player, from);
        var attacker = (auto || byPet) && from != null && from != player ? from->NameString : null;
        if (string.IsNullOrEmpty(attacker)) attacker = null;
        var petAttack = byPet && attacker is not null && action.Name is null && LookupAction(AttackAction).Name is { } attack
            ? $"{attack} {attacker}"
            : null;

        // What a monster, a boss or any other NPC does to you shows no action icon; what another player does (PvP) does.
        var fromNpc = !outgoing && !mine && from != null && from->ObjectKind != ObjectKind.Pc;

        var suffix = !Settings.ShowDefended ? string.Empty : (ScreenLogOption)option switch
        {
            ScreenLogOption.Blocked  => T("Suffix.Blocked"),
            ScreenLogOption.Parried  => T("Suffix.Parried"),
            ScreenLogOption.Resisted => T("Suffix.Resisted"),
            _                        => string.Empty,
        };
        if (mitigated > 0)
            suffix += string.Format(T(Settings.Mitigation == BattleTextMitigation.Percent ? "Suffix.MitigatedPercent" : "Suffix.Mitigated"), mitigated);

        // What decides whether two hits are alike enough to merge, besides the action and the kind of hit: how they
        // were defended, how much was mitigated and, for misses, which one it was.
        var defendedKind = Settings.ShowDefended && (ScreenLogOption)option is ScreenLogOption.Blocked or ScreenLogOption.Parried or ScreenLogOption.Resisted
            ? (ulong)option : 0UL;
        var labelKind    = label is null ? 0UL : kind switch
        {
            FlyTextKind.Dodge or FlyTextKind.NamedDodge => 2UL,
            FlyTextKind.Invulnerable                    => 3UL,
            _                                           => 1UL,
        };

        // A tick has no action: the effect that ticks, by its name and icon, is what makes ticks alike. Estimated ones
        // (with ~) do not merge with exact ones.
        var tickId = (tick || healTick) && Settings.MergeTicks && healed is null && over.Name is { } tickName
            ? (uint)tickName.GetHashCode() ^ over.IconId * 31u | 1u
            : 0u;

        var mergeable = (heal ? Settings.MergeHeals : Settings.MergeHits)
                        && (label is null ? (actionId != 0 || tickId != 0) && (defendedKind == 0 || Settings.MergeDefended) : Settings.MergeMisses)
                        && (attacker is null || !auto) && verdict is null;
        var colors  = Settings.Colors;
        var message = new BattleTextMessage
        {
            Amount     = amount,
            Label      = label,
            ActionName = healedAfterName      ? $"{action.Name ?? over.Name} ({healed})".TrimStart()
                       : attacker is null     ? action.Name ?? over.Name
                       : action.Name is null  ? petAttack ?? attacker
                       : $"{action.Name} {attacker}",
            IconId     = over.IconId != 0 ? over.IconId : (autoAttack && countering == 0) || fromNpc ? 0 : action.IconId,
            IconIsStatus = over.IconId != 0,
            TypeIconId = damage && damageType is >= 1 and <= DamageTypeCount ? DamageTypeIconBase + (uint)damageType : 0,
            Prefix     = (estimated ? "~" : string.Empty) + (heal ? "+" : damage && !outgoing ? "-" : string.Empty),

            // With no status to show for it, a tick at least says what it is.
            Suffix     = (over.Name is not null ? suffix
                       : tick                  ? suffix + T("Suffix.Dot")
                       : healTick              ? suffix + T("Suffix.Hot")
                       : suffix)
                       + (healed is not null && !healedAfterName ? $" ({healed})" : string.Empty)
                       + (verdict is not null && !verdictAfterName ? $" {verdict}" : string.Empty),
            Verdict    = verdictAfterName ? verdict : null,
            NameColor  = verdictAfterName && verdictLook is { Enabled: true } ? verdictLook.Color : null,
            GradientVertical = verdictLook is { GradientDirection: BattleTextGradientDirection.Vertical },
            NameColorEnd = verdictAfterName && verdictLook is { Enabled: true, Gradient: true } ? verdictLook.ColorEnd : null,
            Color      = label is not null ? colors.Miss
                       : heal              ? colors.Heal
                       : !outgoing         ? colors.IncomingDamage
                       : colors.OutgoingDamage,

            MergeKey   = mergeable
                       ? ((ulong)(countering != 0 ? countering : actionId != 0 ? actionId : tickId) << 4) | (crit ? 8u : 0u) | (directHit ? 4u : 0u) | (heal ? 2u : 0u) | 1u
                         | ((defendedKind | (ulong)mitigated << 3 | labelKind << 12 | (estimated ? 1UL << 14 : 0UL)) << 40)
                         | (tickId != 0 && actionId == 0 ? 1UL << 61 : 0UL)
                       : 0,
        };

        MarkHit(message, crit, directHit, heal ? HitSource.Heal : outgoing ? HitSource.Dealt : HitSource.Taken);

        // A hit that is neither critical nor direct takes the look of the verdict; one that is keeps its own.
        if (message.Look is null && verdictLook is { Enabled: true })
            message.Look = new BattleTextLook(verdictLook.Font, verdictLook.FontSize, verdictLook.Animation, verdictLook.Intensity);

        return Queue(type, message);
    }

    /// <summary>
    /// Marks a critical and/or direct hit and gives it the look chosen for that kind of hit, with the
    /// color chosen for whose hit it is: damage you deal, damage you take, or healing.
    /// </summary>
    private void MarkHit(BattleTextMessage message, bool crit, bool directHit, HitSource source)
    {
        if (!crit && !directHit) return;

        message.Crit = crit;
        message.Mark = crit && directHit ? "!!" : crit ? "!" : string.Empty;

        var highlight = Highlight(crit && directHit ? BattleTextHighlight.CriticalDirectHit
                                : crit              ? BattleTextHighlight.Critical
                                : BattleTextHighlight.DirectHit);
        if (!highlight.Enabled) return;

        message.Look  = new BattleTextLook(highlight.Font, highlight.FontSize, highlight.Animation, highlight.Intensity);
        message.Color = source switch
        {
            HitSource.Taken => highlight.ColorTaken,
            HitSource.Heal  => highlight.ColorHeal,
            _               => highlight.Color,
        };
    }

    /// <summary>
    /// The game working out one tick of damage or healing over time: the text it makes for it is made in here,
    /// so what it says of the tick is kept at hand meanwhile. Runs inside the game's own code: it must never throw.
    /// </summary>
    private void OnTick(StatusManager* statuses, BattleChara* target, uint statusId, int tickMode, uint value, uint sourceEntityId, int damageType)
    {
        _tickSource = (statusId, sourceEntityId);
        try
        {
            _tickHook!.Original(statuses, target, statusId, tickMode, value, sourceEntityId, damageType);
        }
        finally
        {
            _tickSource = null;
        }
    }

    // ── Actions with no fly text ────────────────────────────────────────────────

    // Kinds of effect of an action that the game shows as fly text, or that come with one: misses, damage and
    // healing in their variants (1 to 7), and the statuses it applies (14 on the target, 15 on the source).
    private const byte FirstTextEffect   = 1;
    private const byte LastTextEffect    = 7;
    private const byte StatusOnTarget    = 14;
    private const byte StatusOnSource    = 15;

    // Kinds of effect that say the action did not take: resisted in full, on someone invulnerable, or with no
    // effect at all (an enemy immune to the stun it was meant to give).
    private const byte FullResistEffect      = 2;
    private const byte InvulnerableEffect    = 7;
    private const byte NoEffectTextEffect    = 8;
    private const byte StatusNoEffectEffect  = 20;

    // The last action of yours that did not take, for the text the game then makes of it, which may not name it.
    private (uint ActionId, DateTime At)? _noEffectAction;

    // The last action of yours announced by its name alone, and where: if the game then says it did not take,
    // that is said in its place instead of in a second message.
    private (uint ActionId, BattleTextArea Area, BattleTextMessage Message, DateTime At)? _actionAnnounced;

    /// <summary>Runs on the game thread, inside the game's own code: it must never throw.</summary>
    private void OnActionEffect(uint casterEntityId, Character* caster, Vector3* targetPos, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            CaptureAction(casterEntityId, header, effects, targetEntityIds);
        }
        catch (Exception ex)
        {
            // Reported from OnUpdate: disabling the tweak here would free the hook that is running.
            _failure ??= ex;
        }

        _actionHook!.Original(casterEntityId, caster, targetPos, header, effects, targetEntityIds);
    }

    // Kinds of effect of an action that are damage: plain, blocked and parried.
    private const byte DamageEffect  = 3;
    private const byte BlockedEffect = 5;
    private const byte ParriedEffect = 6;

    private const uint TrueNorthStatus     = 1250;  // on you: your actions hit their positional from anywhere
    private const uint DirectionlessStatus = 3808;  // on an enemy: it has no sides for now

    private readonly Dictionary<(uint ActionId, int Level), Positionals.Info?> _positionals = [];
    private readonly Dictionary<uint, bool> _sideless = [];

    // Verdicts waiting for the damage of their action, to be said in its message: the damage shows a moment
    // after the action is resolved, which is when the verdict is known.
    private readonly Dictionary<uint, (bool Hit, Positionals.Side Side, DateTime At)> _positionalVerdicts = [];

    private static readonly TimeSpan VerdictWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What the game's description of an action says about its positional, at the level you are (synced or not):
    /// the same text, with the same numbers, as its tooltip. In English whatever the language of the game.
    /// </summary>
    private Positionals.Info? PositionalOf(uint actionId)
    {
        var key = (actionId, (int)Svc.PlayerState.EffectiveLevel);
        if (_positionals.TryGetValue(key, out var info)) return info;

        if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTransient>(Dalamud.Game.ClientLanguage.English).TryGetRow(actionId, out var text)
            && Svc.Data.GetExcelSheet<LuminaAction>().TryGetRow(actionId, out var action))
        {
            string description;
            try
            {
                description = Svc.SeStringEvaluator.Evaluate(text.Description, default, Dalamud.Game.ClientLanguage.English).ExtractText();
            }
            catch (Exception)
            {
                // Without its numbers the description still says which side: where you stood will decide.
                description = text.Description.ExtractText();
            }

            info = Positionals.Read(description, hasCombo: action.ActionCombo.RowId != 0);
        }

        return _positionals[key] = info;
    }

    /// <summary>
    /// Says whether an action of yours that has a positional hit it: see <see cref="Positionals"/> for how.
    /// </summary>
    private void CapturePositional(GameObject* player, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        if (effects == null || !Event(BattleTextEvent.Positional).Enabled) return;
        if (PositionalOf(header->ActionId) is not { } positional) return;

        // An action with a positional has one target: the first hit that did damage is the one to read.
        // One that did no damage (it missed, the target was invulnerable) says nothing of where you stood.
        for (var target = 0; target < header->NumTargets; target++)
        {
            foreach (ref readonly var effect in effects[target].Effects)
            {
                if (effect.Type is not (DamageEffect or BlockedEffect or ParriedEffect)) continue;

                var manager = GameObjectManager.Instance();
                var enemy   = targetEntityIds == null || manager == null ? null : manager->Objects.GetObjectByEntityId(targetEntityIds[target].ObjectId);
                var stance  = StanceOn(player, enemy, positional.Side);

                if (Positionals.Judge(effect.Param2, Positionals.FromShare(positional, effect.Param2), stance) is not { } hit)
                    return;

                // In the message of its damage, when asked to and this tweak is the one showing that damage;
                // else in an alert of its own.
                var dealt = Event(BattleTextEvent.DamageDealt);
                if (Settings.PositionalInline && dealt.Enabled && Area(dealt.Area).Enabled)
                    _positionalVerdicts[header->ActionId] = (hit, positional.Side, DateTime.UtcNow);
                else
                    Queue(BattleTextEvent.Positional, PositionalMessage(LookupAction(header->ActionId), positional.Side, hit));
                return;
            }
        }
    }

    /// <summary>Where you stand around an enemy, for an action that wants one side of it.</summary>
    private Positionals.Stance StanceOn(GameObject* player, GameObject* enemy, Positionals.Side side)
    {
        if (HasStatus(player, TrueNorthStatus)) return Positionals.Stance.Right;
        if (enemy == null) return Positionals.Stance.Unknown;
        if (HasStatus(enemy, DirectionlessStatus)) return Positionals.Stance.Right;

        // Some enemies have no sides at all: the game says which.
        if (enemy->ObjectKind == ObjectKind.BattleNpc)
        {
            if (!_sideless.TryGetValue(enemy->BaseId, out var sideless))
            {
                sideless = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.BNpcBase>().TryGetRow(enemy->BaseId, out var row) && row.IsOmnidirectional;
                _sideless[enemy->BaseId] = sideless;
            }

            if (sideless) return Positionals.Stance.Right;
        }

        // On the ground: height does not count. An object faces (sin, cos) of its rotation.
        var toPlayer = new Vector2(player->Position.X - enemy->Position.X, player->Position.Z - enemy->Position.Z);
        if (toPlayer.LengthSquared() < 0.0001f) return Positionals.Stance.Unknown;

        var facing  = new Vector2(MathF.Sin(enemy->Rotation), MathF.Cos(enemy->Rotation));
        var cosine  = Math.Clamp(Vector2.Dot(facing, Vector2.Normalize(toPlayer)), -1f, 1f);
        var degrees = MathF.Acos(cosine) * 180f / MathF.PI;

        return Positionals.StanceAt(side, degrees);
    }

    private static bool HasStatus(GameObject* who, uint statusId)
    {
        var statuses = ((BattleChara*)who)->GetStatusManager();
        if (statuses == null) return false;

        foreach (ref readonly var status in statuses->Status)
        {
            if (status.StatusId == statusId) return true;
        }

        return false;
    }

    /// <summary>"Icon, NAME Rear!" or "Icon, NAME Rear missed", with the look chosen for each.</summary>
    private BattleTextMessage PositionalMessage((string? Name, uint IconId) action, Positionals.Side side, bool hit)
    {
        var highlight = hit ? Settings.PositionalHit : Settings.PositionalMiss;
        return new BattleTextMessage
        {
            ActionName = action.Name,
            Order      = DefaultCooldownOrder,
            Hidden     = [],
            Label      = T($"Positional.{(hit ? "Hit" : "Miss")}.{side}"),
            IconId     = action.IconId,
            Color      = highlight.Enabled ? highlight.Color : hit ? DefaultColors.PositionalHit : DefaultColors.PositionalMiss,
            ColorEnd   = highlight is { Enabled: true, Gradient: true } ? highlight.ColorEnd : null,
            GradientVertical = highlight.GradientDirection == BattleTextGradientDirection.Vertical,
            Look       = highlight.Enabled ? new BattleTextLook(highlight.Font, highlight.FontSize, highlight.Animation, highlight.Intensity) : null,
        };
    }

    /// <summary>
    /// Announces an action of yours that nothing else names: it deals no damage and heals nothing. One that only
    /// puts an effect on you is left to the message of that effect; one that puts it on someone else (a stun)
    /// is announced, as the effect alone does not say what you used.
    /// </summary>
    private void CaptureAction(uint casterEntityId, ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        if (header == null || header->ActionType != ActionKindAction) return;

        var player = (GameObject*)Control.GetLocalPlayer();
        if (player == null || player->EntityId != casterEntityId) return;

        CapturePositional(player, header, effects, targetEntityIds);

        // An action that did not take is said by the text the game makes of that, not here.
        bool shown = false, noEffect = false;
        if (effects != null)
        {
            for (var target = 0; target < header->NumTargets; target++)
            {
                var onSelf = targetEntityIds == null || targetEntityIds[target].ObjectId == player->EntityId;
                foreach (ref readonly var effect in effects[target].Effects)
                {
                    shown    |= effect.Type is >= FirstTextEffect and <= LastTextEffect or StatusOnSource
                                || (effect.Type == StatusOnTarget && onSelf);
                    noEffect |= effect.Type is FullResistEffect or InvulnerableEffect or NoEffectTextEffect or StatusNoEffectEffect;
                }
            }
        }

        if (noEffect) _noEffectAction = (header->ActionId, DateTime.UtcNow);
        if (shown || noEffect || !Event(BattleTextEvent.ActionUsed).Enabled) return;

        var actionId = header->ActionId;
        if (!_playerActions.TryGetValue(actionId, out var isPlayerAction))
        {
            // Only the actions of a job: not mounting, teleporting, using an item...
            isPlayerAction = Svc.Data.GetExcelSheet<LuminaAction>().TryGetRow(actionId, out var row) && row.IsPlayerAction;
            _playerActions[actionId] = isPlayerAction;
        }

        var action = LookupAction(actionId);
        if (!isPlayerAction || action.Name is null) return;

        var message = new BattleTextMessage
        {
            Label  = action.Name,
            IconId = action.IconId,
            Color  = Settings.Colors.Action,
        };
        if (Queue(BattleTextEvent.ActionUsed, message))
            _actionAnnounced = (actionId, Event(BattleTextEvent.ActionUsed).Area, message, DateTime.UtcNow);
    }

    /// <summary>
    /// An effect of yours that did not take on someone: "icon, NAME Immune", with the name of the action when
    /// it is known, else that of the effect. What does not take on you is left to the game.
    /// </summary>
    private bool CaptureNoEffect(GameObject* player, GameObject* from, GameObject* to, string labelKey, uint statusId, byte actionKind, uint actionId)
    {
        if (to == player || (from != player && !IsMyPet(player, from))) return false;

        // The action, as the game says it; else the one of yours that was just told not to have taken, or
        // just announced.
        var announced = from == player && _actionAnnounced is { } shown && DateTime.UtcNow - shown.At < VerdictWindow ? _actionAnnounced : null;
        var usedId    = actionKind == ActionKindAction && LookupAction(actionId).Name is not null ? actionId
                      : from == player && _noEffectAction is { } last && DateTime.UtcNow - last.At < VerdictWindow ? last.ActionId
                      : announced?.ActionId ?? 0;
        var action    = LookupAction(usedId);

        var status = action.Name is null ? LookupStatus(statusId) : default;
        if ((action.Name ?? status.Name) is not { } name) return false;

        var options = Event(BattleTextEvent.ActionUsed);
        var message = new BattleTextMessage
        {
            ActionName   = name,
            Order        = DefaultCooldownOrder,
            Hidden       = [],
            Label        = T(labelKey),
            IconId       = action.Name is null ? status.IconId : action.IconId,
            IconIsStatus = action.Name is null,
            Color        = Settings.Colors.Miss,
            Downwards    = options.Motion == BattleTextMotion.Down,
            Static       = options.Motion == BattleTextMotion.Static,

            // The same effect failing on several targets of one action; the high bit keeps it apart from damage.
            MergeKey     = Settings.MergeNoEffect
                         ? (1UL << 62) | ((ulong)(usedId != 0 ? usedId : statusId | 1u << 31) << 4) | (labelKey == "Label.Immune" ? 2UL : 4UL) | 1UL
                         : 0,
        };

        // In the place of the announcement of that action, when it is still there to be replaced.
        if (announced is { } plain && plain.ActionId == usedId)
        {
            _actionAnnounced = null;

            var waiting = _pending.FindIndex(pending => ReferenceEquals(pending.Message, plain.Message));
            if (waiting >= 0)
            {
                _pending[waiting] = (plain.Area, message);
                return true;
            }

            if (_areas[(int)plain.Area]?.Replace(plain.Message, message) == true) return true;
        }

        return Queue(BattleTextEvent.ActionUsed, message);
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
            type = buff ? BattleTextEvent.BuffOnOthers : BattleTextEvent.DebuffOnEnemy;

            // A debuff on an enemy can come with the enemy as its own source, whoever put it there:
            // then the enemy's list of statuses says whether it is yours.
            var direct = from == player || IsMyPet(player, from);

            if (!direct && (buff || from != to || to->ObjectKind != ObjectKind.BattleNpc)) return false;

            if (!buff)
            {
                // The end of a debuff does not say whose it was, and a job mate may have the same one on the
                // same enemy: the end of yours is announced by WatchDebuffs, which knows. Not shown by the game either.
                if (fading) return true;

                if (direct) TrackDebuff(to, statusId, seen: false);
                else if (!ConfirmDebuff(player, to, statusId)) return true;
            }
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

    private static ulong DebuffKey(uint targetEntityId, uint statusId) => ((ulong)targetEntityId << 32) | statusId;

    private void TrackDebuff(GameObject* target, uint statusId, bool seen)
    {
        if (_myDebuffs.Count >= MaxTrackedDebuffs)
            _myDebuffs.Clear();
        _myDebuffs[DebuffKey(target->EntityId, statusId)] = (seen, DateTime.UtcNow);
    }

    /// <summary>Whether someone has a status that you put on them: each status in their list names its source.</summary>
    private static bool HasMyStatus(GameObject* player, GameObject* target, uint statusId)
    {
        var statuses = ((BattleChara*)target)->GetStatusManager();
        return statuses != null && statuses->GetStatusIndex(statusId, player->EntityId) >= 0;
    }

    /// <summary>
    /// Whether a debuff that starts on an enemy, with no word on who put it there, is yours.
    /// One that starts before the enemy's list has it is looked at again for a moment, in <see cref="ConfirmDebuffs"/>.
    /// </summary>
    private bool ConfirmDebuff(GameObject* player, GameObject* target, uint statusId)
    {
        if (!HasMyStatus(player, target, statusId))
        {
            _unconfirmed.Add((target->EntityId, statusId, DateTime.UtcNow + ConfirmWindow));
            return false;
        }

        TrackDebuff(target, statusId, seen: true);
        return true;
    }

    /// <summary>
    /// Announces the end of your debuffs: the moment one of them is no longer in its enemy's list as yours.
    /// The same debuff from someone else stays in the list under their name, and is none of this.
    /// </summary>
    private void WatchDebuffs()
    {
        if (_myDebuffs.Count == 0 || DateTime.UtcNow < _nextDebuffWatch) return;
        _nextDebuffWatch = DateTime.UtcNow + DebuffWatch;

        var player  = (GameObject*)Control.GetLocalPlayer();
        var manager = GameObjectManager.Instance();
        if (player == null || manager == null)
        {
            _myDebuffs.Clear();
            return;
        }

        _debuffKeys.Clear();
        _debuffKeys.AddRange(_myDebuffs.Keys);
        foreach (var key in _debuffKeys)
        {
            var (seen, since) = _myDebuffs[key];
            var statusId = (uint)key;
            var target   = manager->Objects.GetObjectByEntityId((uint)(key >> 32));

            // An enemy that is gone or dead takes its debuffs with it: nothing ended.
            if (target == null || target->IsDead())
            {
                _myDebuffs.Remove(key);
                continue;
            }

            if (HasMyStatus(player, target, statusId))
            {
                if (!seen) _myDebuffs[key] = (true, since);
                continue;
            }

            // Not in the list yet, or put by your pet, which the list names instead of you.
            if (!seen)
            {
                if (DateTime.UtcNow - since > DebuffGrace) _myDebuffs.Remove(key);
                continue;
            }

            _myDebuffs.Remove(key);
            if (!Settings.ShowFading) continue;

            var status = LookupStatus(statusId);
            if (status.Name is null) continue;

            Queue(BattleTextEvent.DebuffOnEnemy, new BattleTextMessage
            {
                Label  = "- " + status.Name,
                IconId = status.IconId,
                Color  = EffectColor(buff: false, fading: true),
                IconIsStatus = true,
            });
        }
    }

    /// <summary>Shows the debuffs that started a moment ago and turned out to be yours.</summary>
    private void ConfirmDebuffs()
    {
        if (_unconfirmed.Count == 0) return;

        var player  = (GameObject*)Control.GetLocalPlayer();
        var manager = GameObjectManager.Instance();
        for (var i = _unconfirmed.Count - 1; i >= 0; i--)
        {
            var (targetId, statusId, until) = _unconfirmed[i];
            var target = player == null || manager == null ? null : manager->Objects.GetObjectByEntityId(targetId);
            var mine   = target != null && target->ObjectKind == ObjectKind.BattleNpc && HasMyStatus(player, target, statusId);

            if (!mine && target != null && DateTime.UtcNow < until) continue;

            _unconfirmed.RemoveAt(i);
            if (!mine) continue;

            TrackDebuff(target, statusId, seen: true);
            var status = LookupStatus(statusId);
            if (status.Name is null) continue;

            Queue(BattleTextEvent.DebuffOnEnemy, new BattleTextMessage
            {
                Label  = "+ " + status.Name,
                IconId = status.IconId,
                Color  = EffectColor(buff: false, fading: false),
                IconIsStatus = true,
            });
        }
    }

    /// <summary>
    /// Whether a status deals damage or heals over time. The game has no column for it, but its own
    /// description of every such status says so in the same words; the English text is read whatever
    /// the language of the game, which carries all of them.
    /// </summary>
    private OverTime OverTimeOf(uint statusId)
    {
        if (_overTime.TryGetValue(statusId, out var kind)) return kind;

        kind = OverTime.None;
        if (Svc.Data.GetExcelSheet<LuminaStatus>(Dalamud.Game.ClientLanguage.English).TryGetRow(statusId, out var row))
        {
            var description = row.Description.ExtractText();
            // A few say it as losing HP instead: "Bleeding HP over time" is what Choco Beak leaves, and the
            // strong poisons of some enemies are "slowly draining HP".
            if (row.StatusCategory == DetrimentalStatus
                && (description.Contains("damage over time", StringComparison.OrdinalIgnoreCase) || description.Contains("bleeding HP over time", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("draining HP", StringComparison.OrdinalIgnoreCase)))
                kind = OverTime.Damage;
            else if (row.StatusCategory == BeneficialStatus && description.Contains("HP over time", StringComparison.OrdinalIgnoreCase))
                kind = OverTime.Healing;
        }

        return _overTime[statusId] = kind;
    }

    /// <summary>
    /// The statuses behind a tick: the damage or healing over time its target has. One tick is the sum of
    /// all of them, so with several they are all named, with the icon of the first.
    /// </summary>
    /// <param name="mineOnly">Name only the ones put by you or by a pet of yours, on whoever it is: the pet itself too.</param>
    /// <returns>Besides the names and the icon, how many of those statuses are yours and how many there are in all.</returns>
    private (string? Name, uint IconId, int Mine, int Total) FindOverTime(GameObject* player, GameObject* target, OverTime kind, bool mineOnly)
    {
        var statuses = ((BattleChara*)target)->GetStatusManager();
        if (statuses == null) return default;

        string? names = null;
        uint iconId = 0;
        int mine = 0, total = 0;
        _overTimeSeen.Clear();
        foreach (ref readonly var status in statuses->Status)
        {
            uint id = status.StatusId;
            if (id == 0 || OverTimeOf(id) != kind) continue;

            var sourceId = status.SourceObject.ObjectId;
            var isMine   = sourceId == player->EntityId;
            if (!isMine)
            {
                var manager = GameObjectManager.Instance();
                isMine = IsMyPet(player, manager == null ? null : manager->Objects.GetObjectByEntityId(sourceId));
            }

            total++;
            if (isMine) mine++;
            if ((mineOnly && !isMine) || !_overTimeSeen.Add(id)) continue;

            var found = LookupStatus(id);
            if (found.Name is null) continue;

            names = names is null ? found.Name : $"{names} + {found.Name}";
            if (iconId == 0) iconId = found.IconId;
        }

        return (names, iconId, mine, total);
    }

    /// <summary>
    /// Reads what every action of a job says its effects do, at the level you are (synced or not). In English
    /// whatever the language of the game, as the names the effects are then looked up by.
    /// </summary>
    private void ReadEffects()
    {
        _effects.Clear();
        _statusEffects.Clear();

        var texts      = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ActionTransient>(Dalamud.Game.ClientLanguage.English);
        var fromPlayer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in Svc.Data.GetExcelSheet<LuminaAction>(Dalamud.Game.ClientLanguage.English))
        {
            if (action.IsPvP || action.ClassJobLevel == 0 || action.ClassJobCategory.RowId == 0) continue;
            if (!texts.TryGetRow(action.RowId, out var text)) continue;

            var name = action.Name.ExtractText();
            if (name.Length == 0) continue;

            string description;
            try
            {
                description = Svc.SeStringEvaluator.Evaluate(text.Description, default, Dalamud.Game.ClientLanguage.English).ExtractText();
            }
            catch (Exception)
            {
                description = text.Description.ExtractText();
            }

            // Two actions can give an effect of the same name: the one a player has in their list is the one meant.
            foreach (var (status, effect) in ActionEffects.Read(action.RowId, name, description))
            {
                if (_effects.ContainsKey(status) && (!action.IsPlayerAction || fromPlayer.Contains(status))) continue;

                _effects[status] = effect;
                if (action.IsPlayerAction) fromPlayer.Add(status);
            }
        }
    }

    /// <summary>What a status does, as the action that gives it says; null when none says anything of it.</summary>
    private ActionEffects.Effect? EffectOf(uint statusId)
    {
        var level = (int)Svc.PlayerState.EffectiveLevel;
        if (level != _effectsLevel)
        {
            ReadEffects();
            _effectsLevel = level;
        }

        if (_statusEffects.TryGetValue(statusId, out var effect)) return effect;

        effect = Svc.Data.GetExcelSheet<LuminaStatus>(Dalamud.Game.ClientLanguage.English).TryGetRow(statusId, out var row)
                 && _effects.TryGetValue(row.Name.ExtractText(), out var found) ? found : null;
        return _statusEffects[statusId] = effect;
    }

    /// <summary>The action behind an effect on you that answers hits with an attack, or 0 when you have none.</summary>
    private uint CounterOf(GameObject* player)
    {
        var statuses = ((BattleChara*)player)->GetStatusManager();
        if (statuses == null) return 0;

        foreach (ref readonly var status in statuses->Status)
        {
            if (status.StatusId != 0 && EffectOf(status.StatusId) is { Counter: true } effect)
                return effect.ActionId;
        }

        return 0;
    }

    /// <summary>
    /// How much of a hit on you was taken off, in percent: by the effects on you that reduce the damage you
    /// take, and by those on whoever hit you that lower the damage they deal. Each one takes its share of what
    /// the others left, as in the game.
    /// </summary>
    private int MitigatedPercent(GameObject* player, GameObject* from, int damageType)
    {
        var left = Mitigate(player, dealt: false, damageType, 1f);
        if (from != null && from != player && from->ObjectKind is ObjectKind.BattleNpc or ObjectKind.Pc)
            left = Mitigate(from, dealt: true, damageType, left);

        return (int)MathF.Round((1f - left) * 100f);
    }

    private float Mitigate(GameObject* who, bool dealt, int damageType, float left)
    {
        var statuses = ((BattleChara*)who)->GetStatusManager();
        if (statuses == null) return left;

        // The same effect from two people is there twice, and counts once.
        _mitigationSeen.Clear();
        foreach (ref readonly var status in statuses->Status)
        {
            if (status.StatusId == 0 || !_mitigationSeen.Add(status.StatusId)) continue;
            if (EffectOf(status.StatusId) is { } effect)
                left = ActionEffects.Apply(effect, dealt, damageType, left);
        }

        return left;
    }

    /// <summary>
    /// The damage over time you are known to have put on an enemy, from the debuffs of yours being watched:
    /// for when its own list of statuses no longer says.
    /// </summary>
    private (string? Name, uint IconId, int Mine, int Total) TrackedOverTime(GameObject* target)
    {
        string? names = null;
        uint iconId = 0;
        var count = 0;
        foreach (var key in _myDebuffs.Keys)
        {
            var statusId = (uint)key;
            if ((uint)(key >> 32) != target->EntityId || OverTimeOf(statusId) != OverTime.Damage) continue;

            count++;
            var found = LookupStatus(statusId);
            if (found.Name is null) continue;

            names = names is null ? found.Name : $"{names} + {found.Name}";
            if (iconId == 0) iconId = found.IconId;
        }

        return (names, iconId, count, count);
    }

    /// <summary>An effect that starts and one that ends have their own colors, besides the + and - before the name.</summary>
    private Vector4 EffectColor(bool buff, bool fading)
    {
        var colors = Settings.Colors;
        return buff ? (fading ? colors.BuffEnd : colors.Buff) : (fading ? colors.DebuffEnd : colors.Debuff);
    }

    private bool IsMyPet(GameObject* player, GameObject* other)
        => Settings.IncludePets && other != null && other->ObjectKind == ObjectKind.BattleNpc && other->OwnerId == player->EntityId;

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
            return message.Hits > 1 ? $"{message.Label} x{message.Hits}" : message.Label;

        var amount = message.Amount;
        var number = !Settings.Abbreviate || amount < 10_000 ? amount.ToString("#,0", CultureInfo.InvariantCulture)
                   : amount < 1_000_000                      ? (amount / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K"
                   :                                           (amount / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";

        var hits = message.Hits > 1 ? $" x{message.Hits}" : string.Empty;
        return $"{message.Prefix}{number}{message.Mark}{hits}{message.Suffix}";
    }

    // ── Cooldown alerts ─────────────────────────────────────────────────────────

    /// <summary>
    /// The actions of the current job, at the level you are (synced or not), whose cooldown can be announced.
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

        // Synced down, the action you have is the one of that level: Raw Intuition, not Bloodwhetting.
        var level   = Svc.PlayerState.EffectiveLevel;
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

        // While the map changes, and for a moment after, the game has cooldowns as running that are not, and
        // then as over: nothing came back. What is really cooling down is picked up again once it settles.
        var conditions = Conditions.Instance();
        if (Control.GetLocalPlayer() == null || (conditions != null && (conditions->BetweenAreas || conditions->BetweenAreas51)))
            _cooldownsSettle = DateTime.UtcNow + CooldownSettle;

        if (DateTime.UtcNow < _cooldownsSettle)
        {
            _coolingDown.Clear();
            return;
        }

        if (DateTime.UtcNow < _nextCooldownPoll) return;
        _nextCooldownPoll = DateTime.UtcNow + CooldownPoll;

        var manager = ActionManager.Instance();
        if (manager == null || !Svc.PlayerState.IsLoaded) return;

        var watchFor = (Svc.PlayerState.ClassJob.RowId, (int)Svc.PlayerState.EffectiveLevel);
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

            // Another character, or none: everything is set up again with its options.
            if (CurrentCharacter != _characterId)
            {
                Disable();
                Enable();
                return;
            }

            PollCooldowns();
            ConfirmDebuffs();
            WatchDebuffs();

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

                PlaceHandle(area, options, anchor);
                node.Position = anchor + new Vector2(options.OffsetX, options.OffsetY);
                SetLanes(node, area, options);
            }

            // Told once the area has taken its position from the handle, above.
            if (_movedArea is { } movedArea)
            {
                _movedArea = null;
                AreaMoved?.Invoke(movedArea);
            }
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>
    /// Shows the handle of an area while the settings window is open, centred on the area point. A handle
    /// that is not where it was last put has been dragged: the area then takes its position from it.
    /// </summary>
    private void PlaceHandle(BattleTextArea area, AreaOptions options, Vector2 anchor)
    {
        var index = (int)area;
        if (_handles[index] is not { } handle) return;

        if (handle.Shown != Preview)
        {
            handle.Shown = Preview;
            _handlePlaced[index] = null;
        }

        if (!Preview) return;

        var half = handle.Size / 2f;
        if (_handlePlaced[index] is { } placed && handle.Position != placed)
        {
            var point = handle.Position + half - anchor;
            options.OffsetX = Math.Clamp((int)MathF.Round(point.X), -OffsetLimitX, OffsetLimitX);
            options.OffsetY = Math.Clamp((int)MathF.Round(point.Y), -OffsetLimitY, OffsetLimitY);
            Changed();
        }

        handle.Position = anchor + new Vector2(options.OffsetX, options.OffsetY) - half;
        _handlePlaced[index] = handle.Position;
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

    /// <summary>
    /// Sample messages while the settings window is open: one kind of event after another, or only
    /// the highlighted ones in the tabs that set their look.
    /// </summary>
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
            MarkHit(message, crit, directHit, outgoing ? HitSource.Dealt : HitSource.Taken);
            return message;
        }

        BattleTextMessage Heal(bool crit = false)
        {
            var message = new BattleTextMessage
            {
                Amount     = crit ? 17_300 : 8_420,
                ActionName = action.Name,
                IconId     = action.IconId,
                Prefix     = "+",
                Color      = colors.Heal,
            };
            MarkHit(message, crit, directHit: false, HitSource.Heal);
            return message;
        }

        BattleTextMessage Effect(bool buff, bool fading) => new()
        {
            Label  = (fading ? "- " : "+ ") + status.Name,
            IconId = status.IconId,
            Color  = EffectColor(buff, fading),
            IconIsStatus = true,
        };

        if (PreviewTab == BattleTextTab.Cooldowns)
        {
            Queue(BattleTextEvent.Cooldown, CooldownMessage(action));
            return;
        }

        if (PreviewTab == BattleTextTab.Highlights)
        {
            // The kind the window is showing.
            var kind = _previewHighlight;
            if (kind is BattleTextHighlight.PositionalHit or BattleTextHighlight.PositionalMiss)
            {
                // One side and the other in turn.
                var side = _previewStep++ % 2 == 0 ? Positionals.Side.Rear : Positionals.Side.Flank;
                Queue(BattleTextEvent.Positional, PositionalMessage(action, side, kind == BattleTextHighlight.PositionalHit));
                return;
            }

            // Dealt and taken, and healing when critical, so the color of each is seen.
            var (crit, directHit) = (kind != BattleTextHighlight.DirectHit, kind != BattleTextHighlight.Critical);
            Queue(BattleTextEvent.DamageDealt, Hit(outgoing: true, crit, auto: false, directHit));
            Queue(BattleTextEvent.DamageTaken, Hit(outgoing: false, crit, auto: false, directHit));
            if (kind == BattleTextHighlight.Critical)
                Queue(BattleTextEvent.HealTaken, Heal(crit: true));
            return;
        }

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
                Queue(BattleTextEvent.ActionUsed, new BattleTextMessage { Label = action.Name, IconId = action.IconId, Color = colors.Action });
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
