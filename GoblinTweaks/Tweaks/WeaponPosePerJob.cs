using GoblinTweaks.Localization;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GoblinTweaks.Core;
using GoblinTweaks.UI;
using PoseType = FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.PoseType;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Remembers the weapon-drawn pose (/cpose) of every job separately, per character. The game keeps a single
/// weapon-drawn pose for all jobs; this tweak saves the one chosen on each job and puts it back
/// after switching to that job.
/// </summary>
/// <remarks>
/// The pose is restored by using the Change Pose emote, exactly as the player would, so the server
/// and other players see the same pose. No game memory is written.
/// </remarks>
[Tweak(TweakCategory.Other)]
public sealed unsafe class WeaponPosePerJob : Tweak<WeaponPosePerJob.Options>
{
    public sealed class Options
    {
        /// <summary>Before poses were kept per character: ClassJob row id -> pose. Moved to the first character that logs in.</summary>
        public Dictionary<uint, byte>? Poses { get; set; }

        /// <summary>Character (content id) -> ClassJob row id -> 0-based weapon-drawn pose chosen on that job.</summary>
        public Dictionary<ulong, Dictionary<uint, byte>> Characters { get; set; } = [];

        /// <summary>The window lists only the jobs the character has unlocked.</summary>
        public bool OnlyUnlocked { get; set; }
    }

    private const ushort ChangePoseEmoteId = 90;
    private const int MaxAttempts = 12;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EmoteInterval = TimeSpan.FromMilliseconds(750);

    // How long a pose change after one of our own Change Pose emotes is taken as ours, not the player's.
    private static readonly TimeSpan OwnChangeWindow = TimeSpan.FromSeconds(2);

    private static readonly IReadOnlyDictionary<uint, byte> NoPoses = new Dictionary<uint, byte>();

    private WeaponPoseAddon? _addon;
    private WeaponPoseSettingsAddon? _settingsAddon;
    private TextHelpAddon? _helpAddon;

    private DateTime _nextPoll = DateTime.MinValue;
    private DateTime _nextEmote = DateTime.MinValue;
    private DateTime _ownChangeUntil = DateTime.MinValue;
    private ulong _character;
    private uint _job;
    private bool _restoring;
    private int _attempts;
    private byte _lastSeen;
    private bool _couldPose;

    // Pose left in place after giving up a restore (or forgetting the current job): not taken as the player's
    // choice until the player changes it, so the saved pose is not overwritten by it.
    private byte? _hold;

    protected internal override void Enable()
    {
        _character = 0;
        _job = 0;
        _restoring = false;
        _hold = null;
        _addon = NewAddon();
        _settingsAddon = NewSettingsAddon();

        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;

        _helpAddon?.Dispose();
        _helpAddon = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;
        _addon?.Dispose();
        _addon = null;
    }

    // ── Access for the native windows ─────────────────────────────────────────

    /// <summary>ClassJob row id -> saved 0-based pose of the character logged in now (empty when none is).</summary>
    internal IReadOnlyDictionary<uint, byte> SavedPoses
        => _character != 0 && Settings.Characters.TryGetValue(_character, out var poses) ? poses : NoPoses;

    /// <summary>Whether a character is logged in, so its poses are known.</summary>
    internal bool HasCharacter => _character != 0;

    internal bool OnlyUnlocked
    {
        get => Settings.OnlyUnlocked;
        set
        {
            Settings.OnlyUnlocked = value;
            SaveSettings();
        }
    }

    /// <summary>Localized text of this tweak, for the native windows.</summary>
    internal string Text(string key) => T(key);

    /// <summary>Forgets the pose saved for a job of the current character; the job goes back to "Default".</summary>
    internal void Forget(uint job)
    {
        if (_character == 0 || !Settings.Characters.TryGetValue(_character, out var poses) || !poses.Remove(job)) return;
        if (job == _job) HoldCurrentPose();
        SaveSettings();
    }

    /// <summary>Forgets every pose saved for the current character.</summary>
    internal void ForgetAll()
    {
        if (_character == 0 || !Settings.Characters.TryGetValue(_character, out var poses) || poses.Count == 0) return;
        poses.Clear();
        HoldCurrentPose();
        SaveSettings();
    }

    // The pose on now stays as it is, but is not saved again for the current job until the player changes it.
    private void HoldCurrentPose()
    {
        var playerState = PlayerState.Instance();
        _restoring = false;
        _hold = playerState != null ? playerState->SelectedPoses[(int)PoseType.WeaponDrawn] : null;
    }

    // ── Main window buttons and windows ───────────────────────────────────────

    public override TweakButton? OpenButton => new(TMain("Open"), TMain("Open.Help"), ToggleWindow);

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelp);

    public override IReadOnlyList<TweakButton> Buttons =>
    [
        new(Loc.Get("Window.Settings"), TMain("Settings.Help"), ToggleSettings),
    ];

    protected override IReadOnlyList<string> CommandNames => ["/gwp", "/gweapon"];

    protected override void OnCommand() => ToggleWindow();

    private void ToggleWindow()
    {
        if (_addon is { IsOpen: true }) { _addon.Close(); return; }

        // The jobs listed depend on the character and its saved poses: build the window again if they changed.
        if (_addon is null || _addon.LayoutKey != WeaponPoseAddon.KeyOf(CurrentLayout()))
        {
            _addon?.Dispose();
            _addon = NewAddon();
        }
        _addon.Open();
    }

    /// <summary>Opens the settings window, or closes it if it is open.</summary>
    internal void ToggleSettings()
    {
        if (_settingsAddon is not { } window) return;
        if (window.IsOpen) window.Close(); else window.Open();
    }

    /// <summary>Opens the help, or closes it if it is open. It belongs to the tweak, so it is closed with it.</summary>
    internal void ToggleHelp()
    {
        _helpAddon ??= new TextHelpAddon
        {
            InternalName    = "GtkWeaponPoseHelp",
            Title           = T("Help.Title"),
            Size            = new System.Numerics.Vector2(440f, 360f),
            RespectCloseAll = false,
            Pages           = [(T("Help.Title"), T("Help.Text"))],
        };
        _helpAddon.Toggle();
    }

    private Dictionary<WeaponPoseAddon.Group, List<Lumina.Excel.Sheets.ClassJob>> CurrentLayout()
        => WeaponPoseAddon.JobsByGroup(SavedPoses, Settings.OnlyUnlocked && Svc.PlayerState.IsLoaded);

    private WeaponPoseAddon NewAddon()
    {
        var layout = CurrentLayout();
        return new WeaponPoseAddon
        {
            InternalName = "GtkWeaponPoses",
            Title        = T("Title"),
            Size         = WeaponPoseAddon.SizeFor(layout),
            Tweak        = this,
            Jobs         = layout,
        };
    }

    private WeaponPoseSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkWeaponPoseSettings",
        Title           = $"{T("Title")} — {Loc.GetIn(UiLanguage, "Window.Settings")}",
        Size            = new System.Numerics.Vector2(460f, 520f),
        Tweak           = this,
        RespectCloseAll = false,
    };

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;

        Rebuild(ref _addon, NewAddon);
        Rebuild(ref _settingsAddon, NewSettingsAddon);

        var helpWasOpen = _helpAddon is { IsOpen: true };
        _helpAddon?.Dispose();
        _helpAddon = null;
        if (helpWasOpen) Svc.Framework.RunOnTick(ToggleHelp, delayTicks: 6);
    }

    protected override void OnSettingsChanged() => _settingsAddon?.PosesChanged();

    // ── Watching and restoring the pose ───────────────────────────────────────

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < _nextPoll) return;
        _nextPoll = DateTime.UtcNow + PollInterval;

        try
        {
            Refresh();
            KeepWindowLayout();
        }
        catch (Exception ex) { ReportFailure(ex); }
    }

    /// <summary>While the window is open, builds it again when the jobs it should list change (a class got a pose, another character...).</summary>
    private void KeepWindowLayout()
    {
        if (_addon is not { IsOpen: true } addon) return;
        if (addon.LayoutKey != WeaponPoseAddon.KeyOf(CurrentLayout()))
            Rebuild(ref _addon, NewAddon);
    }

    private void Refresh()
    {
        var playerState = PlayerState.Instance();
        var player = Control.GetLocalPlayer();
        if (playerState == null || player == null || !Svc.PlayerState.IsLoaded)
        {
            _job = 0;
            return;
        }

        var character = Svc.PlayerState.ContentId;
        if (character == 0) return;
        if (character != _character)
        {
            _character = character;
            _job = 0;
            MoveSharedPoses(character);
            _settingsAddon?.PosesChanged();
        }

        if (!Settings.Characters.TryGetValue(character, out var poses))
            Settings.Characters[character] = poses = [];

        var job = Svc.PlayerState.ClassJob.RowId;
        if (job == 0) return;

        var current = playerState->SelectedPoses[(int)PoseType.WeaponDrawn];

        // Change Pose only cycles the weapon-drawn pose while standing with the weapon out.
        var canPose = player->IsWeaponDrawn && !player->InCombat && player->EmoteController.GetPoseKind() == (int)PoseType.WeaponDrawn;

        // Job switched (or first poll after login): the saved pose of the new job has to be put back.
        if (job != _job)
        {
            _job = job;
            _restoring = poses.ContainsKey(job);
            _attempts = 0;
            _hold = null;
            _lastSeen = current;
            _couldPose = canPose;
        }

        // The pose changed with the weapon out and not right after one of our emotes: the player is choosing one,
        // so stop putting the saved one back and let theirs be saved.
        var changedByPlayer = current != _lastSeen && canPose && _couldPose && DateTime.UtcNow > _ownChangeUntil;
        _lastSeen = current;
        _couldPose = canPose;
        if (changedByPlayer)
        {
            _restoring = false;
            _hold = null;
        }

        if (!_restoring)
        {
            if (_hold is { } held)
            {
                if (current == held) return;
                _hold = null;
            }

            // Whatever pose is selected now is the player's choice for this job.
            if (!poses.TryGetValue(job, out var known) || known != current)
            {
                poses[job] = current;
                SaveSettings();
            }

            return;
        }

        var saved = poses[job];
        if (current == saved)
        {
            _restoring = false;
            return;
        }

        if (!canPose || DateTime.UtcNow < _nextEmote) return;

        // A pose that no longer exists, or one the game refuses to reach: leave the current one, without saving it over the saved one.
        if (saved > EmoteController.GetAvailablePoses(PoseType.WeaponDrawn) || ++_attempts > MaxAttempts)
        {
            Svc.Log.Warning("{tweak}: could not restore pose {pose} for job {job}", Id, saved, job);
            _restoring = false;
            _hold = current;
            return;
        }

        _nextEmote = DateTime.UtcNow + EmoteInterval;
        _ownChangeUntil = DateTime.UtcNow + OwnChangeWindow;
        EmoteManager.Instance()->ExecuteEmote(ChangePoseEmoteId);
    }

    /// <summary>Earlier versions kept one set of poses for every character: it goes to the first character that logs in.</summary>
    private void MoveSharedPoses(ulong character)
    {
        if (Settings.Poses is not { } shared) return;

        if (shared.Count > 0 && !Settings.Characters.ContainsKey(character))
            Settings.Characters[character] = new Dictionary<uint, byte>(shared);

        Settings.Poses = null;
        SaveSettings(notify: false);
    }
}
