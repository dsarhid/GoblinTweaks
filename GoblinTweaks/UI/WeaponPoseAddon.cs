using System.Globalization;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using ClassJobRow = Lumina.Excel.Sheets.ClassJob;

namespace GoblinTweaks.UI;

/// <summary>
/// Native window listing every combat job with its level and the weapon-drawn pose saved for it,
/// laid out like the Classes/Jobs tab of the Character window. Its height follows the longest column.
/// </summary>
internal unsafe class WeaponPoseAddon : NativeAddon
{
    private static readonly Vector4 TitleGold  = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 LevelMax   = new(240 / 255f, 142 / 255f,  55 / 255f, 1f);
    private static readonly Vector4 White      = new(0.93f, 0.93f, 0.93f, 1f);
    private static readonly Vector4 MutedGrey  = new(0.50f, 0.50f, 0.50f, 1f);

    private const uint  FramedJobIconBase = 62100;
    private const float HeaderH  = 26f;
    private const float RowH     = 32f;
    private const float GroupGap = 10f;
    private const float IconSz   = 28f;
    private const float LevelW   = 34f;
    private const float BottomH  = 34f;
    private const float Width    = 450f;
    private const float MinHeight = 260f;

    // Window frame around the content: title bar and borders above, padding below (KamiToolKit window).
    private const float FrameH = 62f;

    // ClassJob.Role values, and the PrimaryStat that tells physical from magical ranged.
    private const byte RoleTank = 1, RoleMelee = 2, RoleRanged = 3, RoleHealer = 4;
    private const byte StatDexterity = 2;

    internal enum Group { Tank, Melee, Healer, PhysicalRanged, MagicalRanged }

    private static readonly Group[][] Columns =
    [
        [Group.Tank, Group.Melee],
        [Group.Healer, Group.PhysicalRanged, Group.MagicalRanged],
    ];

    private sealed record Row(ClassJobRow Job, TextNode Level, TextNode Name, TextNode Pose);

    private readonly List<Row> _rows = [];
    private DateTime _nextRefresh = DateTime.MinValue;

    public required WeaponPosePerJob Tweak { get; init; }

    /// <summary>The jobs to list, by group (from <see cref="JobsByGroup"/>).</summary>
    public required Dictionary<Group, List<ClassJobRow>> Jobs { get; init; }

    /// <summary>Which jobs this window lists, to tell when it has to be built again.</summary>
    public string LayoutKey => KeyOf(Jobs);

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c       = ContentStartPosition;
        var cs      = ContentSize;
        var columnW = cs.X / Columns.Length;

        for (var column = 0; column < Columns.Length; column++)
        {
            var x = c.X + column * columnW;
            var y = c.Y;
            foreach (var group in Columns[column])
            {
                if (!Jobs.TryGetValue(group, out var groupJobs)) continue;

                BuildHeader(new Vector2(x, y), columnW - 12f, Tweak.Text($"Group.{group}"));
                y += HeaderH;

                foreach (var job in groupJobs)
                {
                    BuildRow(new Vector2(x, y), columnW - 12f, job);
                    y += RowH;
                }

                y += GroupGap;
            }
        }

        BuildBottomBar(new Vector2(c.X, c.Y + cs.Y - BottomH), new Vector2(cs.X, BottomH));
        Refresh();
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        if (DateTime.UtcNow < _nextRefresh) return;
        Refresh();
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _rows.Clear();
        base.OnFinalize(addon);
    }

    /// <summary>
    /// Combat jobs by role, in the game's own order. Base classes only appear if a pose was saved on them;
    /// with <paramref name="onlyUnlocked"/>, jobs the character has not unlocked are left out.
    /// </summary>
    internal static Dictionary<Group, List<ClassJobRow>> JobsByGroup(IReadOnlyDictionary<uint, byte> saved, bool onlyUnlocked)
    {
        var sheet   = Svc.Data.GetExcelSheet<ClassJobRow>();
        var classes = sheet.Where(job => job.ClassJobParent.RowId != job.RowId).Select(job => job.ClassJobParent.RowId).ToHashSet();

        var result = new Dictionary<Group, List<ClassJobRow>>();
        foreach (var job in sheet.OrderBy(job => job.UIPriority))
        {
            if (classes.Contains(job.RowId) && !saved.ContainsKey(job.RowId)) continue;
            if (onlyUnlocked && Svc.PlayerState.GetClassJobLevel(job) <= 0) continue;

            Group group;
            switch (job.Role)
            {
                case RoleTank:   group = Group.Tank; break;
                case RoleMelee:  group = Group.Melee; break;
                case RoleHealer: group = Group.Healer; break;
                case RoleRanged: group = job.PrimaryStat == StatDexterity ? Group.PhysicalRanged : Group.MagicalRanged; break;
                default: continue; // crafters, gatherers and the adventurer placeholder
            }

            if (!result.TryGetValue(group, out var list))
                result[group] = list = [];
            list.Add(job);
        }

        return result;
    }

    /// <summary>Identifies the jobs of a layout, in order.</summary>
    internal static string KeyOf(Dictionary<Group, List<ClassJobRow>> jobs)
        => string.Join(",", Columns.SelectMany(column => column)
            .Select(group => jobs.TryGetValue(group, out var list) ? string.Join(" ", list.Select(job => job.RowId)) : "-"));

    /// <summary>Window size that fits the longest column and the bottom bar.</summary>
    internal static Vector2 SizeFor(Dictionary<Group, List<ClassJobRow>> jobs)
    {
        var tallest = Columns.Max(column => column
            .Where(jobs.ContainsKey)
            .Sum(group => HeaderH + jobs[group].Count * RowH + GroupGap));

        return new Vector2(Width, MathF.Max(MinHeight, tallest + BottomH + 8f + FrameH));
    }

    private void BuildHeader(Vector2 pos, float width, string text)
    {
        new TextNode
        {
            String    = text,
            Position  = new Vector2(pos.X + 6f, pos.Y + 2f),
            Size      = new Vector2(width - 6f, 18f),
            TextColor = TitleGold,
            FontSize  = 14,
        }.AttachNode(this);

        new HorizontalLineNode
        {
            Position = new Vector2(pos.X, pos.Y + HeaderH - 6f),
            Size     = new Vector2(width, 2f),
        }.AttachNode(this);
    }

    private void BuildRow(Vector2 pos, float width, ClassJobRow job)
    {
        new IconImageNode
        {
            IconId     = FramedJobIconBase + job.RowId,
            FitTexture = true,
            Position   = new Vector2(pos.X + 2f, pos.Y + (RowH - IconSz) / 2f),
            Size       = new Vector2(IconSz, IconSz),
        }.AttachNode(this);

        var level = new TextNode
        {
            Position      = new Vector2(pos.X + 2f + IconSz, pos.Y + 3f),
            Size          = new Vector2(LevelW, 26f),
            FontType      = UiFont.Heading,
            FontSize      = UiFont.Size(23),
            CharSpacing = UiFont.Spacing,
            AlignmentType = AlignmentType.Right,
        };
        level.AttachNode(this);

        var textX = pos.X + 2f + IconSz + LevelW + 4f;
        var textW = Math.Max(0f, pos.X + width - textX);

        var name = new TextNode
        {
            String   = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(job.Name.ExtractText()),
            Position = new Vector2(textX, pos.Y + 2f),
            Size     = new Vector2(textW, 16f),
            FontSize = 14,
        };
        name.AddTextFlags(TextFlags.Ellipsis);
        name.AttachNode(this);

        var pose = new TextNode
        {
            Position = new Vector2(textX, pos.Y + 17f),
            Size     = new Vector2(textW, 13f),
            FontSize = 12,
        };
        pose.AttachNode(this);

        _rows.Add(new Row(job, level, name, pose));
    }

    private void BuildBottomBar(Vector2 pos, Vector2 size)
    {
        new HorizontalLineNode
        {
            Position = new Vector2(pos.X, pos.Y),
            Size     = new Vector2(size.X, 2f),
        }.AttachNode(this);

        var help = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(pos.X + 4f, pos.Y + (size.Y - 22f) / 2f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Help.Title"),
        };
        help.OnClick = Tweak.ToggleHelp;
        help.AttachNode(this);

        var settings = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = new Vector2(pos.X + size.X - 32f, pos.Y + (size.Y - 28f) / 2f),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Tweak.Text("Settings.Title"),
        };
        settings.OnClick = Tweak.ToggleSettings;
        settings.AttachNode(this);
    }

    /// <summary>Level cap of the expansions the account owns (50 for A Realm Reborn, +10 for each expansion).</summary>
    private static int LevelCap()
    {
        var playerState = PlayerState.Instance();
        return playerState != null ? 50 + 10 * playerState->MaxExpansion : int.MaxValue;
    }

    private void Refresh()
    {
        _nextRefresh = DateTime.UtcNow.AddSeconds(1);

        var loaded = Svc.PlayerState.IsLoaded;
        var cap    = LevelCap();
        var saved  = Tweak.SavedPoses;

        foreach (var row in _rows)
        {
            var level    = loaded ? (int)Svc.PlayerState.GetClassJobLevel(row.Job) : 0;
            var unlocked = level > 0;
            row.Level.String    = unlocked ? level.ToString() : "-";
            row.Level.TextColor = !unlocked ? MutedGrey : level >= cap ? LevelMax : White;
            row.Name.TextColor  = unlocked ? White : MutedGrey;

            if (saved.TryGetValue(row.Job.RowId, out var pose))
            {
                row.Pose.String    = string.Format(Tweak.Text("Pose"), pose + 1);
                row.Pose.TextColor = TitleGold;
            }
            else
            {
                row.Pose.String    = Tweak.Text("Default");
                row.Pose.TextColor = MutedGrey;
            }
        }
    }
}
