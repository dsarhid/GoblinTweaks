using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Dalamud.Game;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;

namespace GoblinTweaks.Glamour.Game;

/// <summary>
/// Everything that can be proposed. <c>RaceRules</c> by EquipRaceCategory id. <c>ItemJobs</c> = for each item,
/// a bit per ClassJob id that can equip it (from its ClassJobCategory); missing = everyone.
/// </summary>
internal sealed record GlamourCatalog(
    IReadOnlyList<CatalogItem> Items, IReadOnlyList<Stain> Stains, IReadOnlyDictionary<uint, RaceRule> RaceRules,
    IReadOnlyDictionary<uint, ulong> ItemJobs);

/// <summary>
/// Builds the catalog from the game data: every item that can be worn in a gear slot and has a model, plus facewear
/// (Glasses). The colors of each item are the dominant colors of its icon, decoded from the icon file in the game
/// data and cached in <c>pluginConfigs/GoblinTweaks-glamour-colors.json</c> (rebuilt when the game version changes).
/// Runs off the framework thread: Lumina reads are thread-safe.
/// </summary>
internal sealed class CatalogBuilder
{
    private const string CacheName = "GoblinTweaks-glamour-colors.json";

    private static readonly string[] Tanks = ["GLA", "PLD", "MRD", "WAR", "DRK", "GNB"];
    private static readonly string[] Healers = ["CNJ", "WHM", "SCH", "AST", "SGE"];
    private static readonly string[] Melee = ["PGL", "MNK", "LNC", "DRG", "ROG", "NIN", "SAM", "RPR", "VPR"];
    private static readonly string[] Ranged = ["ARC", "BRD", "MCH", "DNC"];
    private static readonly string[] Casters = ["THM", "BLM", "ACN", "SMN", "RDM", "BLU", "PCT"];
    private static readonly string[] Crafters = ["CRP", "BSM", "ARM", "GSM", "LTW", "WVR", "ALC", "CUL"];
    private static readonly string[] Gatherers = ["MIN", "BTN", "FSH"];

    public Task<GlamourCatalog> BuildAsync(CancellationToken token, IProgress<float> progress)
        => Task.Run(() => Build(token, progress), token);

    private static GlamourCatalog Build(CancellationToken token, IProgress<float> progress)
    {
        var watch = Stopwatch.StartNew();
        var items = Svc.Data.GetExcelSheet<Item>();
        var itemsEn = Svc.Data.GetExcelSheet<Item>(ClientLanguage.English);
        var (categoryJobs, categoryFamily) = ReadJobCategories();

        // Gear first, without colors.
        var raw = new List<(CatalogItem Item, uint Icon)>();
        var itemJobs = new Dictionary<uint, ulong>();
        foreach (var item in items)
        {
            if (item.ModelMain == 0 || !item.EquipSlotCategory.IsValid || item.EquipSlotCategory.RowId == 0)
                continue;
            if (Slot(item.EquipSlotCategory.Value) is not { } placement)
                continue;
            var name = item.Name.ExtractText();
            if (name.Length == 0)
                continue;
            var nameEn = itemsEn.TryGetRow(item.RowId, out var en) ? en.Name.ExtractText() : name;
            var family = categoryFamily.GetValueOrDefault(item.ClassJobCategory.RowId, StyleFamily.Any);
            // The armor of one set shares its model id (the low 16 bits) from head to feet.
            var setKey = SlotMask.Armor.Has(placement.Slot) ? (uint)(item.ModelMain & 0xFFFF) : 0u;
            raw.Add((new CatalogItem(item.RowId, name, nameEn, placement.Slot, placement.Blocks, item.ModelMain, item.Icon,
                                     default, default, item.DyeCount, family, item.EquipRestriction.RowId, setKey), item.Icon));
            if (categoryJobs.TryGetValue(item.ClassJobCategory.RowId, out var jobs))
                itemJobs[item.RowId] = jobs;
        }

        // Facewear.
        var glassesEn = Svc.Data.GetExcelSheet<Glasses>(ClientLanguage.English);
        foreach (var glasses in Svc.Data.GetExcelSheet<Glasses>())
        {
            var name = glasses.Name.ExtractText();
            if (glasses.RowId == 0 || name.Length == 0 || glasses.Icon <= 0)
                continue;
            var nameEn = glassesEn.TryGetRow(glasses.RowId, out var en) ? en.Name.ExtractText() : name;
            raw.Add((new CatalogItem(GlamourIds.FacewearBase + glasses.RowId, name, nameEn, GlamourSlot.Facewear, SlotMask.None,
                                     glasses.Model, (uint)glasses.Icon, default, default, 0, StyleFamily.Any, 1), (uint)glasses.Icon));
        }
        token.ThrowIfCancellationRequested();

        // Icon colors, from the cache or decoded now.
        var version = GameVersion();
        var cache = LoadCache(version);
        var hits = 0;
        var missing = raw.Select(r => r.Icon).Distinct().Where(icon => !cache.ContainsKey(icon)).ToList();
        hits = raw.Select(r => r.Icon).Distinct().Count() - missing.Count;
        var done = 0;
        Parallel.ForEach(missing, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            icon =>
            {
                cache[icon] = IconColors(icon);
                var n = Interlocked.Increment(ref done);
                if (n % 200 == 0)
                    progress.Report((float)n / missing.Count);
            });
        progress.Report(1f);
        if (missing.Count > 0)
            SaveCache(version, cache);

        var catalog = raw.Select(r => cache.TryGetValue(r.Icon, out var colors)
                                     ? r.Item with { Primary = colors.Primary, Secondary = colors.Secondary }
                                     : r.Item with { Primary = new Rgb(128, 128, 128), Secondary = new Rgb(128, 128, 128) })
                         .ToList();

        var stains = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Stain>()
            .Where(s => s.RowId is > 0 and <= byte.MaxValue && s.Name.ExtractText().Length > 0)
            .Select(s => new Stain((byte)s.RowId, s.Name.ExtractText(), new Rgb((byte)(s.Color >> 16), (byte)(s.Color >> 8), (byte)s.Color)))
            .ToList();

        var raceRules = Svc.Data.GetExcelSheet<EquipRaceCategory>().ToDictionary(
            r => r.RowId,
            r => new RaceRule(r.RowId, [r.Hyur, r.Elezen, r.Lalafell, r.Miqote, r.Roegadyn, r.AuRa, r.Hrothgar, r.Viera], r.Male, r.Female));

        Svc.Log.Info($"GoblinGlamour: catalog of {catalog.Count} pieces ({string.Join(", ", catalog.GroupBy(i => i.Slot).Select(g => $"{g.Key} {g.Count()}"))}), " +
                     $"{stains.Count} dyes, icon colors {hits} cached / {missing.Count} read, in {watch.ElapsedMilliseconds} ms.");
        return new GlamourCatalog(catalog, stains, raceRules, itemJobs);
    }

    /// <summary>The slot an item goes in (the field set to 1) and the slots it covers (fields set to -1).</summary>
    private static (GlamourSlot Slot, SlotMask Blocks)? Slot(EquipSlotCategory c)
    {
        GlamourSlot? slot = null;
        var blocks = SlotMask.None;
        void Check(sbyte value, GlamourSlot s)
        {
            if (value > 0)
                slot ??= s;
            else if (value < 0)
                blocks |= s.Mask();
        }
        Check(c.MainHand, GlamourSlot.MainHand);
        Check(c.OffHand, GlamourSlot.OffHand);
        Check(c.Head, GlamourSlot.Head);
        Check(c.Body, GlamourSlot.Body);
        Check(c.Gloves, GlamourSlot.Hands);
        Check(c.Legs, GlamourSlot.Legs);
        Check(c.Feet, GlamourSlot.Feet);
        Check(c.Ears, GlamourSlot.Ears);
        Check(c.Neck, GlamourSlot.Neck);
        Check(c.Wrists, GlamourSlot.Wrists);
        Check(c.FingerL, GlamourSlot.Ring);
        Check(c.FingerR, GlamourSlot.Ring);
        if (c.SoulCrystal > 0 || c.Waist > 0)
            return null;
        return slot is { } found ? (found, blocks & ~found.Mask()) : null;
    }

    /// <summary>For every ClassJobCategory: a bit per ClassJob id it includes, and the job family when all its jobs share one.</summary>
    private static (Dictionary<uint, ulong> Jobs, Dictionary<uint, StyleFamily> Family) ReadJobCategories()
    {
        // ClassJobCategory has one bool column per job, named by the job's English abbreviation.
        var abbreviations = Svc.Data.GetExcelSheet<ClassJob>(ClientLanguage.English)
            .Where(j => j.RowId < 64 && j.Abbreviation.ExtractText().Length > 0)
            .ToDictionary(j => j.Abbreviation.ExtractText(), j => j.RowId);
        var columns = typeof(ClassJobCategory).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool) && abbreviations.ContainsKey(p.Name))
            .ToList();

        var families = new (string[] Jobs, StyleFamily Family)[]
        {
            (Tanks, StyleFamily.Tank), (Healers, StyleFamily.Healer), (Melee, StyleFamily.Melee), (Ranged, StyleFamily.Ranged),
            (Casters, StyleFamily.Caster), (Crafters, StyleFamily.Crafter), (Gatherers, StyleFamily.Gatherer),
        };

        var jobs = new Dictionary<uint, ulong>();
        var familyOf = new Dictionary<uint, StyleFamily>();
        foreach (var category in Svc.Data.GetExcelSheet<ClassJobCategory>())
        {
            ulong mask = 0;
            var names = new List<string>();
            object boxed = category;
            foreach (var column in columns)
            {
                if (column.GetValue(boxed) is true)
                {
                    mask |= 1UL << (int)abbreviations[column.Name];
                    names.Add(column.Name);
                }
            }
            jobs[category.RowId] = mask;
            var family = families.Where(f => names.Count > 0 && names.All(f.Jobs.Contains)).Select(f => f.Family).FirstOrDefault(StyleFamily.Any);
            familyOf[category.RowId] = family;
        }
        return (jobs, familyOf);
    }

    private static (Rgb Primary, Rgb Secondary) IconColors(uint icon)
    {
        try
        {
            var path = $"ui/icon/{icon / 1000 * 1000:D6}/{icon:D6}.tex";
            var tex = Svc.Data.GetFile<TexFile>(path);
            if (tex is null)
                return (new Rgb(128, 128, 128), new Rgb(128, 128, 128));
            var bgra = tex.ImageData;
            var rgba = new byte[bgra.Length];
            for (var i = 0; i + 3 < bgra.Length; i += 4)
            {
                rgba[i] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = bgra[i + 3];
            }
            return ColorMath.DominantColors(rgba, tex.Header.Width, tex.Header.Height);
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, "GoblinGlamour: icon {icon} unreadable.", icon);
            return (new Rgb(128, 128, 128), new Rgb(128, 128, 128));
        }
    }

    private static string GameVersion()
        => string.Join("|", Svc.Data.GameData.Repositories.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value.Version}"));

    private static string CachePath => Path.Combine(Svc.PluginInterface.ConfigFile.Directory!.FullName, CacheName);

    private sealed class CacheFile
    {
        public string GameVersion { get; set; } = "";
        public Dictionary<uint, string[]> Colors { get; set; } = [];
    }

    private static ConcurrentDictionary<uint, (Rgb Primary, Rgb Secondary)> LoadCache(string version)
    {
        var result = new ConcurrentDictionary<uint, (Rgb, Rgb)>();
        try
        {
            if (!File.Exists(CachePath))
                return result;
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
            if (file is null || file.GameVersion != version)
                return result;
            foreach (var (icon, hexes) in file.Colors)
                if (hexes.Length == 2)
                    result[icon] = (Rgb.FromHex(hexes[0]), Rgb.FromHex(hexes[1]));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "GoblinGlamour: color cache unreadable; rebuilding it.");
            result.Clear();
        }
        return result;
    }

    private static void SaveCache(string version, ConcurrentDictionary<uint, (Rgb Primary, Rgb Secondary)> cache)
    {
        try
        {
            var file = new CacheFile
            {
                GameVersion = version,
                Colors = cache.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => new[] { pair.Value.Primary.ToHex(), pair.Value.Secondary.ToHex() }),
            };
            var temp = CachePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file));
            File.Move(temp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "GoblinGlamour: could not save the color cache.");
        }
    }
}
