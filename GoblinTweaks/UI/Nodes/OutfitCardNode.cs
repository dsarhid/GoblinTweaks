using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Glamour;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>What a card needs to know about one piece.</summary>
internal sealed record CardPiece(
    GlamourSlot Slot, OutfitPiece Piece, uint IconId, string Tooltip, Rgb? Dye0, Rgb? Dye1, bool Locked);

/// <summary>A button of a card.</summary>
internal sealed record CardButton(string Label, string? Tooltip, Action Click);

/// <summary>The buttons of a card (right-aligned, in order) and what its lock and cross do.</summary>
internal sealed record CardActions(
    IReadOnlyList<CardButton> Buttons, string LockTooltip, Action<CardPiece, bool>? Lock,
    string DislikeTooltip, Action<CardPiece>? Dislike);

/// <summary>
/// One proposed (or saved) outfit: its title, the icon of every piece with the dyes under it, a lock to keep that
/// piece when generating again and a cross to never see it again, a line saying why it was proposed, and two buttons
/// (Try On, Favorite or Remove, and with Glamourer: wear it and save it there).
/// Built whole each time the cards change; it lives inside the window's scrolling list. An AI outfit shows its whole
/// opinion, on as many lines as it is given.
/// </summary>
internal sealed class OutfitCardNode : ResNode
{
    private const float IconSize  = 40f;
    private const float IconStep  = 52f;
    private const float IconsY    = 30f;
    private const float ButtonW   = 104f;
    private const float ButtonGap = 6f;
    private const float ReasonY   = IconsY + IconSize + 40f;
    private const float LineH     = 17f;

    /// <summary>Height of a card whose reason line takes <paramref name="lines"/> lines.</summary>
    public static float HeightFor(int lines) => ReasonY + Math.Max(1, lines) * LineH + 14f;

    private static readonly Vector4 Gold  = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 Muted = new(0.62f, 0.62f, 0.62f, 1f);

    public OutfitCardNode(float width, string title, string reason, int reasonLines, IReadOnlyList<CardPiece> pieces, CardActions actions)
    {
        var height = HeightFor(reasonLines);
        Size = new Vector2(width, height);

        // The title stops before the buttons; a long one (an AI request) ends in "..." and shows whole on hover.
        var titleNode = new TextNode
        {
            String    = title,
            Position  = new Vector2(0f, 2f),
            Size      = new Vector2(Math.Max(60f, width - actions.Buttons.Count * (ButtonW + ButtonGap) - 12f), 20f),
            TextColor = Gold,
            FontSize  = 14,
        };
        titleNode.AddTextFlags(TextFlags.Ellipsis);
        if (title.Length > 24) titleNode.TextTooltip = title;
        titleNode.AttachNode(this);

        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            var x = i * IconStep;

            new IconImageNode
            {
                IconId      = piece.IconId,
                FitTexture  = true,
                Position    = new Vector2(x, IconsY),
                Size        = new Vector2(IconSize, IconSize),
                TextTooltip = piece.Tooltip,
            }.AttachNode(this);

            // The dye of each channel, as a small bar under the icon.
            if (piece.Dye0 is { } dye0) Bar(x, dye0, piece.Dye1 is null ? IconSize : IconSize / 2f - 1f);
            if (piece.Dye1 is { } dye1) Bar(x + IconSize / 2f + 1f, dye1, IconSize / 2f - 1f);

            if (actions.Dislike is { } onDislike)
            {
                var cross = new CircleButtonNode
                {
                    Icon        = CircleButtonIcon.CrossSmall,
                    Position    = new Vector2(x + IconSize / 2f, IconsY + IconSize + 10f),
                    Size        = new Vector2(20f, 20f),
                    TextTooltip = actions.DislikeTooltip,
                };
                cross.OnClick = () => onDislike(piece);
                cross.AttachNode(this);
            }

            if (actions.Lock is { } onLock)
            {
                var box = new CheckboxNode
                {
                    String      = string.Empty,
                    Position    = new Vector2(x + (actions.Dislike is null ? (IconSize - 20f) / 2f : 0f), IconsY + IconSize + 10f),
                    Size        = new Vector2(20f, 20f),
                    IsChecked   = piece.Locked,
                    TextTooltip = actions.LockTooltip,
                };
                box.OnClick = on => onLock(piece, on);
                box.AttachNode(this);
            }
        }

        var line = new TextNode
        {
            String        = reason,
            Position      = new Vector2(0f, ReasonY),
            Size          = new Vector2(width, Math.Max(1, reasonLines) * LineH),
            TextColor     = Muted,
            FontSize      = 12,
            AlignmentType = AlignmentType.TopLeft,
        };
        if (reasonLines > 1) line.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
        else line.AddTextFlags(TextFlags.Ellipsis);
        if (reason.Length > 60) line.TextTooltip = reason;   // an AI opinion can be longer than its lines
        line.AttachNode(this);

        var buttonX = width - actions.Buttons.Count * (ButtonW + ButtonGap) + ButtonGap;
        foreach (var button in actions.Buttons)
        {
            Button(buttonX, button);
            buttonX += ButtonW + ButtonGap;
        }

        new HorizontalLineNode { Position = new Vector2(0f, height - 4f), Size = new Vector2(width, 2f) }.AttachNode(this);
    }

    private void Bar(float x, Rgb color, float width)
        => new ColorImageNode
        {
            Color    = PaletteNode.ToVector(color),
            Position = new Vector2(x, IconsY + IconSize + 2f),
            Size     = new Vector2(width, 5f),
        }.AttachNode(this);

    private void Button(float x, CardButton spec)
    {
        var button = new TextButtonNode
        {
            String   = spec.Label,
            Position = new Vector2(x, 0f),
            Size     = new Vector2(ButtonW, 26f),
        };
        if (spec.Tooltip is not null) button.TextTooltip = spec.Tooltip;
        button.OnClick = spec.Click;
        button.AttachNode(this);
    }
}
