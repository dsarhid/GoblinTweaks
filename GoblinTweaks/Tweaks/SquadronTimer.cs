using System.Text;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Adds a Server Info Bar (DTR) entry showing the time until the next squadron mission or
/// training session completes. Hover to see both timers individually.
/// </summary>
[Tweak(TweakCategory.Interface)]
public sealed class SquadronTimer : Tweak
{
    private IDtrBarEntry? _entry;
    private DateTime      _nextRefresh = DateTime.MinValue;

    protected internal override void Enable()
    {
        _entry       = Svc.DtrBar.Get(T("DtrTitle"));
        _entry.Shown = false;
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        _entry?.Remove();
        _entry = null;
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

    protected override IReadOnlyList<string> CommandNames => ["/gst", "/gsquadron"];

    protected override void OnCommand() => ToggleHelpWindow();

    private void OnUpdate(IFramework _)
    {
        if (_entry == null || DateTime.UtcNow < _nextRefresh) return;
        _nextRefresh = DateTime.UtcNow.AddSeconds(1);

        try   { Refresh(); }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private unsafe void Refresh()
    {
        var ps = PlayerState.Instance();
        if (ps == null || !Svc.PlayerState.IsLoaded) { _entry!.Shown = false; return; }

        var missionTs  = ps->SquadronMissionCompletionTimestamp;
        var trainingTs = ps->SquadronTrainingCompletionTimestamp;

        var hasMission  = missionTs  > 0;
        var hasTraining = trainingTs > 0;

        if (!hasMission && !hasTraining) { _entry!.Shown = false; return; }

        _entry!.Shown = true;
        var now = DateTimeOffset.UtcNow;

        // Determine the nearest completion for the bar label.
        var candidates = new List<(string Label, TimeSpan Remaining)>();
        if (hasMission)
        {
            var r = DateTimeOffset.FromUnixTimeSeconds(missionTs) - now;
            candidates.Add((T("Mission"), r));
        }
        if (hasTraining)
        {
            var r = DateTimeOffset.FromUnixTimeSeconds(trainingTs) - now;
            candidates.Add((T("Training"), r));
        }

        candidates.Sort((a, b) => a.Remaining.CompareTo(b.Remaining));
        var nearest = candidates[0];

        var barText = nearest.Remaining <= TimeSpan.Zero
            ? $"⚔ {T("Ready")}"
            : $"⚔ {FormatTime(nearest.Remaining)}";

        _entry.Text = new SeStringBuilder().AddText(barText).Build();

        var tip = new StringBuilder();
        tip.AppendLine(T("TooltipHeader"));
        foreach (var (label, remaining) in candidates)
        {
            var status = remaining <= TimeSpan.Zero ? T("Ready") : FormatTime(remaining);
            tip.AppendLine($"  {label}: {status}");
        }

        _entry.Tooltip = new SeStringBuilder().AddText(tip.ToString().TrimEnd()).Build();
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : t.TotalMinutes >= 1
                ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s"
                : $"{t.Seconds}s";
}
