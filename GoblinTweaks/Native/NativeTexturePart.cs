using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GoblinTweaks.Native;

/// <summary>
/// A parts list of one part whose texture the plugin draws itself (square RGBA pixels), for native nodes to share.
/// Must be created and disposed on the framework thread, and disposed only once no node uses it.
/// </summary>
public sealed unsafe class NativeTexturePart : IDisposable
{
    private NativeTexturePart(AtkUldPartsList* partsList) => PartsList = partsList;

    public AtkUldPartsList* PartsList { get; private set; }

    /// <summary>A texture of <paramref name="size"/>×<paramref name="size"/> pixels; the part covers all of it.</summary>
    public static NativeTexturePart? Create(int size, byte[] rgba, string name)
    {
        var wrap = Svc.Textures.CreateFromRaw(RawImageSpecification.Rgba32(size, size), rgba, name);
        var kernel = (Texture*)Svc.Textures.ConvertToKernelTexture(wrap, leaveWrapOpen: false);
        if (kernel == null)
            return null;

        var space     = IMemorySpace.GetUISpace();
        var partsList = (AtkUldPartsList*)space->Malloc((ulong)sizeof(AtkUldPartsList), 8);
        var part      = (AtkUldPart*)space->Malloc((ulong)sizeof(AtkUldPart), 8);
        var asset     = (AtkUldAsset*)space->Malloc((ulong)sizeof(AtkUldAsset), 8);

        if (partsList == null || part == null || asset == null)
        {
            if (asset != null) IMemorySpace.Free(asset, (ulong)sizeof(AtkUldAsset));
            if (part != null) IMemorySpace.Free(part, (ulong)sizeof(AtkUldPart));
            if (partsList != null) IMemorySpace.Free(partsList, (ulong)sizeof(AtkUldPartsList));
            kernel->DecRef();
            Svc.Log.Error("Could not allocate memory for a texture part");
            return null;
        }

        asset->Id = 0;
        asset->AtkTexture.Ctor();
        asset->AtkTexture.KernelTexture = kernel;
        asset->AtkTexture.TextureType   = TextureType.KernelTexture;

        part->UldAsset = asset;
        part->U        = 0;
        part->V        = 0;
        part->Width    = (ushort)size;
        part->Height   = (ushort)size;

        partsList->Id        = 0;
        partsList->PartCount = 1;
        partsList->Parts     = part;

        return new NativeTexturePart(partsList);
    }

    public void Dispose()
    {
        if (PartsList == null)
            return;

        var part = PartsList->Parts;
        if (part != null)
        {
            if (part->UldAsset != null)
            {
                part->UldAsset->AtkTexture.ReleaseTexture();
                IMemorySpace.Free(part->UldAsset, (ulong)sizeof(AtkUldAsset));
            }

            IMemorySpace.Free(part, (ulong)sizeof(AtkUldPart));
        }

        IMemorySpace.Free(PartsList, (ulong)sizeof(AtkUldPartsList));
        PartsList = null;
    }
}
