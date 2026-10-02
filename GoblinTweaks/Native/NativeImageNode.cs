using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.Native;

/// <summary>Describes a rectangle of a game texture and how big it is drawn.</summary>
/// <param name="TexturePath">Path without the "_hr1" suffix, e.g. "ui/uld/Foo.tex".</param>
/// <param name="U">Left, using the values the Addon Inspector shows as "Hi-Res".</param>
/// <param name="V">Top ("Hi-Res").</param>
/// <param name="Width">Part width ("Hi-Res").</param>
/// <param name="Height">Part height ("Hi-Res").</param>
/// <param name="DisplayWidth">Node width on screen.</param>
/// <param name="DisplayHeight">Node height on screen.</param>
public readonly record struct TexturePart(string TexturePath, ushort U, ushort V, ushort Width, ushort Height, ushort DisplayWidth, ushort DisplayHeight);

/// <summary>
/// An image node owned by the plugin and attached to a component of a game window.
/// Must be created and destroyed on the framework thread; the game never frees it for us.
/// </summary>
public sealed unsafe class NativeImageNode : IDisposable
{
    private AtkImageNode* _node;
    private AtkComponentBase* _owner;

    private NativeImageNode(AtkImageNode* node, AtkComponentBase* owner)
    {
        _node = node;
        _owner = owner;
    }

    public bool IsValid => _node != null;

    /// <summary>Creates the node and links it on top of every other node of <paramref name="owner"/>.</summary>
    public static NativeImageNode? Create(AtkComponentBase* owner, uint nodeId, TexturePart part, short x, short y)
    {
        if (owner == null || owner->UldManager.RootNode == null)
            return null;

        var node = Allocate(nodeId, part);
        if (node == null)
            return null;

        var res = &node->AtkResNode;
        res->SetPositionShort(x, y);
        res->ToggleVisibility(false);

        // Insert before the first sibling of the component root: drawn above the rest.
        var first = owner->UldManager.RootNode;
        while (first->PrevSiblingNode != null)
            first = first->PrevSiblingNode;

        first->PrevSiblingNode = res;
        res->NextSiblingNode = first;
        res->ParentNode = first->ParentNode;
        owner->UldManager.UpdateDrawNodeList();

        return new NativeImageNode(node, owner);
    }

    public void SetVisible(bool visible)
    {
        if (_node != null)
            _node->AtkResNode.ToggleVisibility(visible);
    }

    public void Dispose()
    {
        if (_node == null)
            return;

        var res = &_node->AtkResNode;

        if (res->PrevSiblingNode != null)
            res->PrevSiblingNode->NextSiblingNode = res->NextSiblingNode;
        if (res->NextSiblingNode != null)
            res->NextSiblingNode->PrevSiblingNode = res->PrevSiblingNode;
        if (res->ParentNode != null && res->ParentNode->ChildNode == res)
            res->ParentNode->ChildNode = res->NextSiblingNode;

        res->PrevSiblingNode = res->NextSiblingNode = res->ParentNode = null;

        if (_owner != null)
            _owner->UldManager.UpdateDrawNodeList();

        Free(_node);
        _node = null;
        _owner = null;
    }

    private static AtkImageNode* Allocate(uint nodeId, TexturePart part)
    {
        var space = IMemorySpace.GetUISpace();

        var node = space->Create<AtkImageNode>();
        var partsList = (AtkUldPartsList*)space->Malloc((ulong)sizeof(AtkUldPartsList), 8);
        var uldPart = (AtkUldPart*)space->Malloc((ulong)sizeof(AtkUldPart), 8);
        var asset = (AtkUldAsset*)space->Malloc((ulong)sizeof(AtkUldAsset), 8);

        if (node == null || partsList == null || uldPart == null || asset == null)
        {
            if (asset != null) IMemorySpace.Free(asset, (ulong)sizeof(AtkUldAsset));
            if (uldPart != null) IMemorySpace.Free(uldPart, (ulong)sizeof(AtkUldPart));
            if (partsList != null) IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
            if (node != null) FreeNodeOnly(node);
            Svc.Log.Error("Could not allocate memory for an image node");
            return null;
        }

        asset->Id = 0;
        asset->AtkTexture.Ctor();

        uldPart->UldAsset = asset;
        uldPart->U = part.U;
        uldPart->V = part.V;
        uldPart->Width = part.Width;
        uldPart->Height = part.Height;

        partsList->Id = 0;
        partsList->PartCount = 1;
        partsList->Parts = uldPart;

        var res = &node->AtkResNode;
        res->Type = NodeType.Image;
        res->NodeId = nodeId;
        res->NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
        res->DrawFlags = 0;

        // Neutral colors (a freshly created node would otherwise render black).
        res->Color.R = res->Color.G = res->Color.B = res->Color.A = 255;
        res->AddRed = res->AddGreen = res->AddBlue = 0;
        res->MultiplyRed = res->MultiplyGreen = res->MultiplyBlue = 100;

        node->PartsList = partsList;
        node->PartId = 0;
        node->WrapMode = 2; // stretch the part to the node size

        // Same scale the game uses for its own windows (normal or high resolution UI).
        node->LoadTexture(part.TexturePath, AtkStage.Instance()->AtkTextureResourceManager->DefaultTextureScale);

        res->SetWidth(part.DisplayWidth);
        res->SetHeight(part.DisplayHeight);

        return node;
    }

    private static void Free(AtkImageNode* node)
    {
        var partsList = node->PartsList;
        node->PartsList = null;

        if (partsList != null)
        {
            var uldPart = partsList->Parts;
            if (uldPart != null)
            {
                if (uldPart->UldAsset != null)
                {
                    uldPart->UldAsset->AtkTexture.ReleaseTexture();
                    IMemorySpace.Free(uldPart->UldAsset, (ulong)sizeof(AtkUldAsset));
                }

                IMemorySpace.Free(uldPart, (ulong)sizeof(AtkUldPart));
            }

            IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
        }

        FreeNodeOnly(node);
    }

    private static void FreeNodeOnly(AtkImageNode* node)
    {
        node->AtkResNode.Destroy(false);
        IMemorySpace.Free(node, (ulong)sizeof(AtkImageNode));
    }
}
