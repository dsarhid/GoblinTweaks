using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Adds a Server Info Bar (DTR) entry showing the time until the next retainer returns from a venture.
/// Hover to see all retainers on ventures with their individual countdowns.
/// </summary>
/// <remarks>
/// The game only knows the retainers once the server has sent them: at a summoning bell, or when its Timers window
/// asks for them. Until then the list is empty, so after logging in the tweak asks the same way the Timers window does.
/// </remarks>
[Tweak(TweakCategory.Interface)]
public sealed class RetainerVentureTimer : Tweak
{
    // After logging in, a moment for the game to settle; then a few tries, spaced out, while no retainer is known.
    private static readonly TimeSpan FirstRequestDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestRetry      = TimeSpan.FromMinutes(1);
    private const int MaxRequests = 3;

    private IDtrBarEntry? _entry;
    private DateTime      _nextRefresh = DateTime.MinValue;
    private ulong         _requestedFor;
    private int           _requests;
    private DateTime      _nextRequest = DateTime.MinValue;

    protected internal override void Enable()
    {
        _entry       = Svc.DtrBar.Get(T("DtrTitle"));
        _entry.Shown = false;
        _requestedFor = 0;
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        _entry?.Remove();
        _entry = null;
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

    protected override IReadOnlyList<string> CommandNames => ["/grvt", "/gretimer"];

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
        var retMgr = RetainerManager.Instance();
        if (retMgr == null) { _entry!.Shown = false; return; }

        var count = retMgr->GetRetainerCount();
        if (count == 0) RequestRetainers(retMgr);

        var ventures = new List<(string Name, uint CompleteAt)>();

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

    /// <summary>
    /// Asks the server for the retainers, as the game's Timers window does; the game itself takes in the answer.
    /// A few times per character at most: one with no retainers would otherwise ask for ever.
    /// </summary>
    private unsafe void RequestRetainers(RetainerManager* retainers)
    {
        if (!Svc.PlayerState.IsLoaded || Control.GetLocalPlayer() == null) return;
        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return;

        var character = Svc.PlayerState.ContentId;
        if (character != _requestedFor)
        {
            _requestedFor = character;
            _requests     = 0;
            _nextRequest  = DateTime.UtcNow + FirstRequestDelay;
        }

        if (_requests >= MaxRequests || DateTime.UtcNow < _nextRequest) return;

        _requests++;
        _nextRequest = DateTime.UtcNow + RequestRetry;
        retainers->RequestVenturesTimers();
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : t.TotalMinutes >= 1
                ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s"
                : $"{t.Seconds}s";
}
