using System.Globalization;

namespace GoblinTweaks.Glamour;

// Plain data shared by the Goblin Glamour engine, the AI protocol and the game side. No game types here:
// these files are also compiled into the test project.

/// <summary>An sRGB color.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses "RRGGBB" or "#RRGGBB".</summary>
    public static Rgb FromHex(string hex)
    {
        var value = uint.Parse(hex.AsSpan().TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>"RRGGBB", uppercase, no '#'.</summary>
    public string ToHex() => $"{R:X2}{G:X2}{B:X2}";
}

/// <summary>The slots an outfit can fill.</summary>
public enum GlamourSlot { Head, Body, Hands, Legs, Feet, MainHand, OffHand, Ears, Neck, Wrists, Ring, Facewear }

public static class GlamourIds
{
    /// <summary>
    /// Facewear is not an item but a Glasses row: its <see cref="CatalogItem.Id"/> / <see cref="OutfitPiece.ItemId"/>
    /// is this base plus the row id, so it never collides with an item id (HQ items go up to 1,999,999).
    /// </summary>
    public const uint FacewearBase = 2_000_000;

    public static bool IsFacewear(uint id) => id >= FacewearBase;
}

/// <summary>One bit per <see cref="GlamourSlot"/>.</summary>
[Flags]
public enum SlotMask
{
    None     = 0,
    Head     = 1 << GlamourSlot.Head,
    Body     = 1 << GlamourSlot.Body,
    Hands    = 1 << GlamourSlot.Hands,
    Legs     = 1 << GlamourSlot.Legs,
    Feet     = 1 << GlamourSlot.Feet,
    MainHand = 1 << GlamourSlot.MainHand,
    OffHand  = 1 << GlamourSlot.OffHand,
    Ears     = 1 << GlamourSlot.Ears,
    Neck     = 1 << GlamourSlot.Neck,
    Wrists   = 1 << GlamourSlot.Wrists,
    Ring     = 1 << GlamourSlot.Ring,
    Facewear = 1 << GlamourSlot.Facewear,

    Armor       = Head | Body | Hands | Legs | Feet,
    Weapons     = MainHand | OffHand,
    Accessories = Ears | Neck | Wrists | Ring,
}

public static class SlotMaskExtensions
{
    public static SlotMask Mask(this GlamourSlot slot) => (SlotMask)(1 << (int)slot);

    public static bool Has(this SlotMask mask, GlamourSlot slot) => (mask & slot.Mask()) != 0;
}

/// <summary>Which kind of job a piece was made for (from its ClassJobCategory); used as a light style hint.</summary>
public enum StyleFamily { Any, Caster, Healer, Tank, Melee, Ranged, Crafter, Gatherer }

/// <summary>
/// An item that can be proposed. <c>Name</c> is in the client language, <c>NameEn</c> in English (themes and
/// the AI work on English names). <c>Blocks</c> = slots this piece hides or occupies (a robe that covers the legs).
/// <c>ModelKey</c> identifies the look: items that share a model look the same and count once.
/// <c>Primary</c>/<c>Secondary</c> = the dominant colors of its icon. <c>SetKey</c> = the model shared by the
/// armor pieces of one set (head to feet), 0 when unknown; pieces with the same key were designed together.
/// <c>Shapes</c> = what kind of piece it is, read from its English name (none when the name does not say).
/// </summary>
public sealed record CatalogItem(
    uint Id, string Name, string NameEn, GlamourSlot Slot, SlotMask Blocks, ulong ModelKey, uint IconId,
    Rgb Primary, Rgb Secondary, byte DyeSlots, StyleFamily Family, uint RaceCategory, uint SetKey = 0,
    ShapeTag Shapes = ShapeTag.None);

/// <summary>A dye.</summary>
public sealed record Stain(byte Id, string Name, Rgb Color);

/// <summary>A piece of an outfit: the item and its two dye channels (0 = no dye).</summary>
public readonly record struct OutfitPiece(uint ItemId, byte Stain0, byte Stain1);

/// <summary>The character's own colors. <c>FacePaint</c> is null without face paint.</summary>
public sealed record CharacterPalette(Rgb Skin, Rgb Hair, Rgb Highlights, Rgb LeftEye, Rgb RightEye, Rgb Lips, Rgb? FacePaint);
