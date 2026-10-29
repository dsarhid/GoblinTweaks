using System.Text;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Adds a Server Info Bar (DTR) entry showing the time until the next squadron mission or
/// training session completes. Hover to see both timers individually. When one finishes while
/// the game is running, says so in the chat and plays a sound.
/// </summary>
[Tweak(TweakCategory.Interface)]
public sealed class SquadronTimer : Tweak<SquadronTimer.Options>
{
    public sealed class Options
    {
        /// <summary>Print a chat message when a mission or training finishes.</summary>
        public bool ChatAlert { get; set; } = true;

        /// <summary>Play a sound when a mission or training finishes.</summary>
        public bool SoundAlert { get; set; } = true;
    }

    // Fixed, not translated: Dalamud remembers whether an entry is hidden and where it goes by its title.
    private const string DtrTitle = "GoblinTweaks: Squadron";

    // The <se.6> chat sound effect.
    private const uint AlertSound = 6;

    private static readonly string Clock = SeIconChar.Clock.ToIconString();

    private IDtrBarEntry?        _entry;
    private SimpleSettingsAddon? _settingsAddon;
    private DateTime             _nextRefresh = DateTime.MinValue;
    private string?       _shownText;
    private string?       _shownTooltip;

    // Completion timestamp of the mission / training last seen still running (0 = none):
    // an alert is due when that same timestamp is reached, not for ones already done at login.
    private int _runningMission;
    private int _runningTraining;

    protected internal override void Enable()
    {
        _entry        = Svc.DtrBar.Get(DtrTitle);
        _entry.Shown  = false;
        _shownText    = null;
        _shownTooltip = null;
        _runningMission  = 0;
        _runningTraining = 0;
        _settingsAddon   = NewSettingsAddon();
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        _entry?.Remove();
        _entry = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;
    }

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelpWindow);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(Loc.Get("Window.Settings"), TMain("Settings.Help"), ToggleSettings),
    ];

    private void ToggleSettings()
    {
        if (_settingsAddon is not { } window) return;
        if (window.IsOpen) window.Close(); else window.Open();
    }

    private SimpleSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkSquadronSettings",
        Title           = $"{T("Name")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size            = new System.Numerics.Vector2(560f, 300f),
        RespectCloseAll = false,
        Tweak           = this,
        Heading         = T("Settings.Alerts"),
        Toggles         = () =>
        [
            new(T("ChatAlert"), T("ChatAlert.Help"), () => Settings.ChatAlert, on =>
            {
                Settings.ChatAlert = on;
                SaveSettings();
            }),
            new(T("SoundAlert"), T("SoundAlert.Help"), () => Settings.SoundAlert, on =>
            {
                Settings.SoundAlert = on;
                SaveSettings();
            }),
        ],
    };

    protected override void OnLanguageChanged()
    {
        if (State == TweakState.Enabled) Rebuild(ref _settingsAddon, NewSettingsAddon);
    }

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
        if (ps == null || !Svc.PlayerState.IsLoaded)
        {
            _runningMission  = 0;
            _runningTraining = 0;
            Hide();
            return;
        }

        var missionTs  = ps->SquadronMissionCompletionTimestamp;
        var trainingTs = ps->SquadronTrainingCompletionTimestamp;
        var nowUnix    = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        CheckFinished(missionTs,  ref _runningMission,  "Chat.MissionReady",  nowUnix);
        CheckFinished(trainingTs, ref _runningTraining, "Chat.TrainingReady", nowUnix);

        // Shortest remaining first: that one goes on the bar.
        var timers = new List<(string Label, string Short, TimeSpan Remaining)>(2);
        if (missionTs > 0)  timers.Add((T("Mission"),  T("MissionShort"),  TimeSpan.FromSeconds(missionTs  - nowUnix)));
        if (trainingTs > 0) timers.Add((T("Training"), T("TrainingShort"), TimeSpan.FromSeconds(trainingTs - nowUnix)));

        if (timers.Count == 0) { Hide(); return; }

        timers.Sort((a, b) => a.Remaining.CompareTo(b.Remaining));
        var nearest = timers[0];

        var text = $"{Clock} {nearest.Short} {Status(nearest.Remaining)}";

        var tip = new StringBuilder(T("TooltipHeader"));
        foreach (var (label, _, remaining) in timers)
            tip.Append($"\n  {label}: {Status(remaining)}");
        var tooltip = tip.ToString();

        _entry!.Shown = true;

        // Dalamud redraws the entry on every assignment: only touch it when the text really changed.
        if (text != _shownText)
        {
            _entry.Text = new SeStringBuilder().AddText(text).Build();
            _shownText  = text;
        }
        if (tooltip != _shownTooltip)
        {
            _entry.Tooltip = new SeStringBuilder().AddText(tooltip).Build();
            _shownTooltip  = tooltip;
        }
    }

    private void Hide()
    {
        if (_entry is { Shown: true }) _entry.Shown = false;
    }

    /// <summary>Alerts once when a mission or training seen running reaches its completion time.</summary>
    private void CheckFinished(int timestamp, ref int running, string chatKey, long nowUnix)
    {
        if (timestamp <= 0) { running = 0; return; }
        if (timestamp > nowUnix) { running = timestamp; return; }
        if (running != timestamp) return; // already done when first seen (e.g. at login): no alert

        running = 0;
        if (Settings.ChatAlert)  Svc.Chat.Print(T(chatKey), Name);
        if (Settings.SoundAlert) UIGlobals.PlayChatSoundEffect(AlertSound);
    }

    private string Status(TimeSpan remaining) => remaining <= TimeSpan.Zero ? T("Ready") : FormatTime(remaining);

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : t.TotalMinutes >= 1
                ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s"
                : $"{t.Seconds}s";
}
