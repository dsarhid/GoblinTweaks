using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.Classes;
using KamiToolKit.Enums;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>What the state line of an achievement says, and how far along it is (0 to 1, or -1 when unknown).</summary>
internal readonly record struct AchievementStatusText(string Text, Vector4 Color, string Detail, float Fraction = -1f);

/// <summary>Where the reward of an achievement stands. Whether it was claimed is not something the game tells.</summary>
internal enum AchievementReward
{
    /// <summary>The achievement gives no title or item.</summary>
    None,

    /// <summary>It has one, and the achievement is not done.</summary>
    Pending,

    /// <summary>The achievement is done.</summary>
    Done,
}

/// <summary>Everything one card shows, worked out when the list is built, so a card is redrawn when any of it changes.</summary>
internal sealed record AchievementCard(AchievementInfo Info, string State, Vector4 StateColor, float Fraction,
    bool Done, bool Tracked, bool Selected, AchievementReward Reward, string Tip);

/// <summary>The texts of the cards that depend on the language. Set by the window.</summary>
internal sealed record AchievementCardLabels(string Reward, string Done, string Pending, string Title, string Item);

/// <summary>
/// One achievement as a card, like in the game's own window: its icon, name and description on a cream background, the
/// points on the game's own medal on the right (the same texture its window uses, one star for each level), and a box
/// with the reward and whether it was claimed when it has one.
/// </summary>
internal sealed class AchievementCardNode : ListItemNode<AchievementCard>, IListItemNode
{
    public static float ItemHeight => 84f;

    internal static AchievementCardLabels Labels = new("Reward", "Done", "Pending", "Title", "Item");

    private const float IconSz   = 48f;
    private const float TextX    = 68f;
    private const float MedalW   = 67f;
    private const float MedalH   = 79f;
    private const float MedalPad = 12f;
    private const float GlyphShiftX = 1f;
    private const float GlyphShiftY = 4f;
    private const float RewardW  = 176f;
    private const float BarW     = 150f;

    // The medals of the game's achievement window: three tiers of points and a grey one. Each is 74 x 88 in the texture.
    private const string MedalTexture = "ui/uld/AchievementRank.tex";
    private const float  MedalSrcW    = 74f;
    private const float  MedalSrcH    = 88f;

    private static readonly Vector4 Border       = new(0.27f, 0.19f, 0.11f, 1f);
    private static readonly Vector4 BorderPicked = new(0.92f, 0.76f, 0.30f, 1f);
    private static readonly Vector4 Hover        = new(0.78f, 0.66f, 0.42f, 1f);
    private static readonly Vector4 Cream        = new(0.93f, 0.88f, 0.77f, 1f);
    private static readonly Vector4 CreamDone    = new(0.85f, 0.91f, 0.79f, 1f);
    private static readonly Vector4 CreamUndone  = new(0.86f, 0.83f, 0.76f, 1f);
    private static readonly Vector4 InkUndone    = new(0.38f, 0.31f, 0.23f, 1f);
    private static readonly Vector3 Dimmed       = new(0.72f, 0.72f, 0.72f);
    private static readonly Vector4 Stripe       = new(0.92f, 0.76f, 0.30f, 1f);
    private static readonly Vector4 InkName      = new(0.17f, 0.11f, 0.06f, 1f);
    private static readonly Vector4 InkDone      = new(0.12f, 0.38f, 0.14f, 1f);
    private static readonly Vector4 InkText      = new(0.33f, 0.26f, 0.18f, 1f);
    private static readonly Vector4 InkMuted     = new(0.45f, 0.38f, 0.30f, 1f);
    private static readonly Vector4 InkWarn      = new(0.70f, 0.36f, 0.08f, 1f);
    private static readonly Vector4 White        = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 BarTrack     = new(0.20f, 0.14f, 0.08f, 0.30f);
    private static readonly Vector4 BarFill      = new(0.30f, 0.50f, 0.78f, 1f);
    private static readonly Vector4 BarDone      = new(0.30f, 0.62f, 0.34f, 1f);
    private static readonly Vector4 BoxFill      = new(0.84f, 0.77f, 0.63f, 1f);

    private readonly ColorImageNode _border;
    private readonly ColorImageNode _fill;
    private readonly ColorImageNode _stripe;
    private readonly IconImageNode _icon;
    private readonly TextNode _name;
    private readonly TextNode _description;
    private readonly ColorImageNode _barTrack;
    private readonly ColorImageNode _bar;
    private readonly TextNode _state;
    private readonly TextNode _category;
    private readonly ImageNode _medal;
    private readonly TextNode _points;
    private readonly ColorImageNode _boxBorder;
    private readonly ColorImageNode _boxFill;
    private readonly TextNode _boxLabel;
    private readonly IconImageNode _boxIcon;
    private readonly TextNode _boxName;
    private readonly TextNode _boxState;
    private readonly CollisionNode _boxHit;

    private AchievementCard? _card;
    private float _rewardX;
    private bool _hasBar;
    private bool _hovered;

    public AchievementCardNode()
    {
        _border = new ColorImageNode { Color = Border };
        _border.AttachNode(this);
        _fill = new ColorImageNode { Color = Cream };
        _fill.AttachNode(this);
        _stripe = new ColorImageNode { Color = Stripe, IsVisible = false };
        _stripe.AttachNode(this);

        _icon = new IconImageNode { FitTexture = true, Size = new Vector2(IconSz, IconSz) };
        _icon.AttachNode(this);

        _name = Text(15, InkName, AlignmentType.Left, wrap: false);
        _description = Text(12, InkText, AlignmentType.Left, wrap: true);

        _barTrack = new ColorImageNode { Color = BarTrack, Size = new Vector2(BarW, 7f) };
        _barTrack.AttachNode(this);
        _bar = new ColorImageNode { Color = BarFill, Size = new Vector2(1f, 7f) };
        _bar.AttachNode(this);
        _state = Text(12, InkText, AlignmentType.Left, wrap: false);
        _category = Text(11, InkMuted, AlignmentType.Left, wrap: false);

        _boxBorder = new ColorImageNode { Color = Border };
        _boxBorder.AttachNode(this);
        _boxFill = new ColorImageNode { Color = BoxFill };
        _boxFill.AttachNode(this);
        _boxLabel = Text(10, InkMuted, AlignmentType.Left, wrap: false);
        _boxIcon = new IconImageNode { FitTexture = true, Size = new Vector2(30f, 30f), IsVisible = false };
        _boxIcon.AttachNode(this);
        _boxName = Text(12, InkName, AlignmentType.Left, wrap: false);
        _boxState = Text(11, InkText, AlignmentType.Left, wrap: false);

        // Over the reward box, for its tooltip: the game's own for an item, the title's name for a title.
        _boxHit = new CollisionNode { CollisionType = CollisionType.Hit, IsVisible = false };
        _boxHit.AttachNode(this);

        // One medal per tier of points; the card shows the one that fits its achievement.
        _medal = new ImageNode { Size = new Vector2(MedalW, MedalH) };
        var tiers = new Part[4];
        for (var tier = 0; tier < tiers.Length; tier++)
        {
            tiers[tier] = new Part
            {
                Id                 = (uint)tier,
                TexturePath        = MedalTexture,
                TextureCoordinates = new Vector2(tier * MedalSrcW, 0f),
                Size               = new Vector2(MedalSrcW, MedalSrcH),
            };
        }

        _medal.AddPart(tiers);
        _medal.PartId = 0;
        _medal.AttachNode(this);

        _points = Text(18, White, AlignmentType.Center, wrap: false);
        _points.TextOutlineColor = new Vector4(0.12f, 0.08f, 0.04f, 1f);
        _points.AddTextFlags(TextFlags.Edge);
    }

    private TextNode Text(uint fontSize, Vector4 color, AlignmentType alignment, bool wrap)
    {
        var node = new TextNode { FontSize = fontSize, TextColor = color, AlignmentType = alignment };
        node.AddTextFlags(wrap ? TextFlags.WordWrap | TextFlags.MultiLine : TextFlags.Ellipsis);
        node.AttachNode(this);
        return node;
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        var w = Width;
        var h = Height;

        _border.Size = new Vector2(w, h);
        _fill.Position = new Vector2(2f, 2f);
        _fill.Size = new Vector2(Math.Max(0f, w - 4f), Math.Max(0f, h - 4f));
        _stripe.Position = new Vector2(2f, 2f);
        _stripe.Size = new Vector2(5f, Math.Max(0f, h - 4f));

        _icon.Position = new Vector2(14f, (h - IconSz) / 2f - 4f);

        var medalX = w - MedalPad - MedalW;
        var medalY = Math.Max(0f, (h - MedalH) / 2f);
        _medal.Position = new Vector2(medalX, medalY);

        // The number sits in the middle of the medal's circle: at (37.2, 35.5) of the 74 x 88 of the texture, scaled to the
        // node, and a little lower than the middle of its text box because a digit sits high in its line.
        var circleX = 37.2f * MedalW / MedalSrcW;
        var circleY = 35.5f * MedalH / MedalSrcH;
        _points.Position = new Vector2(medalX + circleX - MedalW / 2f + GlyphShiftX, medalY + circleY - 11f + GlyphShiftY);
        _points.Size = new Vector2(MedalW, 22f);

        _rewardX = medalX - 8f - RewardW;
        _boxBorder.Position = new Vector2(_rewardX, 14f);
        _boxBorder.Size = new Vector2(RewardW, h - 28f);
        _boxFill.Position = new Vector2(_rewardX + 2f, 16f);
        _boxFill.Size = new Vector2(RewardW - 4f, h - 32f);
        _boxLabel.Position = new Vector2(_rewardX + 8f, 19f);
        _boxLabel.Size = new Vector2(RewardW - 16f, 12f);
        _boxIcon.Position = new Vector2(_rewardX + 8f, 34f);
        _boxHit.Position = new Vector2(_rewardX, 14f);
        _boxHit.Size = new Vector2(RewardW, h - 28f);

        PlaceReward(_boxIcon.IsVisible);
        Layout(_card?.Info.Reward.Length > 0);
    }

    /// <summary>The name and the state of the reward start after its icon, when it has one.</summary>
    private void PlaceReward(bool hasIcon)
    {
        var x = _rewardX + (hasIcon ? 44f : 8f);
        var w = RewardW - (hasIcon ? 52f : 16f);
        _boxName.Position = new Vector2(x, 33f);
        _boxName.Size = new Vector2(w, 15f);
        _boxState.Position = new Vector2(x, 50f);
        _boxState.Size = new Vector2(w, 14f);
    }

    /// <summary>The text takes the room left of the medal, and of the reward box when there is one.</summary>
    private void Layout(bool hasReward)
    {
        var right = hasReward ? _rewardX - 10f : Width - MedalPad - MedalW - 10f;
        var textW = Math.Max(0f, right - TextX);

        _name.Position = new Vector2(TextX, 8f);
        _name.Size = new Vector2(textW, 18f);
        _description.Position = new Vector2(TextX, 28f);
        _description.Size = new Vector2(textW, 30f);

        _barTrack.Position = new Vector2(TextX, 66f);
        _bar.Position = new Vector2(TextX, 66f);
        _state.Position = new Vector2(_hasBar ? TextX + BarW + 8f : TextX, 61f);
        _state.Size = new Vector2(Math.Min(120f, textW), 16f);
        _category.Position = new Vector2(TextX + BarW + 132f, 62f);
        _category.Size = new Vector2(Math.Max(0f, textW - BarW - 132f), 14f);
    }

    /// <summary>A light border under the mouse, since the card covers the list's own highlight.</summary>
    public override void Update()
    {
        base.Update();
        if (IsHovered == _hovered) return;

        _hovered = IsHovered;
        _border.Color = _hovered ? Hover : _card?.Selected == true ? BorderPicked : Border;
    }

    /// <summary>The medal of the game for each tier of points: 1 star up to 5, 2 up to 20, 3 above. Greyed while it is not done.</summary>
    private static uint TierOf(int points) => points <= 5 ? 2u : points <= 20 ? 1u : 0u;

    protected override void SetNodeData(AchievementCard card)
    {
        _card = card;
        var data = card.Info;

        _icon.IsVisible = data.IconId != 0;
        _icon.IconId = data.IconId;

        _border.Color = _hovered ? Hover : card.Selected ? BorderPicked : Border;
        _fill.Color = card.Done ? CreamDone : CreamUndone;
        _stripe.IsVisible = card.Tracked;
        _icon.MultiplyColor = card.Done ? Vector3.One : Dimmed;

        _name.String = data.Name;
        _name.TextColor = card.Done ? InkDone : InkUndone;
        _description.String = data.Description;
        _category.String = data.Category;

        _state.String = card.State;
        _state.TextColor = card.Done ? InkDone : card.StateColor;

        var hasBar = card.Fraction >= 0f;
        _hasBar = hasBar;
        _barTrack.IsVisible = hasBar;
        _bar.IsVisible = hasBar;
        if (hasBar) _bar.Width = Math.Max(1f, BarW * Math.Clamp(card.Fraction, 0f, 1f));
        _bar.Color = card.Done ? BarDone : BarFill;

        _medal.PartId = TierOf(data.Points);
        _medal.MultiplyColor = card.Done ? Vector3.One : new Vector3(0.55f, 0.55f, 0.55f);
        _points.String = data.Points.ToString();

        var hasReward = data.Reward.Length > 0;
        _boxBorder.IsVisible = _boxFill.IsVisible = _boxLabel.IsVisible = _boxName.IsVisible = _boxState.IsVisible = hasReward;
        _boxIcon.IsVisible = hasReward && data.RewardIconId != 0;
        if (_boxIcon.IsVisible) _boxIcon.IconId = data.RewardIconId;
        PlaceReward(_boxIcon.IsVisible);
        if (hasReward)
        {
            // A title and an item are told apart in the label; the item shows the game's own tooltip.
            var kind = data.HasItem && !data.HasTitle ? Labels.Item : Labels.Title;
            _boxLabel.String = $"{Labels.Reward} - {kind}".ToUpperInvariant();
            _boxName.String = data.Reward;
            (_boxState.String, _boxState.TextColor) = card.Reward == AchievementReward.Done
                ? (Labels.Done, InkDone)
                : (Labels.Pending, InkMuted);
        }

        _boxHit.IsVisible = hasReward;
        _boxHit.ItemTooltip = hasReward && data.HasItem ? data.RewardItemId : 0;
        _boxHit.TextTooltip = hasReward && !data.HasItem ? $"{Labels.Title}: {data.Reward}" : string.Empty;

        Layout(hasReward);
        TextTooltip = card.Tip;
    }
}
