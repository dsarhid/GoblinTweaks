using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// One line of the category column: "everything", a group of the game (battle, PvP, character...) that folds out, or one
/// of its categories. Key is "" for everything, "k:name" for a group and the category id for a category.
/// </summary>
internal sealed record AchievementCategoryEntry(string Key, string Label, bool Group, bool Expanded, int Done, int Total, bool Active, bool Loaded);

/// <summary>A line of the category column, with how many of its achievements are done.</summary>
internal sealed class AchievementCategoryRowNode : ListItemNode<AchievementCategoryEntry>, IListItemNode
{
    public static float ItemHeight => 26f;

    private static readonly Vector4 GroupGold  = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 SubWhite   = new(0.86f, 0.86f, 0.86f, 1f);
    private static readonly Vector4 ActiveGold = new(0.98f, 0.86f, 0.42f, 1f);
    private static readonly Vector4 Muted      = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 ActiveBack = new(0.45f, 0.38f, 0.18f, 0.55f);
    private static readonly Vector4 GroupBack  = new(0.30f, 0.28f, 0.22f, 0.45f);

    private readonly ColorImageNode _back;
    private readonly TextNode _text;
    private readonly TextNode _count;

    public AchievementCategoryRowNode()
    {
        _back = new ColorImageNode { IsVisible = false };
        _back.AttachNode(this);

        _text = new TextNode { FontSize = 13, AlignmentType = AlignmentType.Left };
        _text.AddTextFlags(TextFlags.Ellipsis);
        _text.AttachNode(this);

        _count = new TextNode { FontSize = 11, AlignmentType = AlignmentType.Right, TextColor = Muted };
        _count.AttachNode(this);
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        _back.Size  = new Vector2(Width, Height);
        _count.Position = new Vector2(Width - 66f, (Height - 14f) / 2f);
        _count.Size = new Vector2(60f, 14f);
    }

    protected override void SetNodeData(AchievementCategoryEntry entry)
    {
        var indent = entry.Group || entry.Key.Length == 0 ? 8f : 22f;
        _text.Position = new Vector2(indent, (Height - 16f) / 2f);
        _text.Size = new Vector2(Math.Max(0f, Width - indent - 70f), 16f);

        var mark = entry.Group ? (entry.Expanded ? "- " : "+ ") : string.Empty;
        _text.String = mark + entry.Label;
        _text.TextColor = entry.Active ? ActiveGold : entry.Group || entry.Key.Length == 0 ? GroupGold : SubWhite;

        _count.String = entry.Loaded ? $"{entry.Done}/{entry.Total}" : entry.Total.ToString();

        _back.IsVisible = entry.Active || entry.Group;
        _back.Color = entry.Active ? ActiveBack : GroupBack;
    }
}
