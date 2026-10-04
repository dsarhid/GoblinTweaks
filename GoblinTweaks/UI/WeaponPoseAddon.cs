using System.Globalization;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using ClassJobRow = Lumina.Excel.Sheets.ClassJob;

namespace GoblinTweaks.UI;

/// <summary>
/// Native window listing every combat job with its level and the weapon-drawn pose saved for it,
/// laid out like the Classes/Jobs tab of the Character window.
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

    // ClassJob.Role values, and the PrimaryStat that tells physical from magical ranged.
    private const byte RoleTank = 1, RoleMelee = 2, RoleRanged = 3, RoleHealer = 4;
    private const byte StatDexterity = 2;

    private enum Group { Tank, Melee, Healer, PhysicalRanged, MagicalRanged }

    private static readonly Group[][] Columns =
    [
        [Group.Tank, Group.Melee],
        [Group.Healer, Group.PhysicalRanged, Group.MagicalRanged],
    ];

    private sealed record Row(ClassJobRow Job, TextNode Level, TextNode Name, TextNode Pose);

    private readonly List<Row> _rows = [];
    private CircleButtonNode? _helpButton;
    private TextHelpAddon? _helpAddon;
    private DateTime _nextRefresh = DateTime.MinValue;

    public required WeaponPosePerJob Tweak { get; init; }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c       = ContentStartPosition;
        var cs      = ContentSize;
        var columnW = cs.X / Columns.Length;
        var jobs    = JobsByGroup();

        for (var column = 0; column < Columns.Length; column++)
        {
            var x = c.X + column * columnW;
            var y = c.Y;
            foreach (var group in Columns[column])
            {
                if (!jobs.TryGetValue(group, out var groupJobs)) continue;

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
        _helpButton = null;

        _helpAddon?.Close();
        _helpAddon = null;

        base.OnFinalize(addon);
    }

    /// <summary>Combat jobs by role, in the game's own order. Base classes only appear if a pose was saved on them.</summary>
    private Dictionary<Group, List<ClassJobRow>> JobsByGroup()
    {
        var sheet   = Svc.Data.GetExcelSheet<ClassJobRow>();
        var classes = sheet.Where(job => job.ClassJobParent.RowId != job.RowId).Select(job => job.ClassJobParent.RowId).ToHashSet();

        var result = new Dictionary<Group, List<ClassJobRow>>();
        foreach (var job in sheet.OrderBy(job => job.UIPriority))
        {
            if (classes.Contains(job.RowId) && !Tweak.SavedPoses.ContainsKey(job.RowId)) continue;

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
            FontType      = FontType.TrumpGothic,
            FontSize      = 23,
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

        _helpButton = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(pos.X + 4f, pos.Y + (size.Y - 22f) / 2f),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Help.Title"),
        };
        _helpButton.OnClick = OpenHelp;
        _helpButton.AttachNode(this);
    }

    private void OpenHelp()
    {
        _helpAddon ??= new TextHelpAddon
        {
            InternalName = "GtkWeaponPoseHelp",
            Title        = Tweak.Text("Help.Title"),
            Size         = new Vector2(440f, 300f),
            Pages        = [(Tweak.Text("Help.Title"), Tweak.Text("Help.Text"))],
        };
        _helpAddon.Open();
    }

    private void Refresh()
    {
        _nextRefresh = DateTime.UtcNow.AddSeconds(1);

        var loaded   = Svc.PlayerState.IsLoaded;
        var levels   = _rows.Select(row => loaded ? (int)Svc.PlayerState.GetClassJobLevel(row.Job) : 0).ToArray();
        var maxLevel = levels.DefaultIfEmpty(0).Max();

        foreach (var (row, level) in _rows.Zip(levels))
        {
            var unlocked = level > 0;
            row.Level.String    = level.ToString();
            row.Level.TextColor = !unlocked ? MutedGrey : level == maxLevel ? LevelMax : White;
            row.Name.TextColor  = unlocked ? White : MutedGrey;

            if (Tweak.SavedPoses.TryGetValue(row.Job.RowId, out var pose))
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
