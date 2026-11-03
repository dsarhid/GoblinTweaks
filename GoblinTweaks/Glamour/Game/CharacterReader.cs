using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace GoblinTweaks.Glamour.Game;

/// <summary>
/// Who the outfits are for. <c>Race</c> 1..8 as in the game; <c>RaceName</c> / <c>JobName</c> in English (for the AI).
/// </summary>
internal sealed record CharacterInfo(
    CharacterPalette Palette, int Race, bool Male, uint JobId, string RaceName, string JobName, bool HasOffHand);

/// <summary>
/// Reads the player's appearance colors from the character's customize data and the game's color table
/// (<c>chara/xls/charamake/human.cmp</c>, laid out as in Penumbra.GameData's CmpData; the "interface" colors are
/// the ones the character creator shows, which match the rendered look best). Framework thread only.
/// </summary>
internal static unsafe class CharacterReader
{
    // human.cmp layout, in bytes. ColorParameters (9216 bytes): Eyes 0, HairHighlights 1024, LipsDark 2048,
    // FacePaintDark 2560, Features 3072, LipsLight 4096, FacePaintLight 4608. Then 32 race/gender blocks of 5120:
    // Skin 0, Hair 1024 (pairs), SkinInterface 3072, HairInterface 4096. Every color is 4 bytes R, G, B, A.
    private const int Interface = 9216, Races = 18432, RaceBlock = 5120;
    private const int Eyes = 0, Highlights = 1024, LipsDark = 2048, PaintDark = 2560, LipsLight = 4096, PaintLight = 4608;
    private const int SkinInterface = 3072, HairInterface = 4096;

    private const byte Hrothgar = 7;

    private static byte[]? _cmp;

    public static CharacterInfo? Read()
    {
        var chara = (Character*)Control.GetLocalPlayer();
        if (chara == null)
            return null;

        _cmp ??= Svc.Data.GetFile("chara/xls/charamake/human.cmp")?.Data;
        if (_cmp is not { Length: > Races + 32 * RaceBlock } cmp)
            return null;

        var c = chara->DrawData.CustomizeData;
        if (c.Race is < 1 or > 8 || c.Tribe is < 1 or > 16)
            return null;

        var raceBase = Races + ((c.Tribe - 1) * 2 + (c.Sex == 1 ? 1 : 0)) * RaceBlock;
        var skin = At(cmp, raceBase + SkinInterface, c.SkinColor);
        var hair = At(cmp, raceBase + HairInterface, c.HairColor);
        var palette = new CharacterPalette(
            Skin: skin,
            Hair: hair,
            Highlights: c.Highlights ? At(cmp, Interface + Highlights, c.HighlightsColor) : hair,
            LeftEye: At(cmp, Interface + Eyes, c.EyeColorLeft),
            RightEye: At(cmp, Interface + Eyes, c.EyeColorRight),
            Lips: c.Race != Hrothgar && c.Lipstick ? Toned(cmp, LipsDark, LipsLight, c.LipColorFurPattern) : skin,
            FacePaint: c.FacePaint != 0 ? Toned(cmp, PaintDark, PaintLight, c.FacePaintColor) : null);

        var jobId = Svc.PlayerState.ClassJob.RowId;
        var english = Svc.Data.GetExcelSheet<ClassJob>(ClientLanguage.English);
        var jobName = english.TryGetRow(jobId, out var job) ? job.Name.ExtractText() : $"job {jobId}";
        var races = Svc.Data.GetExcelSheet<Race>(ClientLanguage.English);
        var raceName = races.TryGetRow(c.Race, out var race) ? (c.Sex == 1 ? race.Feminine : race.Masculine).ExtractText() : $"race {c.Race}";

        return new CharacterInfo(palette, c.Race, c.Sex == 0, jobId, raceName, jobName, HasOffHand(jobId));
    }

    /// <summary>Paladins (shield) and crafters/gatherers (secondary tool) use the off hand; everyone else does not.</summary>
    private static bool HasOffHand(uint jobId)
    {
        if (jobId is 1 or 19 or (>= 8 and <= 17))
            return true;
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return false;
        var equipped = inventory->GetInventoryContainer(InventoryType.EquippedItems);
        var offHand = equipped == null ? null : equipped->GetInventorySlot(1);
        return offHand != null && offHand->ItemId != 0;
    }

    private static Rgb At(byte[] cmp, int offset, int index)
    {
        var o = offset + index * 4;
        return o + 2 < cmp.Length ? new Rgb(cmp[o], cmp[o + 1], cmp[o + 2]) : new Rgb(128, 128, 128);
    }

    /// <summary>Lip and face paint colors: values from 128 up are the light variants.</summary>
    private static Rgb Toned(byte[] cmp, int dark, int light, byte value)
        => value < 128 ? At(cmp, dark, value) : At(cmp, light, value - 128);
}
