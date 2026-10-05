using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>The look of a highlighted message (a special hit, a cooldown alert) in place of that of its area.</summary>
internal sealed record BattleTextLook(GoblinBattleText.BattleTextFont Font, int FontSize, GoblinBattleText.BattleTextAnimation Animation, int Intensity);

/// <summary>One combat event waiting to be, or being, scrolled in a <see cref="BattleTextAreaNode"/>.</summary>
internal sealed class BattleTextMessage
{
    public long Amount { get; set; }

    /// <summary>How many hits were merged into this message.</summary>
    public int Hits { get; set; } = 1;

    public bool Crit { get; set; }

    /// <summary>Written after the amount: "!" for a critical hit, "!!" for a critical direct hit.</summary>
    public string Mark { get; set; } = string.Empty;

    /// <summary>Font, size and animation of its own, null to use those of the area.</summary>
    public BattleTextLook? Look { get; set; }

    /// <summary>Text shown instead of an amount (miss, dodge, cooldown ready...).</summary>
    public string? Label { get; init; }

    public string? ActionName { get; init; }

    /// <summary>Icon of the action, 0 for none.</summary>
    public uint IconId { get; init; }

    /// <summary>The icon is a status icon, which is taller than wide (3:4), not a square action icon.</summary>
    public bool IconIsStatus { get; init; }

    /// <summary>Icon of the damage type (physical, magical...), 0 for none.</summary>
    public uint TypeIconId { get; init; }

    public string Prefix { get; init; } = string.Empty;

    public string Suffix { get; init; } = string.Empty;

    public Vector4 Color { get; set; }

    /// <summary>Color of the action name when it differs from the rest of the message.</summary>
    public Vector4? NameColor { get; init; }

    /// <summary>Order of the parts of this message, when it has one of its own instead of the general one.</summary>
    public IReadOnlyList<GoblinBattleText.BattleTextPart>? Order { get; init; }

    /// <summary>Hidden parts of this message, when it has its own instead of the general ones.</summary>
    public IReadOnlyList<GoblinBattleText.BattleTextPart>? Hidden { get; init; }

    /// <summary>Stay in place until it fades, instead of scrolling.</summary>
    public bool Static { get; set; }

    /// <summary>Scroll from top to bottom instead of from bottom to top.</summary>
    public bool Downwards { get; set; }

    /// <summary>Messages with the same non-zero key that arrive together are merged into one.</summary>
    public ulong MergeKey { get; init; }
}

/// <summary>
/// A scroll area: a point on screen from which combat messages scroll up or down and fade out,
/// drawn with native text and image nodes on the overlay layer.
/// </summary>
internal sealed class BattleTextAreaNode : OverlayNode
{
    /// <summary>Messages an area can show at once; each area may be set to fewer.</summary>
    public const int MaxMessages = 15;

    private const float FadeStart   = 0.8f;   // fraction of the scroll after which the text fades out
    private const float MergeWindow = 0.35f;  // seconds during which hits of the same action are merged
    private const float LaneGap     = 4f;
    private const float CurveWidth  = 60f;
    private const float PartGap     = 4f;
    private const float MaxDelta    = 0.1f;

    private static readonly Vector4 Outline = new(0.05f, 0.05f, 0.05f, 1f);

    /// <summary>One of the game's fonts and the style it is drawn in.</summary>
    private readonly record struct Typeface(FontType Type, bool Italic = false);

    /// <summary>The nodes of one message: its parts are laid out inside the container, which is what moves.</summary>
    private sealed class Slot
    {
        public required ResNode Container { get; init; }
        public required TextNode Name { get; init; }
        public required TextNode Number { get; init; }
        public required IconImageNode Icon { get; init; }
        public required IconImageNode Type { get; init; }
        public BattleTextMessage Message = null!;
        public float Elapsed;
        public float Width;
        public float Height;
    }

    private readonly List<Slot> _active = [];   // oldest first
    private readonly Stack<Slot> _free = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public BattleTextAreaNode()
    {
        for (var i = 0; i < MaxMessages; i++)
        {
            var container = new ResNode { IsVisible = false };
            container.AttachNode(this);

            _free.Push(new Slot
            {
                Container = container,
                Name      = MakeText(container),
                Number    = MakeText(container),
                Icon      = MakeIcon(container),
                Type      = MakeIcon(container),
            });
        }
    }

    public required GoblinBattleText.Options Options { get; init; }

    public required GoblinBattleText.AreaOptions Area { get; init; }

    /// <summary>Builds the amount text of a message; called again when hits are merged into it.</summary>
    public required Func<BattleTextMessage, string> Format { get; init; }

    /// <summary>
    /// The area has events scrolling up and events scrolling down. Each direction then gets one half of
    /// the area and starts from its middle (up from the point upwards, down from the point downwards),
    /// so the two never cross. With a single direction the whole area is one path, end to end.
    /// </summary>
    public bool SplitDirections { get; set; }

    /// <summary>
    /// The area has static events and scrolling ones. The static ones then pile up just outside the
    /// scrolling path instead of at the area point, where the scrolling ones pass.
    /// </summary>
    public bool StaticOutside { get; set; }

    public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;

    /// <summary>Pixels a scrolling message travels: the whole area, or half of it when both directions share it.</summary>
    private float Travel => Math.Max(1f, Area.Height) / (SplitDirections ? 2f : 1f);

    private float Duration => Math.Max(0.5f, Area.DurationTenths / 10f);

    public void Add(BattleTextMessage message)
    {
        if (TryMerge(message)) return;

        // Over the limit of the area, the oldest messages go to make room for the new one.
        var limit = Math.Clamp(Area.MaxMessages, 1, MaxMessages);
        while (_active.Count >= limit)
        {
            Release(_active[0]);
            _active.RemoveAt(0);
        }

        var slot = _free.Pop();

        slot.Message = message;
        slot.Elapsed = 0f;
        _active.Add(slot);
        Show(slot);
        MakeRoom();
    }

    public void Clear()
    {
        foreach (var slot in _active)
            Release(slot);
        _active.Clear();
    }

    protected override void OnUpdate()
    {
        var delta = Math.Min((float)_clock.Elapsed.TotalSeconds, MaxDelta);
        _clock.Restart();

        // Static messages pile up from the area point, newest first, so the loop goes from newest to oldest.
        var pile     = 0f;
        var duration = Duration;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var slot = _active[i];
            slot.Elapsed += delta;
            if (slot.Elapsed >= duration)
            {
                Release(slot);
                _active.RemoveAt(i);
                continue;
            }

            if (IsStatic(slot))
            {
                PlaceStatic(slot, slot.Elapsed / duration, pile);
                pile += slot.Height;
            }
            else
            {
                Place(slot, slot.Elapsed / duration);
            }
        }
    }

    private bool IsStatic(Slot slot)
        => slot.Message.Static || Area.Style == GoblinBattleText.BattleTextStyle.Static;

    private static TextNode MakeText(ResNode parent)
    {
        var text = new TextNode
        {
            AlignmentType    = AlignmentType.Left,
            TextOutlineColor = Outline,
            IsVisible        = false,
        };
        text.AddTextFlags(TextFlags.Edge);
        text.AttachNode(parent);
        return text;
    }

    private static IconImageNode MakeIcon(ResNode parent)
    {
        var icon = new IconImageNode
        {
            FitTexture = true,
            IsVisible  = false,
        };
        icon.AttachNode(parent);
        return icon;
    }

    private bool TryMerge(BattleTextMessage message)
    {
        if (message.MergeKey == 0) return false;

        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var slot = _active[i];
            if (slot.Elapsed > MergeWindow) break;
            if (slot.Message.MergeKey != message.MergeKey) continue;

            slot.Message.Amount += message.Amount;
            slot.Message.Hits   += message.Hits;
            if (message.Crit && !slot.Message.Crit)
            {
                slot.Message.Crit  = true;
                slot.Message.Mark  = message.Mark;
                slot.Message.Look  = message.Look;
                slot.Message.Color = message.Color;
            }

            Show(slot);
            return true;
        }

        return false;
    }

    /// <summary>Pushes older messages ahead so the new one does not overlap them.</summary>
    private void MakeRoom()
    {
        // Only the messages going the same way as the new one are in its path.
        var speed  = Travel / Duration;
        var behind = _active[^1];
        if (IsStatic(behind)) return;

        for (var i = _active.Count - 2; i >= 0; i--)
        {
            var slot = _active[i];
            if (IsStatic(slot) || slot.Message.Downwards != behind.Message.Downwards) continue;

            var minimum = behind.Elapsed + behind.Height / speed;
            if (slot.Elapsed < minimum)
                slot.Elapsed = minimum;
            behind = slot;
        }
    }

    /// <summary>Fills the nodes of a slot and lays its parts out, left to right, in the order the user chose.</summary>
    private void Show(Slot slot)
    {
        var message  = slot.Message;
        var fontSize = (float)Math.Clamp(message.Look?.FontSize ?? Area.FontSize, 8, 96);
        var height   = fontSize + 6f;
        var font     = (message.Look?.Font ?? Options.Font) switch
        {
            GoblinBattleText.BattleTextFont.Axis              => new Typeface(FontType.Axis),
            GoblinBattleText.BattleTextFont.TrumpGothic       => new Typeface(FontType.TrumpGothic),
            GoblinBattleText.BattleTextFont.TrumpGothicItalic => new Typeface(FontType.TrumpGothic, Italic: true),
            GoblinBattleText.BattleTextFont.Miedinger         => new Typeface(FontType.MiedingerMed),
            _                                                 => new Typeface(FontType.Jupiter),
        };

        var hidden = message.Hidden ?? Area.Hidden;

        SetText(slot.Name, hidden.Contains(GoblinBattleText.BattleTextPart.Name) ? null : message.ActionName, font, fontSize, height, message.NameColor ?? message.Color);
        SetText(slot.Number, hidden.Contains(GoblinBattleText.BattleTextPart.Number) ? null : Format(message), font, fontSize, height, message.Color);
        SetIcon(slot.Icon, hidden.Contains(GoblinBattleText.BattleTextPart.Icon) ? 0 : message.IconId, fontSize + 2f, message.IconIsStatus);
        SetIcon(slot.Type, hidden.Contains(GoblinBattleText.BattleTextPart.Type) ? 0 : message.TypeIconId, fontSize - 2f, tall: false);

        var x = 0f;
        foreach (var part in message.Order ?? Area.Order)
        {
            NodeBase node = part switch
            {
                GoblinBattleText.BattleTextPart.Icon => slot.Icon,
                GoblinBattleText.BattleTextPart.Type => slot.Type,
                GoblinBattleText.BattleTextPart.Name => slot.Name,
                _                                    => slot.Number,
            };
            if (!node.IsVisible) continue;

            node.Position = new Vector2(x, (height - node.Height) / 2f);
            x += node.Width + PartGap;
        }

        slot.Width  = Math.Max(0f, x - PartGap);
        slot.Height = height;

        var container = slot.Container;
        container.Size      = new Vector2(slot.Width, height);
        container.Origin    = new Vector2(slot.Width / 2f, height / 2f);
        container.IsVisible = true;
    }

    private static void SetText(TextNode node, string? text, Typeface font, float fontSize, float height, Vector4 color)
    {
        node.IsVisible = !string.IsNullOrEmpty(text);
        if (!node.IsVisible) return;

        // The node is reused: the style of the last message must not stay on.
        if (font.Italic) node.AddTextFlags(TextFlags.Italic);
        else             node.RemoveTextFlags(TextFlags.Italic);

        node.FontType  = font.Type;
        node.FontSize  = (uint)fontSize;
        node.TextColor = color;
        node.String    = text!;
        node.Size      = new Vector2(MathF.Ceiling(node.GetTextDrawSize().X) + 2f, height);
    }

    /// <param name="tall">Keep the 3:4 shape of a status icon; a square box would squash it.</param>
    private static void SetIcon(IconImageNode node, uint iconId, float size, bool tall)
    {
        node.IsVisible = iconId != 0;
        if (!node.IsVisible) return;

        node.IconId = iconId;
        node.Size   = tall ? new Vector2(size * 0.75f, size) + new Vector2(3f, 4f) : new Vector2(size, size);
    }

    private void Place(Slot slot, float progress)
    {
        var down   = slot.Message.Downwards;
        var travel = progress * Travel;
        float y;
        if (SplitDirections)
        {
            // Upwards: the bottom edge leaves the area point. Downwards: the top edge does.
            y = down ? travel : -travel - slot.Height;
        }
        else
        {
            var height = Math.Max(1f, Area.Height);
            y = (down ? travel - height / 2f : height / 2f - travel) - slot.Height / 2f;
        }

        var bow = CurveWidth * (1f - MathF.Pow(2f * progress - 1f, 2f));
        var x   = Area.Style switch
        {
            GoblinBattleText.BattleTextStyle.CurvedLeft  => -bow,
            GoblinBattleText.BattleTextStyle.CurvedRight => bow,
            _                                            => 0f,
        };

        // Anchor 0: the message starts at the area point. 50: centred on it. 100: ends at it.
        x -= slot.Width * Math.Clamp(Area.TextAnchor, 0, 100) / 100f;

        Apply(slot, new Vector2(x, y), progress);
    }

    /// <summary>A message that does not scroll: it sits at the area point, and older ones pile up away from the character.</summary>
    private void PlaceStatic(Slot slot, float progress, float pile)
    {
        var above = Area.OffsetY < 0;
        float y;
        if (StaticOutside)
        {
            var edge = Math.Max(1f, Area.Height) / 2f + LaneGap + pile;
            y = above ? -edge - slot.Height : edge;
        }
        else
        {
            y = (above ? -pile : pile) - slot.Height / 2f;
        }

        var x = -slot.Width * Math.Clamp(Area.TextAnchor, 0, 100) / 100f;

        Apply(slot, new Vector2(x, y), progress);
    }

    /// <summary>Places a message, with its fade and whatever its animation is doing at this moment.</summary>
    private static void Apply(Slot slot, Vector2 position, float progress)
    {
        var look   = slot.Message.Look;
        var effect = look is null
            ? new BattleTextEffect()
            : BattleTextAnimations.Play(look.Animation, slot.Elapsed, Math.Clamp(look.Intensity / 100f, 0f, 3f));

        var fade = progress < FadeStart ? 1f : 1f - (progress - FadeStart) / (1f - FadeStart);

        // Everything is set every frame: the slot is reused, and the last message may have left it tilted or glowing.
        var container = slot.Container;
        container.Position        = position + effect.Offset;
        container.Scale           = effect.Scale;
        container.RotationDegrees = effect.Degrees;
        container.Alpha           = fade * effect.Alpha;
        container.AddColor        = effect.Glow;
    }

    private void Release(Slot slot)
    {
        slot.Container.IsVisible = false;
        _free.Push(slot);
    }
}
