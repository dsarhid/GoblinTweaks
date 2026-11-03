using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace GoblinTweaks.Glamour.Game;

/// <summary>
/// Shows an outfit in the game's Try On window and captures its character preview as a JPEG (for the AI).
/// <see cref="ShowAsync"/> may be called from any thread; it does its game work on the framework thread.
/// </summary>
internal static class TryOnPreview
{
    private static readonly Guid JpegContainer = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    private const int R8G8B8A8 = 28;

    /// <summary>Marks an unused entry of the agent's list (seen in game).</summary>
    private const byte EmptySlot = 14;

    /// <summary>Facewear's slot in the agent's list; its entry's Id is a Glasses row, not an item (seen in game).</summary>
    private const byte FacewearSlot = 13;

    /// <summary>
    /// Shows the pieces, with their dyes and facewear, in the game's Try On window, replacing whatever it showed. True
    /// once the window shows them; false if there is no gear piece to open it with or it did not open within about two
    /// seconds.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentTryon.TryOn"/> replaces the whole list with one item on every call, so it is only used to open
    /// the window with the first piece; the game's own entry for it then serves as the template for writing every
    /// piece straight into <see cref="AgentTryon.TryOnItems"/>, which the game redraws when
    /// <see cref="AgentTryon.TryOnItemsChanged"/> is set.
    /// </remarks>
    public static async Task<bool> ShowAsync(IReadOnlyDictionary<GlamourSlot, OutfitPiece> pieces)
    {
        var list = pieces
            .Where(p => p.Value.ItemId != 0)
            .Select(p => (Index: GlamourIds.IsFacewear(p.Value.ItemId) ? FacewearSlot : SlotIndex(p.Key), Piece: p.Value))
            .OrderBy(p => p.Index)
            .Take(14)
            .ToList();
        if (list.Count == 0 || list[0].Index == FacewearSlot)
            return false;

        var first = list[0].Piece;
        var opened = await Svc.Framework.RunOnFrameworkThread(() => Open(first)).ConfigureAwait(false);
        if (!opened)
            return false;
        for (var tick = 0; tick < 120; tick++)
            if (await Svc.Framework.RunOnTick(() => Write(list), delayTicks: 1).ConfigureAwait(false))
                return true;
        return false;
    }

    /// <summary>Opens the Try On with one piece unless it is already open with a usable entry. Framework thread.</summary>
    private static unsafe bool Open(OutfitPiece first)
    {
        var agent = AgentTryon.Instance();
        if (agent != null && agent->AgentInterface.IsAgentActive() && agent->TryOnItems[0].Id != 0)
            return true;
        return AgentTryon.TryOn(0, first.ItemId, first.Stain0, first.Stain1);
    }

    /// <summary>Writes the pieces over the agent's list once the window holds a game-made entry. Framework thread.</summary>
    private static unsafe bool Write(List<(byte Index, OutfitPiece Piece)> list)
    {
        var agent = AgentTryon.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive() || agent->TryOnItems[0].Id == 0)
            return false;

        var items = Svc.Data.GetExcelSheet<Item>();
        var glasses = Svc.Data.GetExcelSheet<Glasses>();
        var template = agent->TryOnItems[0];
        for (var i = 0; i < agent->TryOnItems.Length; i++)
        {
            ref var entry = ref agent->TryOnItems[i];
            if (i < list.Count && list[i].Index == FacewearSlot
                && glasses.GetRowOrDefault(list[i].Piece.ItemId - GlamourIds.FacewearBase) is { } face)
            {
                var worn = template;
                worn.Id = face.RowId; worn.GlamourId = 0; worn.IconId = (uint)face.Icon;
                worn.EquipSlotCategory = FacewearSlot; worn.GlamourEquipSlotCategory = 0; worn.ClassJobId = 0;
                worn.Stain0Id = 0; worn.Stain1Id = 0; worn.GlamourStain0Id = 0; worn.GlamourStain1Id = 0;
                worn.HasStain0 = false; worn.HasStain1 = false;
                entry = worn;
                continue;
            }
            if (i >= list.Count || items.GetRowOrDefault(list[i].Piece.ItemId) is not { } row)
            {
                entry.Id = 0; entry.GlamourId = 0; entry.IconId = 0;
                entry.Stain0Id = 0; entry.Stain1Id = 0; entry.GlamourStain0Id = 0; entry.GlamourStain1Id = 0;
                entry.HasStain0 = false; entry.HasStain1 = false;
                entry.EquipSlotCategory = EmptySlot;
                continue;
            }
            var piece = list[i].Piece;
            var next = template;
            next.Id = piece.ItemId; next.GlamourId = 0; next.IconId = row.Icon;
            next.EquipSlotCategory = list[i].Index; next.GlamourEquipSlotCategory = 0;
            next.Stain0Id = row.DyeCount >= 1 ? piece.Stain0 : (byte)0;
            next.Stain1Id = row.DyeCount >= 2 ? piece.Stain1 : (byte)0;
            next.GlamourStain0Id = 0; next.GlamourStain1Id = 0;
            next.HasStain0 = row.DyeCount >= 1; next.HasStain1 = row.DyeCount >= 2;
            entry = next;
        }
        agent->TryOnItemsChanged = true;
        return true;
    }

    /// <summary>The equipment slot index the Try On list uses (not the EquipSlotCategory row).</summary>
    private static byte SlotIndex(GlamourSlot slot) => slot switch
    {
        GlamourSlot.MainHand => 0, GlamourSlot.OffHand => 1, GlamourSlot.Head => 2, GlamourSlot.Body => 3,
        GlamourSlot.Hands => 4, GlamourSlot.Legs => 6, GlamourSlot.Feet => 7, GlamourSlot.Ears => 8,
        GlamourSlot.Neck => 9, GlamourSlot.Wrists => 10, _ => 11,
    };

    /// <summary>
    /// The Try On character preview as a JPEG whose longest side is at most <paramref name="maxSide"/>, or null when
    /// the window is not open or it could not be read. Reads the preview texture itself; if that fails, the part of
    /// the screen the window covers. Call on the framework thread; finishes off it.
    /// </summary>
    public static async Task<byte[]?> CaptureJpegAsync(int maxSide = 768)
    {
        try
        {
            if (PreviewTexture() is { } preview)
                return await ToJpeg(preview, maxSide, leaveOpen: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, "GoblinGlamour: Try On texture unreadable; capturing the screen instead.");
        }

        try
        {
            if (WindowBounds() is not { } bounds)
                return null;
            var viewport = await Svc.Textures.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
            {
                ViewportId = bounds.Viewport, TakeBeforeImGuiRender = true, Uv0 = bounds.Uv0, Uv1 = bounds.Uv1,
            }).ConfigureAwait(false);
            return await ToJpeg(viewport, maxSide, leaveOpen: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "GoblinGlamour: could not capture the Try On preview.");
            return null;
        }
    }

    private static async Task<byte[]> ToJpeg(IDalamudTextureWrap source, int maxSide, bool leaveOpen)
    {
        var scale = Math.Min(1f, (float)maxSide / Math.Max(source.Width, source.Height));
        using var resized = await Svc.Textures.CreateFromExistingTextureAsync(source, new TextureModificationArgs
        {
            NewWidth = Math.Max(1, (int)(source.Width * scale)),
            NewHeight = Math.Max(1, (int)(source.Height * scale)),
            DxgiFormat = R8G8B8A8,
            MakeOpaque = true,
        }, leaveWrapOpen: leaveOpen).ConfigureAwait(false);
        using var stream = new MemoryStream();
        await Svc.TextureReadback.SaveToStreamAsync(resized, JpegContainer, stream, leaveWrapOpen: true, leaveStreamOpen: true).ConfigureAwait(false);
        return stream.ToArray();
    }

    /// <summary>The Try On's own render target (the character preview), wrapped without taking ownership.</summary>
    private static unsafe PreviewWrap? PreviewTexture()
    {
        var agent = AgentTryon.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive())
            return null;
        var texture = agent->Texture;
        if (texture == null || texture->D3D11ShaderResourceView == null || texture->ActualWidth == 0 || texture->ActualHeight == 0)
            return null;
        return new PreviewWrap((nint)texture->D3D11ShaderResourceView, (int)texture->ActualWidth, (int)texture->ActualHeight);
    }

    private static unsafe (uint Viewport, Vector2 Uv0, Vector2 Uv1)? WindowBounds()
    {
        var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("Tryon").Address;
        if (addon == null || !addon->IsVisible || addon->RootNode == null)
            return null;
        var viewport = ImGui.GetMainViewport();
        var size = viewport.Size;
        var x = (float)addon->X;
        var y = (float)addon->Y;
        var w = addon->RootNode->Width * addon->Scale;
        var h = addon->RootNode->Height * addon->Scale;
        return (viewport.ID, new Vector2(x / size.X, y / size.Y), new Vector2((x + w) / size.X, (y + h) / size.Y));
    }

    /// <summary>A texture the game owns, seen through Dalamud's texture interface. Disposing it does nothing.</summary>
    private sealed class PreviewWrap(nint shaderResourceView, int width, int height) : IDalamudTextureWrap
    {
        public ImTextureID Handle => new(shaderResourceView);
        public int Width => width;
        public int Height => height;
        public void Dispose() { }
    }
}
