using Lumina.Excel.Sheets;

namespace GoblinTweaks.Glamour.Game;

/// <summary>Names from the game data, in the client language, for the Goblin Glamour windows.</summary>
internal static class GlamourNames
{
    /// <summary>"Au Ra · DRK": the character's race and job.</summary>
    public static string Who(CharacterInfo who)
    {
        var race = Svc.Data.GetExcelSheet<Race>().GetRowOrDefault((uint)who.Race) is { } row
            ? (who.Male ? row.Masculine : row.Feminine).ExtractText()
            : who.RaceName;
        var job = Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(who.JobId) is { } classJob
            ? classJob.Abbreviation.ExtractText()
            : who.JobName;
        return $"{race} · {job}";
    }
}
