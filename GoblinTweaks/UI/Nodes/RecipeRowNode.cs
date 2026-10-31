using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Interfaces;
using KamiToolKit.Nodes;
using GoblinTweaks.Tweaks;

namespace GoblinTweaks.UI.Nodes;

/// <summary>
/// One row in the recipe virtual-scroll list. Two lines: name (colored by craft status) and a
/// "Lv N · Class" subtitle. A green ✓ on the right marks recipes already crafted.
/// </summary>
internal sealed class RecipeRowNode : ListItemNode<CraftableEntry>, IListItemNode
{
    public static float ItemHeight => 38f;

    /// <summary>Set by the addon; invoked with the full entry on right-click.</summary>
    internal static Action<CraftableEntry>? OnItemRightClick;

    /// <summary>Set by the addon: whether a recipe is in My List.</summary>
    internal static Func<uint, bool>? IsInCart;
    /// <summary>Set by the addon; invoked with the recipe id when its My List box is clicked.</summary>
    internal static Action<uint>? OnToggleCart;

    private static readonly Vector4 ColorReady     = new(0.30f, 0.85f, 0.40f, 1f);
    private static readonly Vector4 ColorElsewhere = new(0.95f, 0.80f, 0.20f, 1f);
    private static readonly Vector4 ColorNear      = new(0.95f, 0.55f, 0.15f, 1f);
    private static readonly Vector4 ColorSubtitle  = new(157 / 255f, 131 / 255f,  91 / 255f, 1f);
    private static readonly Vector4 ColorMuted     = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 ColorCheck     = new(0.45f, 0.85f, 0.45f, 1f);

    private const float CheckW = 18f;
    private const float BoxSz  = 20f;
    private const float BoxX   = 4f;
    private const float IconSz = 30f;
    private const float IconX  = BoxX + BoxSz + 4f;
    private const float NameX  = IconX + IconSz + 10f;

    private readonly CheckboxNode  _cartBox;  // in My List or not
    private readonly IconImageNode _icon;
    private readonly TextNode _name;
    private readonly TextNode _sub;   // "Lv N · Class"
    private readonly TextNode _check;

    // Set while the controls are brought in line with the list: writing IsChecked raises OnClick.
    private bool _syncing;

    public unsafe RecipeRowNode()
    {
        _cartBox = new CheckboxNode
        {
            String      = string.Empty,
            Size        = new Vector2(BoxSz, BoxSz),
            TextTooltip = CraftLoc.Get("tip.cart"),
        };
        _cartBox.OnClick = _ =>
        {
            if (!_syncing && ItemData is not null)
                OnToggleCart?.Invoke(ItemData.RecipeId);
        };
        _cartBox.AttachNode(this);

        _icon = new IconImageNode { FitTexture = true, Size = new Vector2(IconSz, IconSz) };
        _icon.AttachNode(this);

        _name = new TextNode { FontSize = 13, TextColor = ColorMuted };
        _name.AddTextFlags(TextFlags.Ellipsis);
        _name.AttachNode(this);

        _sub = new TextNode { FontSize = 11, TextColor = ColorSubtitle };
        _sub.AddTextFlags(TextFlags.Ellipsis);
        _sub.AttachNode(this);

        _check = new TextNode
        {
            String        = "✓",
            FontSize      = 13,
            TextColor     = ColorCheck,
            AlignmentType = AlignmentType.Center,
        };
        _check.AttachNode(this);

        // Native right-click → handled by the addon (item menu if owned, else crafting log).
        AddEvent(AtkEventType.MouseDown, (_, _, _, _, eventData) =>
        {
            if (eventData is not null && eventData->MouseData.ButtonId == 1 && ItemData is not null)
                OnItemRightClick?.Invoke(ItemData);
        });
    }

    protected override void OnSizeChanged()
    {
        base.OnSizeChanged();

        var iconY = Math.Max(0f, (Height - IconSz) / 2f);
        var textW = Math.Max(0f, Width - NameX - CheckW - 8f);

        _cartBox.Position = new Vector2(BoxX, Math.Max(0f, (Height - BoxSz) / 2f));
        _icon.Position  = new Vector2(IconX, iconY);

        _name.Position  = new Vector2(NameX, 4f);
        _name.Size      = new Vector2(textW, 16f);

        _sub.Position   = new Vector2(NameX, 21f);
        _sub.Size       = new Vector2(textW, 13f);

        _check.Position = new Vector2(Width - CheckW - 2f, Math.Max(0f, (Height - 14f) / 2f));
        _check.Size     = new Vector2(CheckW, 14f);
    }

    protected override void SetNodeData(CraftableEntry data)
    {
        _icon.IconId    = data.IconId;
        _name.String    = data.Name;
        _name.TextColor = data.Status switch
        {
            CraftStatus.Ready          => ColorReady,
            CraftStatus.ReadyElsewhere => ColorElsewhere,
            CraftStatus.NearComplete   => ColorNear,
            _                          => ColorMuted,
        };

        var lvl   = data.CraftLevel > 0 ? CraftLoc.Fmt("row.level", data.CraftLevel) : string.Empty;
        var cls   = data.CraftClass;
        _sub.String = (lvl, cls) switch
        {
            ("", "")  => string.Empty,
            ("", _)   => cls,
            (_, "")   => lvl,
            _         => $"{lvl}  ·  {cls}",
        };

        _check.IsVisible = data.IsCrafted;

        ItemTooltip = data.ItemId;
        SyncCart(data);
    }

    // The list only calls SetNodeData when a row gets a different recipe; My List changes on its own, so the box is
    // brought in line every frame too.
    public override void Update()
    {
        base.Update();
        if (ItemData is not null) SyncCart(ItemData);
    }

    private void SyncCart(CraftableEntry data)
    {
        var inCart = IsInCart?.Invoke(data.RecipeId) == true;
        if (_cartBox.IsChecked == inCart) return;

        _syncing = true;
        try     { _cartBox.IsChecked = inCart; }
        finally { _syncing = false; }
    }
}
