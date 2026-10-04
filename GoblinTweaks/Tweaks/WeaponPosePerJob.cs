using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GoblinTweaks.Core;
using GoblinTweaks.UI;
using PoseType = FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.PoseType;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Remembers the weapon-drawn pose (/cpose) of every job separately. The game keeps a single
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
        /// <summary>ClassJob row id -> 0-based weapon-drawn pose chosen on that job.</summary>
        public Dictionary<uint, byte> Poses { get; set; } = [];
    }

    private const ushort ChangePoseEmoteId = 90;
    private const int MaxAttempts = 12;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan EmoteInterval = TimeSpan.FromMilliseconds(750);

    private WeaponPoseAddon? _addon;
    private DateTime _nextPoll = DateTime.MinValue;
    private DateTime _nextEmote = DateTime.MinValue;
    private uint _job;
    private bool _restoring;
    private int _attempts;

    protected internal override void Enable()
    {
        _job = 0;
        _restoring = false;
        _addon = new WeaponPoseAddon
        {
            InternalName = "GtkWeaponPoses",
            Title        = T("Title"),
            Size         = new System.Numerics.Vector2(450f, 610f),
            Tweak        = this,
        };

        Svc.Framework.Update += OnUpdate;
    }

    protected internal override void Disable()
    {
        Svc.Framework.Update -= OnUpdate;
        _addon?.Close();
        _addon = null;
    }

    /// <summary>ClassJob row id -> saved 0-based pose, for the native window.</summary>
    internal IReadOnlyDictionary<uint, byte> SavedPoses => Settings.Poses;

    /// <summary>Localized text of this tweak, for the native window.</summary>
    internal string Text(string key) => T(key);

    public override void DrawSettings()
    {
        // Open the native window (icon hints that a separate window opens).
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ExternalLinkAlt, T("Open")))
            _addon?.Open();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("Open.Help"));
    }

    private void OnUpdate(IFramework _)
    {
        if (DateTime.UtcNow < _nextPoll) return;
        _nextPoll = DateTime.UtcNow + PollInterval;

        try   { Refresh(); }
        catch (Exception ex) { ReportFailure(ex); }
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

        var job = Svc.PlayerState.ClassJob.RowId;
        if (job == 0) return;

        var current = playerState->SelectedPoses[(int)PoseType.WeaponDrawn];

        // Job switched (or first poll after login): the saved pose of the new job has to be put back.
        if (job != _job)
        {
            _job = job;
            _restoring = Settings.Poses.ContainsKey(job);
            _attempts = 0;
        }

        if (!_restoring)
        {
            // Whatever pose is selected now is the player's choice for this job.
            if (!Settings.Poses.TryGetValue(job, out var known) || known != current)
            {
                Settings.Poses[job] = current;
                SaveSettings();
            }

            return;
        }

        var saved = Settings.Poses[job];
        if (current == saved)
        {
            _restoring = false;
            return;
        }

        // Change Pose only cycles the weapon-drawn pose while standing with the weapon out.
        if (!player->IsWeaponDrawn || player->InCombat || player->EmoteController.GetPoseKind() != (int)PoseType.WeaponDrawn)
            return;

        if (DateTime.UtcNow < _nextEmote) return;

        // A pose that no longer exists, or one the game refuses to reach: keep the current one instead of retrying forever.
        if (saved > EmoteController.GetAvailablePoses(PoseType.WeaponDrawn) || ++_attempts > MaxAttempts)
        {
            Svc.Log.Warning("{tweak}: could not restore pose {pose} for job {job}", Id, saved, job);
            _restoring = false;
            return;
        }

        _nextEmote = DateTime.UtcNow + EmoteInterval;
        EmoteManager.Instance()->ExecuteEmote(ChangePoseEmoteId);
    }
}
