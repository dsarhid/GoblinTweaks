using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// Shared data type for category sidebar entries.
/// Crafted = recipes crafted at least once; Total = recipes in the category.
/// </summary>
internal readonly record struct CategoryEntry(string Name, int Crafted, int Total);

/// <summary>
/// One row in the category sidebar list.
/// Name on the left (clips if long), "crafted/total" right-aligned on the right.
/// </summary>
internal sealed class CategoryRowNode : ListItemNode<CategoryEntry>, IListItemNode
{
    public static float ItemHeight => 22f;

    private static readonly Vector4 ColorBody     = new(204 / 255f, 204 / 255f, 204 / 255f, 1f);
    private static readonly Vector4 ColorSubtitle = new(157 / 255f, 131 / 255f,  91 / 255f, 1f);

    // Fixed width reserved for the "10/74" count so long names never overlap it.
    private const float CountW = 54f;

    private readonly TextNode _name;
    private readonly TextNode _count;

    public CategoryRowNode()
    {
        _name = new TextNode { FontSize = 12, TextColor = ColorBody };
        _name.AddTextFlags(FFXIVClientStructs.FFXIV.Component.GUI.TextFlags.Ellipsis);
        _name.AttachNode(this);

        _count = new TextNode
        {
            FontSize      = 11,
            TextColor     = ColorSubtitle,
            AlignmentType = AlignmentType.Right,
        };
        _count.AttachNode(this);
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        var centerY = Math.Max(0f, (Height - 14f) / 2f);
        var nameW   = Math.Max(0f, Width - CountW - 12f);

        _name.Position  = new Vector2(8f, centerY);
        _name.Size      = new Vector2(nameW, 14f);

        _count.Position = new Vector2(Width - CountW - 4f, centerY + 1f);
        _count.Size     = new Vector2(CountW, 13f);
    }

    protected override void SetNodeData(CategoryEntry data)
    {
        _name.String  = data.Name;
        _count.String = $"{data.Crafted}/{data.Total}";

        // Tint green when everything in the category is crafted.
        _count.TextColor = data.Crafted >= data.Total && data.Total > 0
            ? new Vector4(0.45f, 0.80f, 0.45f, 1f)
            : ColorSubtitle;
    }
}
