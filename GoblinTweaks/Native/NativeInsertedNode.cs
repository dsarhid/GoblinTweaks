using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.Native;

/// <summary>
/// A clipping mask or an image of the plugin, linked right after a node of a game component, among its siblings. No
/// game node is moved: only the two neighbours of the new node point to it. <see cref="Unlink"/> takes it out again;
/// <see cref="Free"/> then releases it, once the draw list no longer refers to it.
/// </summary>
/// <remarks>
/// A clipping mask clips the siblings that come before it (towards the first child), so a mask placed after the first
/// few siblings clips exactly those. The node is created and freed on the framework thread; its texture is borrowed
/// and must outlive it.
/// </remarks>
public sealed unsafe class NativeInsertedNode
{
    private AtkResNode* _node;
    private readonly ulong _size;
    private bool _linked;

    private NativeInsertedNode(AtkResNode* node, ulong size)
    {
        _node = node;
        _size = size;
    }

    /// <summary>The node it hangs from (the component node, for the first nodes of a component).</summary>
    public AtkResNode* Parent => _node->ParentNode;

    /// <summary>The node itself, to link another one right after it.</summary>
    public AtkResNode* Node => _node;

    /// <summary>A clipping mask with the texture of <paramref name="parts"/>, placed right after <paramref name="previous"/>.</summary>
    public static NativeInsertedNode? Mask(AtkResNode* previous, AtkUldPartsList* parts, uint nodeId)
    {
        var node = IMemorySpace.GetUISpace()->Create<AtkClippingMaskNode>();
        if (node == null)
            return null;

        Init((AtkResNode*)node, NodeType.ClippingMask, nodeId);
        node->PartsList = parts;
        node->PartId    = 0;

        var inserted = new NativeInsertedNode((AtkResNode*)node, (ulong)sizeof(AtkClippingMaskNode));
        inserted.LinkAfter(previous);
        return inserted;
    }

    /// <summary>An image with the texture of <paramref name="parts"/>, placed right after <paramref name="previous"/>.</summary>
    public static NativeInsertedNode? Image(AtkResNode* previous, AtkUldPartsList* parts, uint nodeId)
    {
        var node = IMemorySpace.GetUISpace()->Create<AtkImageNode>();
        if (node == null)
            return null;

        Init((AtkResNode*)node, NodeType.Image, nodeId);
        node->PartsList = parts;
        node->PartId    = 0;
        node->WrapMode  = 2; // stretch the part to the node size

        var inserted = new NativeInsertedNode((AtkResNode*)node, (ulong)sizeof(AtkImageNode));
        inserted.LinkAfter(previous);
        return inserted;
    }

    private void LinkAfter(AtkResNode* previous)
    {
        var node = _node;
        var after = previous->PrevSiblingNode;

        node->ParentNode      = previous->ParentNode;
        node->NextSiblingNode = previous;
        node->PrevSiblingNode = after;

        previous->PrevSiblingNode = node;
        // Some nodes point back to their own last descendant instead of to their sibling: left as they are.
        if (after != null && after->NextSiblingNode == previous)
            after->NextSiblingNode = node;

        // A component node does not count the first nodes of its component as children.
        if (node->ParentNode != null && (ushort)node->ParentNode->Type < 1000)
            node->ParentNode->ChildCount++;

        _linked = true;
    }

    /// <summary>Takes the node out, joining its neighbours again. The draw list must be updated before <see cref="Free"/>.</summary>
    public void Unlink()
    {
        if (!_linked)
            return;

        var node = _node;
        var before = node->NextSiblingNode;
        var after = node->PrevSiblingNode;

        if (before != null && before->PrevSiblingNode == node)
            before->PrevSiblingNode = after;
        if (after != null && after->NextSiblingNode == node)
            after->NextSiblingNode = before;

        if (node->ParentNode != null && (ushort)node->ParentNode->Type < 1000 && node->ParentNode->ChildCount > 0)
            node->ParentNode->ChildCount--;

        node->ParentNode = node->PrevSiblingNode = node->NextSiblingNode = null;
        _linked = false;
    }

    /// <summary>Releases the node. Only after <see cref="Unlink"/> and an update of the draw list.</summary>
    public void Free()
    {
        if (_linked || _node == null)
            return;

        if (_node->Type == NodeType.ClippingMask)
            ((AtkClippingMaskNode*)_node)->PartsList = null;
        else
            ((AtkImageNode*)_node)->PartsList = null;

        _node->Destroy(false);
        IMemorySpace.Free(_node, _size);
        _node = null;
    }

    /// <summary>Places the node in its parent: <paramref name="size"/> pixels of its texture, drawn at <paramref name="scale"/>.</summary>
    public void Place(Vector2 position, ushort size, Vector2 scale)
    {
        const float Epsilon = 0.01f;
        var node = _node;

        if (MathF.Abs(node->X - position.X) > Epsilon || MathF.Abs(node->Y - position.Y) > Epsilon)
            node->SetPositionFloat(position.X, position.Y);
        if (node->Width != size) node->SetWidth(size);
        if (node->Height != size) node->SetHeight(size);
        if (MathF.Abs(node->ScaleX - scale.X) > 0.0001f || MathF.Abs(node->ScaleY - scale.Y) > 0.0001f)
            node->SetScale(scale.X, scale.Y);
    }

    /// <summary>Shows or hides the node, with the opacity given.</summary>
    public void SetShown(bool visible, byte alpha)
    {
        var node = _node;
        if (node->NodeFlags.HasFlag(NodeFlags.Visible) != visible)
            node->ToggleVisibility(visible);
        if (node->Color.A != alpha)
        {
            node->Color.A = alpha;
            node->DrawFlags |= 1;
        }
    }

    /// <summary>Draws an image node with another texture (one part, drawn whole).</summary>
    public void SetParts(AtkUldPartsList* parts)
    {
        if (_node->Type != NodeType.Image || parts == null)
            return;

        var image = (AtkImageNode*)_node;
        if (image->PartsList == parts)
            return;

        image->PartsList = parts;
        image->PartId    = 0;
        _node->DrawFlags |= 1;
    }

    /// <summary>Takes the colors of another node: tint, opacity and the darkening the game gives what can not be used.</summary>
    public void CopyColors(AtkResNode* from)
    {
        var node = _node;
        if (node->Color.RGBA == from->Color.RGBA
            && node->AddRed == from->AddRed && node->AddGreen == from->AddGreen && node->AddBlue == from->AddBlue
            && node->MultiplyRed == from->MultiplyRed && node->MultiplyGreen == from->MultiplyGreen && node->MultiplyBlue == from->MultiplyBlue)
            return;

        node->Color         = from->Color;
        node->AddRed        = from->AddRed;
        node->AddGreen      = from->AddGreen;
        node->AddBlue       = from->AddBlue;
        node->MultiplyRed   = from->MultiplyRed;
        node->MultiplyGreen = from->MultiplyGreen;
        node->MultiplyBlue  = from->MultiplyBlue;
        node->DrawFlags    |= 1;
    }

    private static void Init(AtkResNode* node, NodeType type, uint nodeId)
    {
        node->Type      = type;
        node->NodeId    = nodeId;
        node->NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
        node->DrawFlags = 0;

        // Neutral colors (a freshly created node would otherwise render black).
        node->Color.R = node->Color.G = node->Color.B = node->Color.A = 255;
        node->AddRed = node->AddGreen = node->AddBlue = 0;
        node->MultiplyRed = node->MultiplyGreen = node->MultiplyBlue = 100;

        node->SetPositionFloat(0f, 0f);
        node->SetScale(1f, 1f);
    }
}
