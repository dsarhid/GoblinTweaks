using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;

namespace GoblinTweaks.UI.Nodes;

/// <summary>One combat event waiting to be, or being, scrolled in a <see cref="BattleTextAreaNode"/>.</summary>
internal sealed class BattleTextMessage
{
    public long Amount { get; set; }

    /// <summary>How many hits were merged into this message.</summary>
    public int Hits { get; set; } = 1;

    public bool Crit { get; set; }

    /// <summary>Text shown instead of an amount (miss, dodge, cooldown ready...).</summary>
    public string? Label { get; init; }

    public string? ActionName { get; init; }

    /// <summary>Icon of the action, 0 for none.</summary>
    public uint IconId { get; init; }

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
    private const int   MaxMessages = 15;
    private const float FadeStart   = 0.8f;   // fraction of the scroll after which the text fades out
    private const float MergeWindow = 0.35f;  // seconds during which hits of the same action are merged
    private const float PopTime     = 0.18f;  // seconds a critical hit takes to shrink to its size
    private const float PopScale    = 0.6f;
    private const float CritScale   = 1.35f;
    private const float CurveWidth  = 60f;
    private const float PartGap     = 4f;
    private const float MaxDelta    = 0.1f;

    private static readonly Vector4 Outline = new(0.05f, 0.05f, 0.05f, 1f);

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

    public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;

    private float Duration => Math.Max(0.5f, Area.DurationTenths / 10f);

    public void Add(BattleTextMessage message)
    {
        if (TryMerge(message)) return;

        Slot slot;
        if (_free.Count > 0)
        {
            slot = _free.Pop();
        }
        else
        {
            slot = _active[0];
            _active.RemoveAt(0);
        }

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
        var speed  = Math.Max(1f, Area.Height) / Duration;
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
        var fontSize = MathF.Round(Area.FontSize * (message.Crit ? CritScale : 1f));
        var height   = fontSize + 6f;
        var font     = Options.Font switch
        {
            GoblinBattleText.BattleTextFont.Axis        => FontType.Axis,
            GoblinBattleText.BattleTextFont.TrumpGothic => FontType.TrumpGothic,
            GoblinBattleText.BattleTextFont.Miedinger   => FontType.MiedingerMed,
            _                                           => FontType.Jupiter,
        };

        var hidden = message.Hidden ?? Options.Hidden;

        SetText(slot.Name, hidden.Contains(GoblinBattleText.BattleTextPart.Name) ? null : message.ActionName, font, fontSize, height, message.NameColor ?? message.Color);
        SetText(slot.Number, hidden.Contains(GoblinBattleText.BattleTextPart.Number) ? null : Format(message), font, fontSize, height, message.Color);
        SetIcon(slot.Icon, hidden.Contains(GoblinBattleText.BattleTextPart.Icon) ? 0 : message.IconId, fontSize + 2f);
        SetIcon(slot.Type, hidden.Contains(GoblinBattleText.BattleTextPart.Type) ? 0 : message.TypeIconId, fontSize - 2f);

        var x = 0f;
        foreach (var part in message.Order ?? Options.Order)
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

    private static void SetText(TextNode node, string? text, FontType font, float fontSize, float height, Vector4 color)
    {
        node.IsVisible = !string.IsNullOrEmpty(text);
        if (!node.IsVisible) return;

        node.FontType  = font;
        node.FontSize  = (uint)fontSize;
        node.TextColor = color;
        node.String    = text!;
        node.Size      = new Vector2(MathF.Ceiling(node.GetTextDrawSize().X) + 2f, height);
    }

    private static void SetIcon(IconImageNode node, uint iconId, float size)
    {
        node.IsVisible = iconId != 0;
        if (!node.IsVisible) return;

        node.IconId = iconId;
        node.Size   = new Vector2(size, size);
    }

    private void Place(Slot slot, float progress)
    {
        var height = Math.Max(1f, Area.Height);
        var travel = progress * height;
        var y      = (slot.Message.Downwards ? travel - height / 2f : height / 2f - travel) - slot.Height / 2f;

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
        var y = (Area.OffsetY < 0 ? -pile : pile) - slot.Height / 2f;
        var x = -slot.Width * Math.Clamp(Area.TextAnchor, 0, 100) / 100f;

        Apply(slot, new Vector2(x, y), progress);
    }

    private static void Apply(Slot slot, Vector2 position, float progress)
    {
        var pop = slot.Message.Crit && slot.Elapsed < PopTime ? 1f + PopScale * (1f - slot.Elapsed / PopTime) : 1f;

        var container = slot.Container;
        container.Position = position;
        container.Scale    = new Vector2(pop, pop);
        container.Alpha    = progress < FadeStart ? 1f : 1f - (progress - FadeStart) / (1f - FadeStart);
    }

    private void Release(Slot slot)
    {
        slot.Container.IsVisible = false;
        _free.Push(slot);
    }
}
