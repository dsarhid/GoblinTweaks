using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Classes;
using KamiToolKit.Extensions;
using KamiToolKit.Nodes;

namespace GoblinTweaks.Native;

/// <summary>An image node that draws a texture loaded by Dalamud (a PNG of the plugin) instead of one of the game's.</summary>
internal sealed unsafe class WrapImageNode : ImageNode
{
    public WrapImageNode(IDalamudTextureWrap texture)
    {
        TextureWidth  = texture.Width;
        TextureHeight = texture.Height;

        AddPart(new Part { Id = 0, U = 0, V = 0, Width = (ushort)texture.Width, Height = (ushort)texture.Height });
        PartId = 0;

        // Drawn at the size of the node: left alone it would be drawn at the size of the picture and run out of it.
        FitTexture = true;

        var part = Node->PartsList->Parts;
        part->LoadTexture(texture);
    }

    public int TextureWidth { get; }

    public int TextureHeight { get; }
}
