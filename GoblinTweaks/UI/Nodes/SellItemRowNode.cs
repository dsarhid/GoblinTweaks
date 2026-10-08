using System.Numerics;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// One sellable stack: checkbox, game icon, rarity-colored name, quantity and item level.
/// The native item tooltip shows on hover.
/// </summary>
internal sealed class SellItemRowNode : ListItemNode<SellableItem>, IListItemNode
{
    public static float ItemHeight => 34f;

    /// <summary>Set by the addon: whether a stack is selected, and the callback when the checkbox changes.</summary>
    internal static Func<string, bool>? IsSelected;
    internal static Action<string, bool>? SelectionChanged;

    /// <summary>Set by the addon: Universalis normal price per unit (null while loading, 0 when unknown) and its tooltip.</summary>
    internal static Func<SellableItem, long?>? PriceOf;
    internal static string PriceTooltip = string.Empty;


    /// <summary>Set by the addon: units to sell of a stack (the whole stack by default) and the callback when it is edited.</summary>
    internal static Func<SellableItem, int>? SellQuantity;
    internal static Action<string, int>? SellQuantityChanged;

    private static readonly Vector4 ColorMuted = new(0.60f, 0.60f, 0.60f, 1f);

    private static readonly string HighQuality = SeIconChar.HighQuality.ToIconString();

    private const float CheckW = 24f;
    private const float BoxSz  = 16f;
    private const float IconSz = 26f;
    internal const float QtyW   = 54f;
    internal const float SellW  = 78f;
    internal const float LevelW = 70f;
    internal const float PriceW = 96f;

    private readonly ColorImageNode _boxFrame;
    private readonly ColorImageNode _boxFill;
    private readonly IconImageNode _icon;
    private readonly TextNode      _name;
    private readonly TextNode      _qty;
    private readonly TextNode      _level;
    private readonly TextNode      _price;
    private readonly CollisionNode _priceHit;
    private readonly NumericInputNode _sell;
    private readonly CollisionNode _hit;

    /// <summary>Rows currently alive, so "select all" can redraw their boxes. Cleared by the addon when it closes.</summary>
    internal static readonly List<SellItemRowNode> Live = [];

    /// <summary>Redraws every row's box from the current selection.</summary>
    internal static void RefreshAll()
    {
        foreach (var row in Live) row.RefreshSelected();
    }

    private void RefreshSelected()
    {
        if (_key is null) return;

        ShowSelected(IsSelected?.Invoke(_key) ?? false);

        var price = PriceOf?.Invoke(ItemData);
        _price.String = price is null ? "..." : price > 0 ? $"{price:N0}" : "-";
    }

    private string? _key;
    private bool    _binding; // setting the input value fires its callback; ignore it while binding data

    public unsafe SellItemRowNode()
    {
        // A plain drawn box instead of a native checkbox: the whole row handles the click, so there is
        // exactly one source of truth (the addon's selection set) and no double toggling.
        Live.Add(this);

        _boxFrame = new ColorImageNode { Size = new Vector2(BoxSz, BoxSz), Color = new Vector4(0.80f, 0.72f, 0.50f, 1f) };
        _boxFrame.AttachNode(this);
        _boxFill = new ColorImageNode { Size = new Vector2(BoxSz - 4f, BoxSz - 4f), Color = new Vector4(0.10f, 0.09f, 0.08f, 1f) };
        _boxFill.AttachNode(this);

        _icon = new IconImageNode { FitTexture = true, Size = new Vector2(IconSz, IconSz) };
        _icon.AttachNode(this);

        _name = new TextNode { FontSize = 13 };
        _name.AddTextFlags(TextFlags.Ellipsis);
        _name.AttachNode(this);

        _qty = new TextNode { FontSize = 12, TextColor = ColorMuted, AlignmentType = AlignmentType.Right };
        _qty.AttachNode(this);

        _level = new TextNode { FontSize = 12, TextColor = ColorMuted, AlignmentType = AlignmentType.Right };
        _level.AttachNode(this);

        _price = new TextNode { FontSize = 12, TextColor = new Vector4(0.40f, 0.88f, 0.46f, 1f), AlignmentType = AlignmentType.Right };
        _price.AttachNode(this);
        _priceHit = new CollisionNode { CollisionType = CollisionType.Hit };
        _priceHit.AttachNode(this);

        _sell = new NumericInputNode { Min = 1, Max = 1, Value = 1, Size = new Vector2(SellW - 6f, 24f) };
        _sell.OnValueUpdate = value =>
        {
            if (_binding || _key is null) return;
            SellQuantityChanged?.Invoke(_key, value);
        };
        _sell.AttachNode(this);

        // Only the left part of the row (box, icon, name) toggles the selection, so clicking the
        // quantity field does not. It carries the item tooltip too, since it sits above the row.
        _hit = new CollisionNode { CollisionType = CollisionType.Hit };
        _hit.AddEvent(AtkEventType.MouseDown, (_, _, _, _, eventData) =>
        {
            if (eventData is null || eventData->MouseData.ButtonId != 0 || _key is null) return;

            var on = !(IsSelected?.Invoke(_key) ?? false);
            SelectionChanged?.Invoke(_key, on);
            ShowSelected(on);
        });
        _hit.AttachNode(this);
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        var textY = Math.Max(0f, (Height - 16f) / 2f);
        var iconX = 4f + CheckW + 4f;
        var nameX = iconX + IconSz + 6f;
        var nameW = Math.Max(0f, Width - nameX - QtyW - SellW - LevelW - PriceW - 8f);

        var boxY = Math.Max(0f, (Height - BoxSz) / 2f);
        _boxFrame.Position = new Vector2(6f, boxY);
        _boxFill.Position  = new Vector2(8f, boxY + 2f);
        _icon.Position  = new Vector2(iconX, Math.Max(0f, (Height - IconSz) / 2f));
        _name.Position  = new Vector2(nameX, textY);
        _name.Size      = new Vector2(nameW, 16f);
        _qty.Position   = new Vector2(nameX + nameW, textY);
        _qty.Size       = new Vector2(QtyW, 16f);
        _sell.Position  = new Vector2(nameX + nameW + QtyW + 6f, Math.Max(0f, (Height - 24f) / 2f));
        _level.Position = new Vector2(nameX + nameW + QtyW + SellW, textY);
        _level.Size     = new Vector2(LevelW, 16f);

        _price.Position    = new Vector2(nameX + nameW + QtyW + SellW + LevelW, textY);
        _price.Size        = new Vector2(PriceW - 6f, 16f);
        _priceHit.Position = new Vector2(nameX + nameW + QtyW + SellW + LevelW, 0f);
        _priceHit.Size     = new Vector2(PriceW, Height);

        _hit.Position = Vector2.Zero;
        _hit.Size     = new Vector2(nameX + nameW, Height);
    }

    protected override void SetNodeData(SellableItem data)
    {
        _key = data.Key;

        ShowSelected(IsSelected?.Invoke(data.Key) ?? false);

        _icon.IconId = data.IconId;
        _name.String = data.Hq ? $"{data.Name} {HighQuality}" : data.Name;
        _name.TextColor = RarityColor(data.Rarity);
        _qty.String   = data.Quantity.ToString();
        _level.String = data.ItemLevel > 1 ? data.ItemLevel.ToString() : string.Empty;

        var price = PriceOf?.Invoke(data);
        _price.String = price is null ? "..." : price > 0 ? $"{price:N0}" : "-";
        _priceHit.TextTooltip = PriceTooltip;

        _binding = true;
        _sell.Max   = Math.Max(1, data.Quantity);
        _sell.Value = Math.Clamp(SellQuantity?.Invoke(data) ?? data.Quantity, 1, Math.Max(1, data.Quantity));
        _binding = false;

        // HQ items open the HQ version of the native tooltip (HQ ids are offset by 1,000,000).
        var tooltip = data.Hq ? data.ItemId + 1_000_000 : data.ItemId;
        ItemTooltip      = tooltip;
        _hit.ItemTooltip = tooltip;
    }

    private void ShowSelected(bool on)
        => _boxFill.Color = on ? new Vector4(0.95f, 0.78f, 0.25f, 1f) : new Vector4(0.10f, 0.09f, 0.08f, 1f);

    /// <summary>The same colors the game uses for item names by rarity.</summary>
    public static Vector4 RarityColor(byte rarity) => rarity switch
    {
        2 => new Vector4(0.45f, 0.88f, 0.45f, 1f), // green
        3 => new Vector4(0.42f, 0.66f, 1.00f, 1f), // blue
        4 => new Vector4(0.74f, 0.50f, 0.97f, 1f), // purple
        7 => new Vector4(1.00f, 0.56f, 0.82f, 1f), // aetherial pink
        _ => new Vector4(0.93f, 0.93f, 0.93f, 1f), // white
    };
}
