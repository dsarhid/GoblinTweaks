using System.Numerics;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>X position and width of every column of the GoblinSniper list, shared by rows and the header.</summary>
internal readonly record struct SniperColumns(float NameX, float NameW, float LevelX, float AverageX, float PriceX, float DiscountX, float ResaleX, float WorldX, float AgeX)
{
    public const float IconSz    = 32f;
    public const float LevelW    = 50f;
    public const float PriceW    = 112f;
    public const float DiscountW = 64f;
    public const float ResaleW   = 70f;
    public const float WorldW    = 104f;
    public const float AgeW      = 60f;
    private const float Gap      = 8f;

    public static SniperColumns For(float width)
    {
        var ageX      = width - AgeW - Gap;
        var worldX    = ageX - WorldW - Gap;
        var resaleX   = worldX - ResaleW - Gap - 6f;
        var discountX = resaleX - DiscountW - Gap;
        var priceX    = discountX - PriceW - Gap;
        var averageX  = priceX - PriceW - Gap;
        var levelX    = averageX - LevelW - Gap;
        var nameX     = 6f + IconSz + 8f;

        return new SniperColumns(nameX, Math.Max(0f, levelX - nameX - Gap), levelX, averageX, priceX, discountX, resaleX, worldX, ageX);
    }
}

/// <summary>
/// One deal in the GoblinSniper list: icon, name and type, item level, normal price, listed price
/// (with the next seller's price below), discount, resale chance (with sales per day below), world and
/// how old the data is.
/// </summary>
internal sealed class SniperRowNode : ListItemNode<SniperDeal>, IListItemNode
{
    public static float ItemHeight => 40f;

    /// <summary>Set by the addon: second line under the item name (its type).</summary>
    internal static Func<SniperDeal, string>? SubText;

    /// <summary>Set by the addon: localized "Next: {0}" label for the next seller's price.</summary>
    internal static string NextFormat = "{0}";

    /// <summary>Set by the addon: localized "{0}/day" label for the sales per day.</summary>
    internal static string PerDayFormat = "{0}";

    private static readonly Vector4 ColorName     = new(0.93f, 0.93f, 0.93f, 1f);
    private static readonly Vector4 ColorSubtitle = new(157 / 255f, 131 / 255f,  91 / 255f, 1f);
    private static readonly Vector4 ColorMuted    = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly Vector4 ColorPrice    = new(0.40f, 0.88f, 0.46f, 1f);
    private static readonly Vector4 ColorDiscount = new(0.96f, 0.58f, 0.20f, 1f);
    private static readonly Vector4 ColorLikely   = new(0.40f, 0.88f, 0.46f, 1f);
    private static readonly Vector4 ColorMaybe    = new(0.95f, 0.82f, 0.28f, 1f);
    private static readonly Vector4 ColorUnlikely = new(0.92f, 0.42f, 0.38f, 1f);

    private static readonly string Gil = SeIconChar.Gil.ToIconString();
    private static readonly string HighQuality = SeIconChar.HighQuality.ToIconString();

    private readonly IconImageNode _icon;
    private readonly TextNode _name;
    private readonly TextNode _sub;
    private readonly TextNode _level;
    private readonly TextNode _average;
    private readonly TextNode _price;
    private readonly TextNode _next;
    private readonly TextNode _discount;
    private readonly TextNode _resale;
    private readonly TextNode _perDay;
    private readonly TextNode _world;
    private readonly TextNode _age;

    public SniperRowNode()
    {
        _icon = new IconImageNode { FitTexture = true, Size = new Vector2(SniperColumns.IconSz, SniperColumns.IconSz) };
        _icon.AttachNode(this);

        _name = new TextNode { FontSize = 13, TextColor = ColorName };
        _name.AddTextFlags(TextFlags.Ellipsis);
        _name.AttachNode(this);

        _sub = new TextNode { FontSize = 11, TextColor = ColorSubtitle };
        _sub.AddTextFlags(TextFlags.Ellipsis);
        _sub.AttachNode(this);

        _level    = Column(13, ColorMuted, AlignmentType.Right);
        _average  = Column(13, ColorName, AlignmentType.Right);
        _price    = Column(13, ColorPrice, AlignmentType.Right);
        _next     = Column(11, ColorMuted, AlignmentType.Right);
        _discount = Column(14, ColorDiscount, AlignmentType.Right);
        _resale   = Column(13, ColorLikely, AlignmentType.Right);
        _perDay   = Column(11, ColorMuted, AlignmentType.Right);
        _world    = Column(12, ColorName, AlignmentType.Left);
        _age      = Column(12, ColorMuted, AlignmentType.Right);
    }

    private TextNode Column(uint fontSize, Vector4 color, AlignmentType alignment)
    {
        var node = new TextNode { FontSize = fontSize, TextColor = color, AlignmentType = alignment };
        node.AttachNode(this);
        return node;
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        var columns = SniperColumns.For(Width);
        var middle  = Math.Max(0f, (Height - 16f) / 2f);

        _icon.Position = new Vector2(6f, Math.Max(0f, (Height - SniperColumns.IconSz) / 2f));

        _name.Position = new Vector2(columns.NameX, 5f);
        _name.Size     = new Vector2(columns.NameW, 16f);
        _sub.Position  = new Vector2(columns.NameX, 22f);
        _sub.Size      = new Vector2(columns.NameW, 13f);

        _level.Position = new Vector2(columns.LevelX, middle);
        _level.Size     = new Vector2(SniperColumns.LevelW, 16f);

        _average.Position = new Vector2(columns.AverageX, middle);
        _average.Size     = new Vector2(SniperColumns.PriceW, 16f);

        _price.Position = new Vector2(columns.PriceX, 5f);
        _price.Size     = new Vector2(SniperColumns.PriceW, 16f);
        _next.Position  = new Vector2(columns.PriceX, 22f);
        _next.Size      = new Vector2(SniperColumns.PriceW, 13f);

        _discount.Position = new Vector2(columns.DiscountX, middle);
        _discount.Size     = new Vector2(SniperColumns.DiscountW, 16f);

        _resale.Position = new Vector2(columns.ResaleX, 5f);
        _resale.Size     = new Vector2(SniperColumns.ResaleW, 16f);
        _perDay.Position = new Vector2(columns.ResaleX, 22f);
        _perDay.Size     = new Vector2(SniperColumns.ResaleW, 13f);

        _world.Position = new Vector2(columns.WorldX, middle);
        _world.Size     = new Vector2(SniperColumns.WorldW, 16f);

        _age.Position = new Vector2(columns.AgeX, middle);
        _age.Size     = new Vector2(SniperColumns.AgeW, 16f);
    }

    protected override void SetNodeData(SniperDeal data)
    {
        _icon.IconId = data.IconId;

        var name = data.Hq ? $"{data.Name} {HighQuality}" : data.Name;
        _name.String = data.Quantity > 1 ? $"{name}  x{data.Quantity}" : name;
        _sub.String  = SubText?.Invoke(data) ?? string.Empty;

        _level.String    = data.ItemLevel.ToString();
        _average.String  = $"{data.AveragePrice:N0}{Gil}";
        _price.String    = $"{data.Price:N0}{Gil}";
        _next.String     = data.NextPrice > 0 ? string.Format(NextFormat, $"{data.NextPrice:N0}") : string.Empty;
        _discount.String = $"-{data.Discount}%";
        _resale.String    = $"{data.ResaleChance}%";
        _resale.TextColor = data.ResaleChance >= 75 ? ColorLikely : data.ResaleChance >= 40 ? ColorMaybe : ColorUnlikely;
        _perDay.String    = string.Format(PerDayFormat, data.SalesPerDay >= 10 ? $"{data.SalesPerDay:0}" : $"{data.SalesPerDay:0.##}");
        _world.String    = data.World;
        _age.String      = FormatAge(data.Reviewed);

        ItemTooltip = data.ItemId;
    }

    private static string FormatAge(DateTime reviewed)
    {
        if (reviewed == DateTime.MinValue) return "?";

        var age = DateTime.UtcNow - reviewed;
        return age.TotalHours >= 1 ? $"{(int)age.TotalHours}h" : $"{Math.Max(0, (int)age.TotalMinutes)}m";
    }
}
