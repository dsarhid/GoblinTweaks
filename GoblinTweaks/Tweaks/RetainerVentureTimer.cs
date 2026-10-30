using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Adds a Server Info Bar (DTR) entry showing how many retainers are back from their venture and the time until
/// the next one returns. Hover to see all retainers on ventures with their individual countdowns. When a venture
/// finishes while the game is running, says so in the chat and plays a sound.
/// </summary>
/// <remarks>
/// The game only knows the retainers once the server has sent them: at a summoning bell, or when its Timers window
/// asks for them. Until then the list is empty, so after logging in the tweak asks the same way the Timers window does,
/// and meanwhile shows the ventures it last saw for the character.
/// </remarks>
[Tweak(TweakCategory.Interface)]
public sealed class RetainerVentureTimer : Tweak<RetainerVentureTimer.Options>
{
    public sealed class Options
    {
        /// <summary>Print a chat message when a retainer comes back from a venture.</summary>
        public bool ChatAlert { get; set; } = true;

        /// <summary>Play a sound when a retainer comes back from a venture.</summary>
        public bool SoundAlert { get; set; } = true;

        /// <summary>
        /// The ventures of each character (by content id) as last seen, shown after logging in until the server sends
        /// the retainers again.
        /// </summary>
        public Dictionary<ulong, List<SavedVenture>> Characters { get; set; } = [];
    }

    /// <summary>A retainer on a venture, as last seen. <see cref="CompleteAt"/> is a Unix timestamp.</summary>
    public sealed record SavedVenture(ulong RetainerId, string Name, uint CompleteAt);

    // Fixed, not translated: Dalamud remembers whether an entry is hidden and where it goes by its title.
    private const string DtrTitle = "GoblinTweaks: Ventures";

    // The <se.6> chat sound effect.
    private const uint AlertSound = 6;

    // After logging in, a moment for the game to settle; then a few tries, spaced out, while no retainer is known.
    private static readonly TimeSpan FirstRequestDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestRetry      = TimeSpan.FromMinutes(1);
    private const int MaxRequests = 3;

    private static readonly string Clock = SeIconChar.Clock.ToIconString();

    private IDtrBarEntry?        _entry;
    private SimpleSettingsAddon? _settingsAddon;
    private DateTime             _nextRefresh = DateTime.MinValue;
    private ulong         _requestedFor;
    private int           _requests;
    private DateTime      _nextRequest = DateTime.MinValue;
    private string?       _shownText;
    private string?       _shownTooltip;

    // Character whose retainers are being followed, and the venture end of each retainer last seen still away:
    // an alert is due when that same end is reached, not for ventures already done when first seen.
    private ulong _character;
    private readonly Dictionary<ulong, uint> _away = [];

    protected internal override void Enable()
    {
        _entry        = Svc.DtrBar.Get(DtrTitle);
        _entry.Shown  = false;
        _requestedFor = 0;
        _shownText    = null;
        _shownTooltip = null;
        _character    = 0;
        _away.Clear();
        _settingsAddon = NewSettingsAddon();
        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        _entry?.Remove();
        _entry = null;
        _away.Clear();
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
        InternalName    = "GtkVentureSettings",
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
        if (retMgr == null || !Svc.PlayerState.IsLoaded)
        {
            // Logged out: the game forgets the retainers, so the next login asks for them again, same character or not.
            _requestedFor = 0;
            _character    = 0;
            _away.Clear();
            Hide();
            return;
        }

        // Another character: what was followed belongs to the previous one.
        var character = Svc.PlayerState.ContentId;
        if (character != _character)
        {
            _character = character;
            _away.Clear();
        }

        var count = retMgr->GetRetainerCount();
        if (count == 0) RequestRetainers(retMgr);

        // Until the server sends the retainers, the ventures last seen for this character stand in for them.
        var ventures = count > 0 ? LiveVentures(retMgr, count) : SavedVentures(character);
        if (count > 0) SaveVentures(character, ventures);

        var nowUnix  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seen     = new HashSet<ulong>();
        var finished = new List<string>();

        foreach (var (id, name, at) in ventures)
        {
            seen.Add(id);

            if (at > nowUnix) _away[id] = at;
            else if (_away.TryGetValue(id, out var expected) && expected == at)
            {
                _away.Remove(id);
                finished.Add(name);
            }
        }

        // Retainers no longer on a venture (collected, dismissed) stop being followed.
        foreach (var id in _away.Keys.Where(id => !seen.Contains(id)).ToList())
            _away.Remove(id);

        if (finished.Count > 0) Alert(finished);

        if (ventures.Count == 0) { Hide(); return; }

        ventures = [.. ventures.OrderBy(v => v.CompleteAt)];

        var ready   = ventures.Count(v => v.CompleteAt <= nowUnix);
        // None left when every retainer is back.
        var next    = ventures.FirstOrDefault(v => v.CompleteAt > nowUnix);
        var nextIn  = next is not null ? FormatTime(TimeSpan.FromSeconds(next.CompleteAt - nowUnix)) : null;
        var readyTx = ready > 0 ? string.Format(T("ReadyCount"), ready) : null;

        var text = $"{Clock} {T("Short")} " + (readyTx, nextIn) switch
        {
            ({ } r, { } n) => $"{r} · {n}",
            ({ } r, null)  => r,
            _              => nextIn!,
        };

        var tip = new StringBuilder(T("TooltipHeader"));
        foreach (var (_, name, at) in ventures)
            tip.Append($"\n  {name}: {(at <= nowUnix ? T("Ready") : FormatTime(TimeSpan.FromSeconds(at - nowUnix)))}");
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

    /// <summary>The retainers on a venture, as the game has them now.</summary>
    private unsafe List<SavedVenture> LiveVentures(RetainerManager* retMgr, uint count)
    {
        var ventures = new List<SavedVenture>();
        for (uint i = 0; i < count && i < 10; i++)
        {
            var ret = retMgr->GetRetainerBySortedIndex(i);
            if (ret == null || !ret->Available || ret->VentureComplete == 0) continue;

            var span = ret->Name;
            var end  = span.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(end >= 0 ? span[..end] : span);
            if (string.IsNullOrWhiteSpace(name)) name = string.Format(T("Unnamed"), i + 1);

            ventures.Add(new SavedVenture(ret->RetainerId, name, ret->VentureComplete));
        }

        return ventures;
    }

    private List<SavedVenture> SavedVentures(ulong character)
        => Settings.Characters.TryGetValue(character, out var saved) ? saved : [];

    /// <summary>Keeps the ventures of the character for the next login. Written only when they change.</summary>
    private void SaveVentures(ulong character, List<SavedVenture> ventures)
    {
        if (character == 0) return;
        if (Settings.Characters.TryGetValue(character, out var saved) && saved.SequenceEqual(ventures)) return;

        Settings.Characters[character] = ventures;
        SaveSettings(notify: false);
    }

    private void Hide()
    {
        if (_entry is { Shown: true }) _entry.Shown = false;
    }

    /// <summary>One chat line per retainer that came back, and one sound for all of them.</summary>
    private void Alert(List<string> names)
    {
        if (Settings.ChatAlert)
            foreach (var name in names)
                Svc.Chat.Print(string.Format(T("Chat.Ready"), name), Name);

        if (Settings.SoundAlert) UIGlobals.PlayChatSoundEffect(AlertSound);
    }

    /// <summary>
    /// Asks the server for the retainers, as the game's Timers window does; the game itself takes in the answer.
    /// A few times per character at most: one with no retainers would otherwise ask for ever.
    /// </summary>
    private unsafe void RequestRetainers(RetainerManager* retainers)
    {
        if (Control.GetLocalPlayer() == null) return;
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
        Svc.Log.Debug($"RetainerVentureTimer: asking the server for the retainers ({_requests}/{MaxRequests}).");
        retainers->RequestVenturesTimers();
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : t.TotalMinutes >= 1
                ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s"
                : $"{t.Seconds}s";
}
