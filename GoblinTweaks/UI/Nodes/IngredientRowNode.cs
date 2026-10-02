using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;
using GoblinTweaks.Tweaks;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// One ingredient in the detail tree. <see cref="Depth"/> indents nested sub-ingredients;
/// <see cref="Craftable"/> rows can be expanded to reveal their own sub-recipe (<see cref="NodeId"/>
/// identifies the tree node so the addon can toggle it).
/// </summary>
internal readonly record struct IngredientRow(
    IngredientCheck Check, string PrimaryLocation, int Depth, bool Craftable, bool Expanded, int NodeId);

/// <summary>
/// One row in the ingredient detail list: indent + expand caret + game icon + name
/// (colored by availability) + qty + primary inventory location.
/// </summary>
internal sealed class IngredientRowNode : ListItemNode<IngredientRow>, IListItemNode
{
    public static float ItemHeight => 34f;

    /// <summary>Set by the addon; invoked with the ingredient item id on right-click.</summary>
    internal static Action<uint>? OnItemRightClick;

    private static readonly Vector4 ColorFulfilled = new(0.30f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 ColorElsewhere = new(0.95f, 0.80f, 0.20f, 1f);
    private static readonly Vector4 ColorMissing   = new(0.85f, 0.35f, 0.30f, 1f);
    private static readonly Vector4 ColorSubtitle  = new(157 / 255f, 131 / 255f,  91 / 255f, 1f);
    private static readonly Vector4 ColorCaret     = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);

    private const float IndentW = 16f;
    private const float CaretW  = 14f;
    private const float IconSz  = 26f;
    private const float Gap     = 6f;
    private const float QtyW    = 56f;
    private const float LocW    = 130f;

    private readonly TextNode      _caret;
    private readonly IconImageNode _icon;
    private readonly TextNode      _name;
    private readonly TextNode      _qty;
    private readonly TextNode      _location;

    // Current row depth. Stored separately because the list calls SetNodeData *before*
    // updating ItemData, so reading ItemData.Depth during layout would use the stale value.
    private int _depth;

    public unsafe IngredientRowNode()
    {
        _caret = new TextNode { FontSize = 13, TextColor = ColorCaret, AlignmentType = AlignmentType.Center };
        _caret.AttachNode(this);

        _icon = new IconImageNode { FitTexture = true, Size = new Vector2(IconSz, IconSz) };
        _icon.AttachNode(this);

        _name = new TextNode { FontSize = 12 };
        _name.AddTextFlags(TextFlags.Ellipsis);
        _name.AttachNode(this);

        _qty = new TextNode { FontSize = 11, TextColor = ColorSubtitle };
        _qty.AttachNode(this);

        _location = new TextNode { FontSize = 10, TextColor = ColorSubtitle };
        _location.AttachNode(this);

        // MouseDown (not MouseClick) reliably fires for the right button here.
        AddEvent(AtkEventType.MouseDown, (_, _, _, _, eventData) =>
        {
            if (eventData is not null && eventData->MouseData.ButtonId == 1 && ItemData.Check is not null)
                OnItemRightClick?.Invoke(ItemData.Check.ItemId);
        });
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged(); // keeps the native hover/select highlight sized correctly
        Layout();
    }

    private void Layout()
    {
        var indent = _depth * IndentW;
        var caretX = 4f + indent;
        var iconX  = caretX + CaretW + 2f;
        var nameX  = iconX + IconSz + Gap;

        var iconY  = Math.Max(0f, (Height - IconSz) / 2f);
        var textY  = Math.Max(0f, (Height - 14f) / 2f);
        var nameW  = Math.Max(0f, Width - nameX - QtyW - LocW - 4f);

        _caret.Position = new Vector2(caretX, textY);
        _caret.Size     = new Vector2(CaretW, 14f);

        _icon.Position  = new Vector2(iconX, iconY);

        _name.Position  = new Vector2(nameX, textY);
        _name.Size      = new Vector2(nameW, 14f);

        _qty.Position   = new Vector2(nameX + nameW, textY + 1f);
        _qty.Size       = new Vector2(QtyW, 13f);

        _location.Position = new Vector2(nameX + nameW + QtyW, textY + 1f);
        _location.Size     = new Vector2(LocW, 13f);
    }

    protected override void SetNodeData(IngredientRow data)
    {
        var ing = data.Check;

        _depth = data.Depth;
        _caret.IsVisible = data.Craftable;
        _caret.String    = data.Expanded ? "-" : "+";

        _icon.IconId = ing.IconId;
        _name.String = ing.Name;
        _qty.String  = $"{ing.Available}/{ing.Required}";

        Vector4 statusColor;
        string  locText;

        if (ing.Missing > 0)
        {
            statusColor = ColorMissing;
            locText     = string.IsNullOrEmpty(data.PrimaryLocation) ? CraftLoc.Get("loc.notfound") : data.PrimaryLocation;
        }
        else if (ing.InPlayer >= ing.Required)
        {
            statusColor = ColorFulfilled;
            locText     = CraftLoc.Get("loc.mainbags");
        }
        else
        {
            statusColor = ColorElsewhere;
            locText     = string.IsNullOrEmpty(data.PrimaryLocation) ? CraftLoc.Get("loc.otherinv") : data.PrimaryLocation;
        }

        _name.TextColor     = statusColor;
        _qty.TextColor      = statusColor;
        _location.String    = locText;
        _location.TextColor = ColorSubtitle;

        // Re-run layout since the indent depends on the row's data.
        Layout();

        // Collision list is rebuilt by the addon after repopulation — see RecipeRowNode.
        ItemTooltip = ing.ItemId;
    }
}
