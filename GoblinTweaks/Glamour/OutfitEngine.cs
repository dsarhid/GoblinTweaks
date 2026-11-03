namespace GoblinTweaks.Glamour;

/// <summary>
/// What to generate. <c>Slots</c> = slots to fill (accessories / facewear only when their checkbox is on).
/// <c>Words</c> = free words the item names should contain (English). <c>Locked</c> pieces are kept in every
/// outfit. <c>Count</c> is clamped to 3..15.
/// </summary>
public sealed record GenerateOptions(
    SlotMask Slots, bool HasOffHand, bool UseDyes, Theme? Theme, IReadOnlyList<string> Words,
    IReadOnlyDictionary<GlamourSlot, OutfitPiece> Locked, int Count)
{
    /// <summary>Words that mark a costume (event outfits, swimwear...): left out unless the theme or the words ask for it.</summary>
    public IReadOnlyList<string> CostumeWords { get; init; } = [];

    /// <summary>Pieces the user said they do not like: never proposed.</summary>
    public IReadOnlySet<uint> Disliked { get; init; } = new HashSet<uint>();

    /// <summary>Pieces the user likes (from their favorites): preferred, and so are the other pieces of their sets.</summary>
    public IReadOnlySet<uint> Liked { get; init; } = new HashSet<uint>();

    /// <summary>Pieces shown by the last presses of Generate, with how many times: avoided, so each press brings new ones.</summary>
    public IReadOnlyDictionary<uint, int> RecentItems { get; init; } = new Dictionary<uint, int>();

    /// <summary>Sets (<see cref="CatalogItem.SetKey"/>) shown by the last presses, with how many times: avoided too.</summary>
    public IReadOnlyDictionary<uint, int> RecentSets { get; init; } = new Dictionary<uint, int>();

    /// <summary>Where in the list of anchor colors the first outfit starts (to continue a list made in several calls).</summary>
    public int AnchorOffset { get; init; }

    /// <summary>
    /// Themes used as style tags: two pieces that fit the same one (both gothic, both samurai...) share a style,
    /// whatever theme was chosen. At most 64 are used.
    /// </summary>
    public IReadOnlyList<Theme> StyleThemes { get; init; } = [];

    /// <summary>
    /// Pieces made for the same role (tank, caster, healer...) count as sharing a style. Off by default: a samurai
    /// may well wear tank armor.
    /// </summary>
    public bool MatchRole { get; init; }

    /// <summary>
    /// Build outfits around the character's own colors (on by default). Off, every outfit gets a palette of its own,
    /// at random, and the character's colors play no part.
    /// </summary>
    public bool UseCharacterColors { get; init; } = true;

    /// <summary>Shapes asked for ("with a cape"): pieces of those shapes count like pieces that fit the theme.</summary>
    public ShapeTag WantedShapes { get; init; }

    /// <summary>Shapes that look wrong together (heavy armor and swimwear): a piece that clashes with a chosen one loses that much.</summary>
    public IReadOnlyList<ShapeClash> Clashes { get; init; } = [];
}

/// <summary>Why an outfit was proposed; shown as a short line on its card.</summary>
public enum ReasonCode
{
    EchoesHair, EchoesEyes, ContrastsSkin, Monochrome, Complementary, MatchesTheme, SameFamily, SlotEmpty,
    EchoesHighlights, Analogous, Triadic, Pastel, DeepTone, SetBase, OwnPalette,
}

public sealed record Reason(ReasonCode Code, GlamourSlot Slot);

public sealed record OutfitProposal(IReadOnlyDictionary<GlamourSlot, OutfitPiece> Pieces, float Score, IReadOnlyList<Reason> Reasons);

/// <summary>
/// Proposes outfits without AI. Each outfit is built around one "anchor" color taken from the character
/// (hair, eyes, highlights, colors related to the hair by the color wheel, a near-monochrome tone, a pastel and a
/// deep tone, a contrast with the skin) — one anchor per outfit, which is what makes them differ — then fills the
/// slots greedily with the pieces that go best with the anchor and with what is already chosen, mixing pieces of
/// any set. After ten outfits the anchors come round again, turned a little on the color wheel; every reuse of a
/// piece costs more than the last, so long lists do not repeat the same favorite piece.
/// <para>
/// The first armor piece chosen (the body, usually) sets the outfit's base: up to two more pieces of its set are
/// preferred, then the rest are accents from other sets. Costumes are left out unless the theme asks for them,
/// disliked pieces are never used, and liked pieces (and their sets) are preferred.
/// </para>
/// <para>
/// Every piece must go with the rest of its outfit: by style (same set or a style theme in common, and the same
/// role when <see cref="GenerateOptions.MatchRole"/> is on) or by color. Chance picks among the pieces that do, and each locked piece takes some of it away: the more pieces
/// are locked, the more the others follow from them.
/// </para>
/// </summary>
public static class OutfitEngine
{
    // Wide, so every press can reach well past the pieces that fit the character best.
    private const int ShortlistSize = 150;

    // Shortlist score.
    private const float ShortPalette = 0.45f, ShortMatch = 0.35f, ShortJitter = 0.35f;
    private const float DyedFit = 0.85f;   // palette fit of a piece that will be dyed

    // Pick score inside an outfit. PickChosen weighs how well a piece goes with the pieces already chosen.
    private const float PickAnchor = 0.4f, PickChosen = 0.4f, PickShortlist = 0.2f;

    // A piece that goes with none of the chosen ones (relation below this) loses this much.
    private const float RelatedAt = 0.5f, UnrelatedMalus = 0.4f;

    // How related two pieces are by style alone: same set, a style theme in common, same role.
    private const float SameSet = 1f, SameTheme = 0.8f, SameRole = 0.7f;

    // Least share of chance left when nearly everything is locked.
    private const float MinFreedom = 0.1f;
    private const float PickMatch = 0.5f;   // only with a theme or words: a chosen theme must beat the reuse penalty
    private const float FamilyBonus = 0.1f;
    private const float ReusePenalty = 0.3f;   // per earlier use of the same piece

    // Base set: pieces of the base's set get a bonus until it has this many, then a malus (the rest are accents).
    private const int SetPieces = 3;
    private const float SetBonus = 0.35f, SetMalus = 0.2f;

    // Liked pieces, and other pieces of a liked piece's set.
    private const float LikedBonus = 0.15f, LikedSetBonus = 0.08f;

    // Pieces and sets the last presses showed (per time shown, up to three times), and chance in each pick.
    private const float RecentItemPenalty = 0.3f, RecentSetPenalty = 0.2f;
    private const int RecentCap = 3;
    private const float PickJitter = 0.3f;

    /// <summary>Fill order: the big pieces decide the look first.</summary>
    private static readonly GlamourSlot[] FillOrder =
    [
        GlamourSlot.Body, GlamourSlot.Legs, GlamourSlot.Head, GlamourSlot.Hands, GlamourSlot.Feet,
        GlamourSlot.MainHand, GlamourSlot.OffHand,
        GlamourSlot.Ears, GlamourSlot.Neck, GlamourSlot.Wrists, GlamourSlot.Ring, GlamourSlot.Facewear,
    ];

    private const int MaxOutfits = 15;

    /// <summary>How far each new round of anchors turns on the color wheel, in degrees.</summary>
    private const float RoundHueShift = 25f;

    private enum AnchorKind
    {
        Hair, Eyes, Complementary, Monochrome, ContrastSkin, Highlights, Analogous, Triadic, Pastel, DeepTone, Own,
    }

    /// <summary>Palette fit of every piece when the character's colors are not used: none is preferred.</summary>
    private const float NeutralFit = 0.7f;

    private static readonly AnchorKind[] AnchorOrder =
    [
        AnchorKind.Hair, AnchorKind.Eyes, AnchorKind.Complementary, AnchorKind.Monochrome, AnchorKind.ContrastSkin,
        AnchorKind.Highlights, AnchorKind.Analogous, AnchorKind.Triadic, AnchorKind.Pastel, AnchorKind.DeepTone,
    ];

    public static IReadOnlyList<OutfitProposal> Generate(
        IReadOnlyList<CatalogItem> candidates, IReadOnlyList<Stain> stains, CharacterPalette palette,
        GenerateOptions options, Random random)
    {
        var count = Math.Clamp(options.Count, 3, MaxOutfits);
        var slots = ActiveSlots(options);
        var byId = new Dictionary<uint, CatalogItem>();
        var bySet = new Dictionary<(GlamourSlot, uint), List<CatalogItem>>();
        foreach (var item in candidates)
        {
            if (!byId.TryAdd(item.Id, item) || item.SetKey == 0 || !Usable(item, options))
                continue;
            if (!bySet.TryGetValue((item.Slot, item.SetKey), out var members))
                bySet[(item.Slot, item.SetKey)] = members = [];
            members.Add(item);
        }
        var likedSets = new HashSet<uint>();
        foreach (var id in options.Liked)
            if (byId.TryGetValue(id, out var liked) && liked.SetKey != 0)
                likedSets.Add(liked.SetKey);
        // Chance shrinks with every locked slot: with most of the outfit locked, the rest follows from it.
        var lockedSlots = options.Locked.Keys.Count(slots.Contains);
        var freedom = slots.Count == 0 ? 1f : Math.Max(MinFreedom, 1f - (float)lockedSlots / slots.Count);
        var context = new Context(byId, bySet, likedSets, palette, stains, options, random, freedom,
                                  new Dictionary<uint, ulong>(), options.StyleThemes.Take(64).ToList());

        // One shortlist per slot, shared by all outfits (with jitter, so pressing Generate again varies).
        var shortlists = new Dictionary<GlamourSlot, List<Scored>>();
        foreach (var slot in slots)
            shortlists[slot] = ShortlistScored(candidates, slot, palette, options, ShortlistSize, random, freedom);

        var used = new Dictionary<uint, int>();
        var proposals = new List<OutfitProposal>(count);
        for (var i = 0; i < count; i++)
        {
            if (!options.UseCharacterColors)
            {
                var (own, ownSecond) = OwnAnchor(random);
                proposals.Add(BuildOutfit(AnchorKind.Own, own, ownSecond, slots, shortlists, context, used));
                continue;
            }
            var index = i + Math.Max(0, options.AnchorOffset);
            var kind = AnchorOrder[index % AnchorOrder.Length];
            var (anchor, second) = Anchor(kind, palette);
            anchor = TurnHue(anchor, RoundHueShift * (index / AnchorOrder.Length));
            proposals.Add(BuildOutfit(kind, anchor, second, slots, shortlists, context, used));
        }
        return proposals;
    }

    /// <summary>
    /// The best items of one slot for this character and theme, at most <paramref name="max"/>, one per look
    /// (<c>ModelKey</c>). Used by <see cref="Generate"/> and to pick the candidates sent to the AI. Without
    /// <paramref name="random"/> there is no jitter.
    /// </summary>
    public static IReadOnlyList<CatalogItem> Shortlist(
        IReadOnlyList<CatalogItem> candidates, GlamourSlot slot, CharacterPalette palette, GenerateOptions options,
        int max, Random? random = null)
        => ShortlistScored(candidates, slot, palette, options, max, random, 1f).Select(s => s.Item).ToList();

    private readonly record struct Scored(CatalogItem Item, float Score, float Match);

    private sealed record Context(
        Dictionary<uint, CatalogItem> ById, Dictionary<(GlamourSlot, uint), List<CatalogItem>> BySet, HashSet<uint> LikedSets,
        CharacterPalette Palette, IReadOnlyList<Stain> Stains, GenerateOptions Options, Random Random, float Freedom,
        Dictionary<uint, ulong> StyleTags, List<Theme> StyleThemes)
    {
        /// <summary>A bit per style theme the piece fits (cached).</summary>
        public ulong Tags(CatalogItem item)
        {
            if (StyleTags.TryGetValue(item.Id, out var tags)) return tags;
            for (var i = 0; i < StyleThemes.Count; i++)
                if (Themes.Match(StyleThemes[i], item.NameEn) > 0)
                    tags |= 1UL << i;
            return StyleTags[item.Id] = tags;
        }

        /// <summary>How well two pieces go together: by style (set, theme, role) or by color, 0 to 1.</summary>
        public float Relation(CatalogItem a, Rgb aColor, CatalogItem? b, Rgb bColor)
        {
            var color = ColorMath.Harmony(aColor, bColor);
            if (b is null) return color;
            if (a.SetKey != 0 && a.SetKey == b.SetKey) return SameSet;
            var style = (Tags(a) & Tags(b)) != 0 ? SameTheme
                      : Options.MatchRole && a.Family != StyleFamily.Any && a.Family == b.Family ? SameRole
                      : 0f;
            return Math.Max(style, color);
        }
    }

    /// <summary>Not disliked, and not a costume unless the theme or the words ask for it.</summary>
    private static bool Usable(CatalogItem item, GenerateOptions options)
        => !options.Disliked.Contains(item.Id)
           && (options.CostumeWords.Count == 0 || Themes.MatchWords(options.CostumeWords, item.NameEn) == 0 || Match(item, options) > 0);

    /// <summary>
    /// How well a piece suits the character before the outfit is known. A piece that will be dyed takes the outfit's
    /// color, so its own color says little: it counts as a good fit, which keeps the many dyeable pieces in play.
    /// </summary>
    private static float ShortScore(CatalogItem item, CharacterPalette? palette, float match, Random? random, bool useDyes, float freedom = 1f)
        => ShortPalette * (useDyes && item.DyeSlots > 0 ? DyedFit : palette is null ? NeutralFit : PaletteFit(item.Primary, palette)) + ShortMatch * match
           + ShortJitter * freedom * (random is null ? 0f : (float)random.NextDouble());

    private static List<Scored> ShortlistScored(
        IReadOnlyList<CatalogItem> candidates, GlamourSlot slot, CharacterPalette palette, GenerateOptions options,
        int max, Random? random, float freedom)
    {
        var best = new Dictionary<ulong, Scored>();
        foreach (var item in candidates)
        {
            if (item.Slot != slot || !Usable(item, options))
                continue;
            var match = Match(item, options);
            var score = ShortScore(item, options.UseCharacterColors ? palette : null, match, random, options.UseDyes, freedom);
            var key = item.ModelKey != 0 ? item.ModelKey : 0x8000_0000_0000_0000UL | item.Id;
            if (!best.TryGetValue(key, out var current) || score > current.Score)
                best[key] = new Scored(item, score, match);
        }
        return best.Values.OrderByDescending(s => s.Score).ThenBy(s => s.Item.Id).Take(max).ToList();
    }

    private static OutfitProposal BuildOutfit(
        AnchorKind kind, Rgb anchor, Rgb second, List<GlamourSlot> slots, Dictionary<GlamourSlot, List<Scored>> shortlists,
        Context context, Dictionary<uint, int> used)
    {
        var (byId, stains, options) = (context.ById, context.Stains, context.Options);
        uint baseSet = 0;
        var setCount = 0;
        var pieces = new Dictionary<GlamourSlot, OutfitPiece>();
        var reasons = new List<Reason>();
        var chosen = new List<(CatalogItem? Item, Rgb Color)>();
        var filled = SlotMask.None;   // slots taken or covered
        var scores = new List<float>();
        StyleFamily? family = null;
        var familyMatches = 0;
        var themed = options.Theme is not null || options.Words.Count > 0 || options.WantedShapes != ShapeTag.None;
        GlamourSlot? themeSlot = null;

        // Locked pieces first, never replaced.
        foreach (var (slot, piece) in options.Locked)
        {
            if (!slots.Contains(slot))
                continue;
            pieces[slot] = piece;
            filled |= slot.Mask();
            if (byId.TryGetValue(piece.ItemId, out var item))
            {
                filled |= item.Blocks;
                if (SlotMask.Armor.Has(slot) && item.SetKey != 0 && (baseSet == 0 || baseSet == item.SetKey))
                {
                    baseSet = item.SetKey;
                    setCount++;
                }
                chosen.Add((item, PieceColor(item, piece, stains)));
                family ??= item.Family == StyleFamily.Any ? null : item.Family;
            }
        }

        foreach (var slot in slots)
        {
            if (filled.Has(slot))
                continue;

            var list = shortlists[slot];
            if (baseSet != 0 && setCount < SetPieces && context.BySet.TryGetValue((slot, baseSet), out var members))
            {
                // The rest of the base's set may not be in the shortlist (its colors are judged on their own): add it.
                list = [.. list];
                foreach (var member in members)
                    if (!list.Exists(s => s.Item.Id == member.Id))
                    {
                        var match = Match(member, options);
                        list.Add(new Scored(member, ShortScore(member, options.UseCharacterColors ? context.Palette : null, match, null, options.UseDyes), match));
                    }
            }
            if (list.Count == 0)
            {
                reasons.Add(new Reason(ReasonCode.SlotEmpty, slot));
                continue;
            }

            Scored? bestPick = null;
            var bestScore = float.MinValue;
            OutfitPiece bestPiece = default;
            foreach (var scored in list)
            {
                var item = scored.Item;
                if ((item.Blocks & filled) != 0)
                    continue;   // would cover a piece already chosen

                var piece = Dye(item, anchor, second, stains, options.UseDyes);
                var color = PieceColor(item, piece, stains);
                var score = PickAnchor * ColorMath.Harmony(color, anchor) + PickShortlist * scored.Score;
                if (chosen.Count == 0)
                    score += PickChosen * 0.8f;
                else
                {
                    var best = 0f;
                    var sum = 0f;
                    var clash = 0f;
                    foreach (var (other, otherColor) in chosen)
                    {
                        var relation = context.Relation(item, color, other, otherColor);
                        sum += relation;
                        best = Math.Max(best, relation);
                        if (other is not null)
                            clash = Math.Max(clash, ShapeTags.Clash(item.Shapes, other.Shapes, options.Clashes));
                    }
                    score += PickChosen * sum / chosen.Count;
                    if (best < RelatedAt)
                        score -= UnrelatedMalus;
                    score -= clash;
                }
                if (themed)
                    score += PickMatch * scored.Match;
                if (options.MatchRole && family is { } f && item.Family == f)
                    score += FamilyBonus;
                score -= ReusePenalty * used.GetValueOrDefault(item.Id);
                score -= RecentItemPenalty * Math.Min(RecentCap, options.RecentItems.GetValueOrDefault(item.Id));
                if (item.SetKey != 0)
                    score -= RecentSetPenalty * Math.Min(RecentCap, options.RecentSets.GetValueOrDefault(item.SetKey));
                score += PickJitter * context.Freedom * (float)context.Random.NextDouble();
                if (baseSet != 0 && item.SetKey == baseSet && SlotMask.Armor.Has(slot))
                    score += setCount < SetPieces ? SetBonus : -SetMalus;
                if (options.Liked.Contains(item.Id))
                    score += LikedBonus;
                else if (item.SetKey != 0 && context.LikedSets.Contains(item.SetKey))
                    score += LikedSetBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPick = scored;
                    bestPiece = piece;
                }
            }

            if (bestPick is not { } pick)
                continue;   // every candidate would cover a chosen piece: the slot is covered anyway

            pieces[slot] = bestPiece;
            filled |= slot.Mask() | pick.Item.Blocks;
            chosen.Add((pick.Item, PieceColor(pick.Item, bestPiece, stains)));
            scores.Add(bestScore);
            used[pick.Item.Id] = used.GetValueOrDefault(pick.Item.Id) + 1;
            if (SlotMask.Armor.Has(slot) && pick.Item.SetKey != 0)
            {
                if (baseSet == 0)
                    baseSet = pick.Item.SetKey;
                if (pick.Item.SetKey == baseSet)
                    setCount++;
            }
            if (pick.Match > 0 && themed)
                themeSlot ??= slot;
            if (pick.Item.Family != StyleFamily.Any)
            {
                if (family is null)
                    family = pick.Item.Family;
                else if (pick.Item.Family == family)
                    familyMatches++;
            }
        }

        var anchorSlot = pieces.Keys.FirstOrDefault(s => !options.Locked.ContainsKey(s), pieces.Keys.FirstOrDefault(GlamourSlot.Body));
        reasons.Insert(0, new Reason(kind switch
        {
            AnchorKind.Hair          => ReasonCode.EchoesHair,
            AnchorKind.Eyes          => ReasonCode.EchoesEyes,
            AnchorKind.Complementary => ReasonCode.Complementary,
            AnchorKind.Monochrome    => ReasonCode.Monochrome,
            AnchorKind.Highlights    => ReasonCode.EchoesHighlights,
            AnchorKind.Analogous     => ReasonCode.Analogous,
            AnchorKind.Triadic       => ReasonCode.Triadic,
            AnchorKind.Pastel        => ReasonCode.Pastel,
            AnchorKind.DeepTone      => ReasonCode.DeepTone,
            AnchorKind.Own           => ReasonCode.OwnPalette,
            _                        => ReasonCode.ContrastsSkin,
        }, anchorSlot));
        if (themeSlot is { } ts)
            reasons.Insert(1, new Reason(ReasonCode.MatchesTheme, ts));
        if (setCount >= 2)
            reasons.Insert(themeSlot is null ? 1 : 2, new Reason(ReasonCode.SetBase, GlamourSlot.Body));
        if (options.MatchRole && familyMatches > 0)
            reasons.Add(new Reason(ReasonCode.SameFamily, GlamourSlot.Body));

        return new OutfitProposal(pieces, scores.Count == 0 ? 0f : scores.Average(), reasons);
    }

    private static List<GlamourSlot> ActiveSlots(GenerateOptions options)
        => FillOrder.Where(slot => options.Slots.Has(slot) && (slot != GlamourSlot.OffHand || options.HasOffHand)).ToList();

    /// <summary>The anchor color of an outfit and the color for the second dye channel.</summary>
    private static (Rgb Anchor, Rgb Second) Anchor(AnchorKind kind, CharacterPalette palette)
    {
        var hairHue = ColorMath.Hue(palette.Hair);
        switch (kind)
        {
            case AnchorKind.Hair:
                return (palette.Hair, palette.RightEye);
            case AnchorKind.Eyes:
                return (palette.RightEye, palette.Hair);
            case AnchorKind.Complementary:
            {
                var lightness = Math.Clamp(ColorMath.Lightness(palette.Hair), 0.35f, 0.75f);
                var chroma = Math.Max(ColorMath.Chroma(palette.Hair), 0.08f);
                return (ColorMath.FromOklch(lightness, chroma, (hairHue + 180f) % 360f), palette.Hair);
            }
            case AnchorKind.Monochrome:
                return (ColorMath.FromOklch(0.35f, 0.03f, hairHue), palette.Highlights);
            case AnchorKind.Highlights:
                return (palette.Highlights, palette.Hair);
            case AnchorKind.Analogous:
                return (Wheel(palette.Hair, 35f), palette.RightEye);
            case AnchorKind.Triadic:
                return (Wheel(palette.Hair, 120f), palette.Hair);
            case AnchorKind.Pastel:
                return (ColorMath.FromOklch(0.86f, 0.05f, ColorMath.Hue(palette.RightEye)), palette.Highlights);
            case AnchorKind.DeepTone:
                return (ColorMath.FromOklch(0.22f, 0.04f, ColorMath.Hue(palette.RightEye)), palette.RightEye);
            default:
            {
                // Light skin → deep tone, dark skin → light tone, on the opposite hue.
                var skinLight = ColorMath.Lightness(palette.Skin) > 0.6f;
                var hue = (ColorMath.Hue(palette.Skin) + 180f) % 360f;
                return (ColorMath.FromOklch(skinLight ? 0.32f : 0.85f, 0.09f, hue), palette.Hair);
            }
        }
    }

    /// <summary>
    /// A palette that owes nothing to the character: a color of any hue, lightness and strength, and for the second
    /// dye one that goes with it (next to it, opposite, or a lighter or darker shade of it).
    /// </summary>
    private static (Rgb Anchor, Rgb Second) OwnAnchor(Random random)
    {
        var hue = (float)random.NextDouble() * 360f;
        var lightness = 0.3f + (float)random.NextDouble() * 0.5f;
        var chroma = 0.03f + (float)random.NextDouble() * 0.12f;
        var anchor = ColorMath.FromOklch(lightness, chroma, hue);
        var second = random.Next(3) switch
        {
            0 => ColorMath.FromOklch(lightness, chroma, (hue + 30f) % 360f),
            1 => ColorMath.FromOklch(Math.Clamp(1.1f - lightness, 0.25f, 0.85f), chroma, (hue + 180f) % 360f),
            _ => ColorMath.FromOklch(lightness > 0.55f ? lightness - 0.3f : lightness + 0.3f, chroma * 0.7f, hue),
        };
        return (anchor, second);
    }

    /// <summary>A color turned <paramref name="degrees"/> on the wheel from <paramref name="from"/>, kept clearly colored and of middle lightness.</summary>
    private static Rgb Wheel(Rgb from, float degrees)
    {
        var lightness = Math.Clamp(ColorMath.Lightness(from), 0.35f, 0.75f);
        var chroma = Math.Max(ColorMath.Chroma(from), 0.08f);
        return ColorMath.FromOklch(lightness, chroma, (ColorMath.Hue(from) + degrees) % 360f);
    }

    /// <summary>The same color turned on the wheel; greys (no hue to turn) stay as they are.</summary>
    private static Rgb TurnHue(Rgb color, float degrees)
    {
        if (degrees == 0f || ColorMath.Chroma(color) < 0.04f)
            return color;
        return ColorMath.FromOklch(ColorMath.Lightness(color), ColorMath.Chroma(color), (ColorMath.Hue(color) + degrees) % 360f);
    }

    /// <summary>How well an item color suits the character: echoes hair or eyes, or stands out from the skin.</summary>
    private static float PaletteFit(Rgb color, CharacterPalette palette)
    {
        var hair = ColorMath.Harmony(color, palette.Hair);
        var eyes = ColorMath.Harmony(color, palette.RightEye);
        var skinContrast = Math.Min(1f, ColorMath.Distance(color, palette.Skin) * 2f);
        return Math.Max(hair, Math.Max(eyes, skinContrast));
    }

    private static float Match(CatalogItem item, GenerateOptions options)
    {
        var theme = options.Theme is { } t ? Themes.Match(t, item.NameEn) : 0f;
        var shape = (item.Shapes & options.WantedShapes) != 0 ? 1f : 0f;
        return Math.Max(Math.Max(theme, shape), Themes.MatchWords(options.Words, item.NameEn));
    }

    /// <summary>How many color schemes <see cref="Recolor"/> tries for one that changes the outfit's dyes.</summary>
    private const int RecolorTries = 12;

    /// <summary>
    /// The same pieces with other dyes: a new color scheme (one of the character's, when <paramref name="palette"/> is
    /// given, or one of its own), dyed the way Generate dyes. Pieces that cannot be dyed, unknown items and the
    /// <paramref name="keep"/> slots (locked pieces) stay as they are. A few schemes are tried, so the result differs.
    /// </summary>
    public static IReadOnlyDictionary<GlamourSlot, OutfitPiece> Recolor(
        IReadOnlyDictionary<GlamourSlot, OutfitPiece> pieces, IReadOnlyDictionary<uint, CatalogItem> items, IReadOnlyList<Stain> stains,
        CharacterPalette? palette, IReadOnlySet<GlamourSlot> keep, Random random)
    {
        var result = new Dictionary<GlamourSlot, OutfitPiece>(pieces);
        if (stains.Count == 0)
            return result;

        var kinds = Enum.GetValues<AnchorKind>().Where(kind => kind != AnchorKind.Own).ToArray();
        for (var attempt = 0; attempt < RecolorTries; attempt++)
        {
            var (anchor, second) = palette is not null && random.Next(2) == 0
                ? Anchor(kinds[random.Next(kinds.Length)], palette)
                : OwnAnchor(random);

            var changed = false;
            foreach (var (slot, piece) in pieces)
            {
                if (keep.Contains(slot) || !items.TryGetValue(piece.ItemId, out var item) || item.DyeSlots == 0)
                    continue;
                var dyed = Dye(item, anchor, second, stains, useDyes: true);
                result[slot] = dyed;
                changed |= dyed != piece;
            }
            if (changed)
                break;
        }
        return result;
    }

    private static OutfitPiece Dye(CatalogItem item, Rgb anchor, Rgb second, IReadOnlyList<Stain> stains, bool useDyes)
    {
        if (!useDyes || item.DyeSlots == 0 || stains.Count == 0)
            return new OutfitPiece(item.Id, 0, 0);
        var stain0 = Nearest(stains, anchor);
        var stain1 = item.DyeSlots >= 2 ? Nearest(stains, second) : (byte)0;
        return new OutfitPiece(item.Id, stain0, stain1);
    }

    private static byte Nearest(IReadOnlyList<Stain> stains, Rgb color)
    {
        var best = stains[0];
        var bestDistance = ColorMath.Distance(best.Color, color);
        for (var i = 1; i < stains.Count; i++)
        {
            var distance = ColorMath.Distance(stains[i].Color, color);
            if (distance < bestDistance)
            {
                best = stains[i];
                bestDistance = distance;
            }
        }
        return best.Id;
    }

    /// <summary>The color a piece shows: its first dye when dyed, its own color otherwise.</summary>
    private static Rgb PieceColor(CatalogItem item, OutfitPiece piece, IReadOnlyList<Stain> stains)
    {
        if (piece.Stain0 != 0)
            foreach (var stain in stains)
                if (stain.Id == piece.Stain0)
                    return stain.Color;
        return item.Primary;
    }
}
