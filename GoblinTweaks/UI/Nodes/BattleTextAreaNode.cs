using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;
using Lumina.Text;
using Lumina.Text.ReadOnly;

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

    /// <summary>Said after the action name, in its own color: whether the positional was hit.</summary>
    public string? Verdict { get; init; }

    /// <summary>Color of the verdict (or of the whole action name when there is none).</summary>
    public Vector4? NameColor { get; init; }

    /// <summary>When set, the verdict goes from <see cref="NameColor"/> to this color, letter by letter.</summary>
    public Vector4? NameColorEnd { get; init; }

    /// <summary>The gradient goes from top to bottom instead of from left to right.</summary>
    public bool GradientVertical { get; init; }

    /// <summary>When set, the label goes from <see cref="Color"/> to this color, letter by letter.</summary>
    public Vector4? ColorEnd { get; init; }

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
    private const float MergeWindow = 0.4f;   // seconds during which hits of the same action are merged
    private const float LaneGap     = 4f;
    private const float CurveWidth  = 60f;
    private const float PartGap     = 4f;
    private const float MaxDelta    = 0.1f;

    /// <summary>Widest outline the settings offer, in pixels.</summary>
    public const int MaxOutline = 4;

    private static readonly Vector4 Outline = new(0.05f, 0.05f, 0.05f, 1f);

    /// <summary>One of the game's fonts and the style it is drawn in.</summary>
    private readonly record struct Typeface(FontType Type, bool Italic = false);

    /// <summary>
    /// A thicker outline for a text: the same text drawn again in the outline color, shifted around a circle,
    /// behind it. The game's own edge is one pixel wide and cannot be made wider.
    /// </summary>
    private sealed class Outlined(ResNode layer)
    {
        private readonly List<TextNode> _copies = [];

        public void Hide()
        {
            foreach (var copy in _copies)
                copy.IsVisible = false;
        }

        /// <summary>Draws <paramref name="text"/> around <paramref name="source"/>, which must already be laid out.</summary>
        public void Update(TextNode source, string? text, Typeface font, float fontSize, Vector4 color, int thickness)
        {
            if (string.IsNullOrEmpty(text) || !source.IsVisible)
            {
                Hide();
                return;
            }

            var count = thickness <= 2 ? 8 : 16;
            while (_copies.Count < count)
            {
                var copy = MakeText(layer);
                copy.RemoveTextFlags(TextFlags.Edge);
                _copies.Add(copy);
            }

            for (var i = 0; i < _copies.Count; i++)
            {
                var copy = _copies[i];
                copy.IsVisible = i < count;
                if (!copy.IsVisible) continue;

                if (font.Italic) copy.AddTextFlags(TextFlags.Italic);
                else             copy.RemoveTextFlags(TextFlags.Italic);

                var angle = MathF.Tau * i / count;
                copy.FontType  = font.Type;
                copy.FontSize  = (uint)fontSize;
                copy.TextColor = color;
                copy.String    = text;
                copy.Size      = source.Size;
                copy.Position  = source.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * thickness;
            }
        }
    }

    /// <summary>The nodes of one message: its parts are laid out inside the container, which is what moves.</summary>
    private sealed class Slot
    {
        public required ResNode Container { get; init; }

        /// <summary>Holds the outline copies of the texts; first in the container, so they are drawn behind everything.</summary>
        public required ResNode OutlineLayer { get; init; }
        public Outlined? NameOutline;
        public Outlined? NumberOutline;
        public required TextNode Name { get; init; }
        public required TextNode Number { get; init; }
        public required IconImageNode Icon { get; init; }
        public required IconImageNode Type { get; init; }
        public Banded? NameBands;
        public Banded? NumberBands;
        public BattleTextMessage Message = null!;
        public float Elapsed;

        /// <summary>Seconds since it was shown. Unlike <see cref="Elapsed"/>, newer messages pushing it ahead do not change it.</summary>
        public float Age;
        public float Width;
        public float Height;
    }

    /// <summary>
    /// A vertical gradient: the text drawn once per band, each copy clipped to its band and in its own color.
    /// The game's text has one color, so this is the only way to have two in a letter.
    /// </summary>
    private sealed class Banded
    {
        private const int Count = 8;

        private readonly ResNode[] _clips = new ResNode[Count];
        private readonly TextNode[] _texts = new TextNode[Count];
        private float _height;

        public Banded(ResNode parent)
        {
            for (var i = 0; i < Count; i++)
            {
                _clips[i] = new ResNode { IsVisible = false };
                _clips[i].AddNodeFlags(NodeFlags.Clip);
                _clips[i].AttachNode(parent);
                _texts[i] = MakeText(_clips[i]);
            }
        }

        public float Width { get; private set; }

        public void Hide()
        {
            foreach (var clip in _clips)
                clip.IsVisible = false;
        }

        public void SetOutline(Vector4 color)
        {
            foreach (var text in _texts)
                text.TextOutlineColor = color;
        }

        public void Show(string text, Typeface font, float fontSize, float height, Vector4 top, Vector4 bottom)
        {
            _height = height;
            for (var i = 0; i < Count; i++)
            {
                SetText(_texts[i], text, font, fontSize, height, Vector4.Lerp(top, bottom, (i + 0.5f) / Count) with { W = 1f });
                _clips[i].IsVisible = true;
            }

            Width = _texts[0].Width;
        }

        /// <summary>Puts the bands at <paramref name="x"/>; the first and last reach the edges of the line, the rest share the letters.</summary>
        public void Place(float x)
        {
            var from = _height * 0.2f;
            var step = (_height * 0.85f - from) / Count;
            for (var i = 0; i < Count; i++)
            {
                // Whole pixels, and each band reaches one pixel into the next, which is drawn over it: no gap, no line.
                var y   = i == 0 ? 0f : MathF.Round(from + i * step);
                var end = i == Count - 1 ? MathF.Ceiling(_height) : MathF.Round(from + (i + 1) * step) + 1f;

                _clips[i].Position = new Vector2(x, y);
                _clips[i].Size     = new Vector2(Width, end - y);
                _texts[i].Position = new Vector2(0f, -y);
            }
        }
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

            var layer = new ResNode();
            layer.AttachNode(container);

            _free.Push(new Slot
            {
                Container = container,
                OutlineLayer = layer,
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
        slot.Age     = 0f;
        _active.Add(slot);
        Show(slot);
        MakeRoom();
    }

    /// <summary>
    /// Puts another message where one being shown is, without moving it. False when that one is no longer shown.
    /// </summary>
    public bool Replace(BattleTextMessage shown, BattleTextMessage message)
    {
        foreach (var slot in _active)
        {
            if (!ReferenceEquals(slot.Message, shown)) continue;

            slot.Message = message;
            Show(slot);
            return true;
        }

        return false;
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
            slot.Age     += delta;
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
            if (slot.Age > MergeWindow) break;
            if (slot.Message.MergeKey != message.MergeKey) continue;

            // Messages with the same key look the same: critical hits have no key, and are never merged.
            slot.Message.Amount += message.Amount;
            slot.Message.Hits   += message.Hits;

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

        slot.NameBands?.Hide();
        slot.NumberBands?.Hide();
        slot.Number.Alpha = 1f;

        var showName   = !hidden.Contains(GoblinBattleText.BattleTextPart.Name);
        var nameBands  = false;
        var nameOffset = 0f;
        if (showName && message is { GradientVertical: true, Verdict: { } verdict, NameColorEnd: { } verdictEnd } && !string.IsNullOrEmpty(message.ActionName))
        {
            // The name takes the room of the verdict too, which is drawn on top in bands.
            SetText(slot.Name, message.ActionName, font, fontSize, height, message.Color);
            slot.NameBands ??= new Banded(slot.Container);
            slot.NameBands.Show(verdict, font, fontSize, height, message.NameColor ?? message.Color, verdictEnd);

            nameOffset      = slot.Name.Width + fontSize * 0.3f;
            slot.Name.Width = nameOffset + slot.NameBands.Width;
            nameBands       = true;
        }
        else
        {
            SetName(slot.Name, showName ? message : null, font, fontSize, height);
        }
        var numberText = hidden.Contains(GoblinBattleText.BattleTextPart.Number) ? null : Format(message);
        SetText(slot.Number, numberText, font, fontSize, height, message.Color, message.GradientVertical ? null : message.ColorEnd);

        // The text of the number is there for its size; its bands are what shows.
        var numberBands = slot.Number.IsVisible && message is { GradientVertical: true, ColorEnd: not null };
        if (numberBands)
        {
            var numberEnd = message.ColorEnd!.Value;
            slot.NumberBands ??= new Banded(slot.Container);
            slot.NumberBands.Show(numberText!, font, fontSize, height, message.Color, numberEnd);
            slot.Number.Alpha = 0f;
        }

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

        if (numberBands)
            slot.NumberBands!.Place(slot.Number.Position.X);

        if (nameBands)
            slot.NameBands!.Place(slot.Name.Position.X + nameOffset);

        ApplyOutline(slot, message, font, fontSize, showName, nameBands, numberText);

        slot.Width  = Math.Max(0f, x - PartGap);
        slot.Height = height;

        var container = slot.Container;
        container.Size      = new Vector2(slot.Width, height);
        container.Origin    = new Vector2(slot.Width / 2f, height / 2f);
        container.IsVisible = true;
    }

    /// <summary>
    /// The outline of the user's choice, once the parts are laid out. Off, the texts keep the game's own dark edge;
    /// on, that edge takes the outline color and the copies behind the text widen it.
    /// </summary>
    private void ApplyOutline(Slot slot, BattleTextMessage message, Typeface font, float fontSize, bool showName, bool nameBands, string? numberText)
    {
        var on    = Options.OutlineEnabled;
        var color = on ? Options.Colors.Outline with { W = 1f } : Outline;

        slot.Name.TextOutlineColor   = color;
        slot.Number.TextOutlineColor = color;
        slot.NameBands?.SetOutline(color);
        slot.NumberBands?.SetOutline(color);

        if (!on)
        {
            slot.NameOutline?.Hide();
            slot.NumberOutline?.Hide();
            return;
        }

        // With a gradient verdict, only the name is drawn: the bands of the verdict keep the game's edge.
        var name = !showName ? null
            : nameBands || message.Verdict is null || string.IsNullOrEmpty(message.ActionName) ? message.ActionName
            : message.ActionName + " " + message.Verdict;

        var thickness = Math.Clamp(Options.OutlineThickness, 1, MaxOutline);
        slot.NameOutline   ??= new Outlined(slot.OutlineLayer);
        slot.NumberOutline ??= new Outlined(slot.OutlineLayer);
        slot.NameOutline.Update(slot.Name, name, font, fontSize, color, thickness);
        slot.NumberOutline.Update(slot.Number, numberText, font, fontSize, color, thickness);
    }

    /// <summary>The action name in the color of the message, then the verdict, if any, in its own color or gradient.</summary>
    private static void SetName(TextNode node, BattleTextMessage? message, Typeface font, float fontSize, float height)
    {
        if (message?.Verdict is not { } verdict || string.IsNullOrEmpty(message.ActionName))
        {
            SetText(node, message?.ActionName, font, fontSize, height, message?.NameColor ?? message?.Color ?? default, message?.NameColorEnd);
            return;
        }

        var name = message.ActionName + " ";
        SetText(node, name + verdict, font, fontSize, height, message.Color, null);

        var builder = new SeStringBuilder();
        builder.PushColorRgba(message.Color with { W = 1f });
        builder.Append(name);
        builder.PopColor();

        var from = message.NameColor ?? message.Color;
        var to   = message.NameColorEnd ?? from;
        var last = Math.Max(1, verdict.Length - 1);
        for (var i = 0; i < verdict.Length; i++)
        {
            builder.PushColorRgba(Vector4.Lerp(from, to, (float)i / last) with { W = 1f });
            builder.Append(verdict[i].ToString());
            builder.PopColor();
        }

        node.String = builder.ToReadOnlySeString();
    }

    private static void SetText(TextNode node, string? text, Typeface font, float fontSize, float height, Vector4 color, Vector4? colorEnd = null)
    {
        node.IsVisible = !string.IsNullOrEmpty(text);
        if (!node.IsVisible) return;

        // The node is reused: the style of the last message must not stay on.
        if (font.Italic) node.AddTextFlags(TextFlags.Italic);
        else             node.RemoveTextFlags(TextFlags.Italic);

        node.FontType  = font.Type;
        node.FontSize  = (uint)fontSize;
        node.TextColor = color;
        node.String    = colorEnd is { } end ? Gradient(text!, color, end) : text!;
        node.Size      = new Vector2(MathF.Ceiling(node.GetTextDrawSize().X) + 2f, height);
    }

    /// <summary>The text with each letter in its own color, from one color to the other.</summary>
    private static ReadOnlySeString Gradient(string text, Vector4 from, Vector4 to)
    {
        var builder = new SeStringBuilder();
        var last    = Math.Max(1, text.Length - 1);
        for (var i = 0; i < text.Length; i++)
        {
            var color = Vector4.Lerp(from, to, (float)i / last);
            builder.PushColorRgba(new Vector4(color.X, color.Y, color.Z, 1f));
            builder.Append(text[i].ToString());
            builder.PopColor();
        }

        return builder.ToReadOnlySeString();
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
