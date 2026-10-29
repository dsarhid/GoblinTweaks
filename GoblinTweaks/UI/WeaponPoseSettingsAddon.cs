using System.Globalization;
using System.Numerics;
using GoblinTweaks.Tweaks;
using KamiToolKit.Nodes;
using ClassJobRow = Lumina.Excel.Sheets.ClassJob;

namespace GoblinTweaks.UI;

/// <summary>
/// Native settings window of Weapon pose per job: language of its windows, which jobs the window lists,
/// and the poses saved for the character logged in, each of which can be forgotten.
/// </summary>
internal sealed class WeaponPoseSettingsAddon : SettingsPanelAddon
{
    private const float ButtonW = 110f;

    public WeaponPosePerJob? Tweak { get; init; }

    protected override float HeaderHeight => 40f;

    /// <summary>The saved poses changed: show them again (on the next frame, outside any click).</summary>
    internal void PosesChanged()
    {
        if (IsOpen) RequestRebuild();
    }

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;

        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 6f));
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var tweak = Tweak;

        Section(tweak.Text("Settings.Window"));

        Toggle(tweak.Text("OnlyUnlocked"), tweak.Text("OnlyUnlocked.Help"), tweak.OnlyUnlocked,
            on => tweak.OnlyUnlocked = on);

        Section(tweak.Text("Settings.Saved"));

        if (!tweak.HasCharacter)
        {
            Note(tweak.Text("Settings.NoCharacter"), Pad, ContentW, Muted);
            return;
        }

        var saved = tweak.SavedPoses;
        if (saved.Count == 0)
        {
            Note(tweak.Text("Settings.NoneSaved"), Pad, ContentW, Muted);
            return;
        }

        Note(tweak.Text("Settings.Saved.Help"), Pad, ContentW, Muted);
        Space(6f);

        var sheet = Svc.Data.GetExcelSheet<ClassJobRow>();
        foreach (var (jobId, pose) in saved.OrderBy(entry => sheet.GetRowOrDefault(entry.Key)?.UIPriority ?? 0u).ToList())
        {
            var name = sheet.GetRowOrDefault(jobId) is { } job
                ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(job.Name.ExtractText())
                : jobId.ToString();

            SavedRow(name, string.Format(tweak.Text("Pose"), pose + 1), () => tweak.Forget(jobId));
        }

        Space(8f);
        Button(tweak.Text("Settings.ForgetAll"), Pad, 180f, tweak.ForgetAll);
        Y += 36f;
    }

    /// <summary>A job with its saved pose and a button to forget it.</summary>
    private void SavedRow(string job, string pose, Action forget)
    {
        new TextNode
        {
            String   = job,
            Position = new Vector2(Pad, Y + 4f),
            Size     = new Vector2(ContentW - ButtonW - 120f, 20f),
            TextColor = White,
            FontSize = 14,
        }.AttachNode(Host);

        new TextNode
        {
            String    = pose,
            Position  = new Vector2(Pad + ContentW - ButtonW - 110f, Y + 5f),
            Size      = new Vector2(100f, 20f),
            TextColor = Gold,
            FontSize  = 13,
        }.AttachNode(Host);

        Button(Tweak!.Text("Settings.Forget"), Pad + ContentW - ButtonW, ButtonW, forget);
        Y += 32f;
    }

    private void Button(string label, float x, float width, Action click)
    {
        var button = new TextButtonNode
        {
            String   = label,
            Position = new Vector2(x, Y),
            Size     = new Vector2(width, 28f),
        };
        button.OnClick = click;
        button.AttachNode(Host);
    }
}
