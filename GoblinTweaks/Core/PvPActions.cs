using System.Text.RegularExpressions;
using Dalamud.Game;
using Lumina.Excel.Sheets;
using GameAction = Lumina.Excel.Sheets.Action;

namespace GoblinTweaks.Core;

/// <summary>
/// The PvP actions of one job that share one cooldown, followed over enemies as one: Drill, Bioblaster, Air Anchor
/// and Chain Saw are one group, each one primed by the one before. <see cref="Id"/> is the one on the hotbar.
/// </summary>
/// <param name="Members">Every action of the group, in the order they come one after another.</param>
/// <param name="ChargeSeconds">For a Limit Break, its gauge charge time from its description; 0 when not given.</param>
internal sealed record PvPAction(
    uint Id, string Name, uint Icon, float Recast, bool IsLimitBreak, byte Job, string Description,
    uint[] Members, uint[] MemberIcons, float ChargeSeconds)
{
    /// <summary>The icon of the action that comes after <paramref name="lastUsed"/> in the group (the first one when none).</summary>
    public uint NextIcon(uint lastUsed)
    {
        var index = Array.IndexOf(Members, lastUsed);
        return MemberIcons[index < 0 ? 0 : (index + 1) % Members.Length];
    }
}

/// <summary>
/// The PvP actions of every job worth following on enemies, read from the game data: one entry per cooldown shared
/// by actions of a job, when it is longer than 5 seconds, and the Limit Breaks.
/// </summary>
internal static partial class PvPActions
{
    // Both rows of the ActionCategory sheet named "Limit Break": PvP ones are 15.
    public static readonly uint[] LimitBreakCategories = [9, 15];

    private const int MinRecast100ms = 50;

    // The cooldown group of the global cooldown: shared by everything, it says nothing.
    private const byte GlobalCooldownGroup = 58;

    /// <summary>
    /// Followed until the player changes it: every Limit Break, and the actions known to put a control or a
    /// debuff on their target. The game data does not say which actions do that, so the list is chosen by hand.
    /// </summary>
    public static readonly uint[] Defaults =
    [
        // Limit Breaks.
        29255, 29662, 29401, 29432, 29497, 29097, 29130, 29415, 29485, 29515, 39215,
        29069, 41498, 29553, 29537, 41502, 29266, 29673, 29678, 39190, 29083, 29230,

        // Controls and debuffs.
        41510, // Lethargy (BLM)
        29395, // Silent Nocturne (BRD)
        29496, // Horrid Roar (DRG)
        29094, // Salted Earth (DRK)
        29409, // Wildfire (MCH)
        41451, // Dokumori (NIN)
        41430, // Shield Smite (PLD)
        29549, // Death Warrant (RPR)
        29535, // Mineuchi (SAM)
        29716, // Chain Stratagem (SCH)
        29233, // Biolysis (SCH)
        29262, // Toxikon (SGE)
        29228, // Miracle of Nature (WHM)
        29080, // Orogeny (WAR)
        29081, // Blota (WAR)
    ];

    private static Dictionary<uint, PvPAction>? _byId;
    private static Dictionary<uint, PvPAction>? _byMember;
    private static Dictionary<byte, List<PvPAction>>? _byJob;
    private static Dictionary<byte, string>? _jobNames;

    /// <summary>The groups, by the id of their hotbar action.</summary>
    public static IReadOnlyDictionary<uint, PvPAction> ById
    {
        get { Load(); return _byId!; }
    }

    /// <summary>The group of any of its actions, by that action's id.</summary>
    public static IReadOnlyDictionary<uint, PvPAction> ByMember
    {
        get { Load(); return _byMember!; }
    }

    /// <summary>The groups of each job (ClassJob row), in the game's order.</summary>
    public static IReadOnlyDictionary<byte, List<PvPAction>> ByJob
    {
        get { Load(); return _byJob!; }
    }

    /// <summary>The name of each job that has actions here, in the game's language.</summary>
    public static IReadOnlyDictionary<byte, string> JobNames
    {
        get { Load(); return _jobNames!; }
    }

    [GeneratedRegex(@"Gauge Charge Time:\s*(\d+)")]
    private static partial Regex ChargeTime();

    private static void Load()
    {
        if (_byId != null) return;

        // An action of one job has a ClassJobCategory named like that job's abbreviation.
        var jobs = new Dictionary<string, (byte Id, string Name)>();
        foreach (var job in Svc.Data.GetExcelSheet<ClassJob>())
        {
            if (job.JobIndex == 0) continue;
            jobs[job.Abbreviation.ToString()] = ((byte)job.RowId, job.Name.ToString());
        }

        var descriptions = Svc.Data.GetExcelSheet<ActionTransient>();
        var english = Svc.Data.GetExcelSheet<ActionTransient>(ClientLanguage.English);

        // Every PvP action of a job, hotbar or not (the actions primed by another are not on the hotbar), by job and
        // cooldown group, in id order: the order they come one after another.
        var grouped = new Dictionary<(byte Job, byte Group), List<GameAction>>();
        foreach (var row in Svc.Data.GetExcelSheet<GameAction>())
        {
            if (!row.IsPvP || row.CooldownGroup == GlobalCooldownGroup) continue;
            if (!row.ClassJobCategory.IsValid || !jobs.TryGetValue(row.ClassJobCategory.Value.Name.ToString(), out var job)) continue;

            var key = (job.Id, row.CooldownGroup);
            if (!grouped.TryGetValue(key, out var list)) grouped[key] = list = [];
            list.Add(row);
        }

        var byId = new Dictionary<uint, PvPAction>();
        var byMember = new Dictionary<uint, PvPAction>();
        var byJob = new Dictionary<byte, List<PvPAction>>();
        var names = new Dictionary<byte, string>();

        foreach (var ((jobId, _), rows) in grouped.OrderBy(pair => pair.Value.Min(row => row.RowId)))
        {
            // The one on the hotbar stands for the group; a group with none is not something a player uses.
            var main = rows.FirstOrDefault(row => row.IsPlayerAction);
            if (main.RowId == 0) continue;

            var isLimitBreak = LimitBreakCategories.Contains(main.ActionCategory.RowId);
            if (!isLimitBreak && main.Recast100ms <= MinRecast100ms) continue;

            var members = rows.Where(row => row.IsPlayerAction || row.Recast100ms == main.Recast100ms).OrderBy(row => row.RowId).ToList();
            var description = descriptions.TryGetRow(main.RowId, out var transient) ? transient.Description.ExtractText() : string.Empty;
            var charge = 0f;
            if (isLimitBreak && english.TryGetRow(main.RowId, out var englishText)
                && ChargeTime().Match(englishText.Description.ExtractText()) is { Success: true } match)
                charge = float.Parse(match.Groups[1].Value);

            var action = new PvPAction(
                main.RowId,
                string.Join(" / ", members.Select(row => row.Name.ToString())),
                main.Icon,
                main.Recast100ms / 10f,
                isLimitBreak,
                jobId,
                description,
                [.. members.Select(row => row.RowId)],
                [.. members.Select(row => (uint)row.Icon)],
                charge);

            byId[action.Id] = action;
            foreach (var member in action.Members) byMember[member] = action;
            if (!byJob.TryGetValue(jobId, out var jobList)) byJob[jobId] = jobList = [];
            jobList.Add(action);
            names[jobId] = jobs.Values.First(job => job.Id == jobId).Name;
        }

        _byJob = byJob;
        _jobNames = names;
        _byMember = byMember;
        _byId = byId;

        var shared = byId.Values.Count(action => action.Members.Length > 1);
        var charges = byId.Values.Count(action => action.ChargeSeconds > 0);
        Svc.Log.Info($"PvPActions: {byId.Count} PvP cooldowns of {byJob.Count} jobs can be followed on enemies ({shared} shared by several actions, {charges} Limit Break charge times read).");
    }
}
