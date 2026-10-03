using System.Text;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using GoblinTweaks.Core;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Adds a Server Info Bar (DTR) entry showing the time until the next retainer returns from a venture.
/// Hover to see all retainers on ventures with their individual countdowns.
/// </summary>
[Tweak(TweakCategory.Interface)]
public sealed class RetainerVentureTimer : Tweak
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

    private void OnUpdate(IFramework _)
    {
        if (_entry == null || DateTime.UtcNow < _nextRefresh) return;
        _nextRefresh = DateTime.UtcNow.AddSeconds(1);

        try   { Refresh(); }
        catch (Exception ex) { ReportFailure(ex); }
    }

    private unsafe void Refresh()
    {
        var retMgr = RetainerManager.Instance();
        if (retMgr == null) { _entry!.Shown = false; return; }

        var ventures = new List<(string Name, uint CompleteAt)>();
        var count    = retMgr->GetRetainerCount();

        for (uint i = 0; i < count && i < 10; i++)
        {
            var ret = retMgr->GetRetainerBySortedIndex(i);
            if (ret == null || !ret->Available || ret->VentureComplete == 0) continue;

            var span = ret->Name;
            var end  = span.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(end >= 0 ? span[..end] : span);
            if (string.IsNullOrWhiteSpace(name)) name = $"Retainer {i + 1}";

            ventures.Add((name, ret->VentureComplete));
        }

        if (ventures.Count == 0) { _entry!.Shown = false; return; }

        ventures.Sort((a, b) => a.CompleteAt.CompareTo(b.CompleteAt));
        _entry!.Shown = true;

        var now     = DateTimeOffset.UtcNow;
        var nearest = DateTimeOffset.FromUnixTimeSeconds(ventures[0].CompleteAt);
        var delta   = nearest - now;

        _entry.Text = new SeStringBuilder()
            .AddText(delta <= TimeSpan.Zero ? $"⚗ {T("Ready")}" : $"⚗ {FormatTime(delta)}")
            .Build();

        var tip = new StringBuilder();
        tip.AppendLine(T("TooltipHeader"));
        foreach (var (name, at) in ventures)
        {
            var r = DateTimeOffset.FromUnixTimeSeconds(at) - now;
            tip.AppendLine($"  {name}: {(r <= TimeSpan.Zero ? T("Ready") : FormatTime(r))}");
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
