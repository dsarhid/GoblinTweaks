using System.Numerics;
using GoblinTweaks.Glamour;
using GoblinTweaks.Tweaks;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// The AI requests of Goblin Glamour, newest first: when, what kind (create or judge), what was asked, which provider
/// and model, and whether it answered. A create request can be put back in the request box to ask it again.
/// </summary>
internal sealed unsafe class GlamourHistoryAddon : SettingsPanelAddon
{
    private const float ButtonW = 180f;

    public GoblinGlamour? Tweak { get; init; }

    protected override float HeaderHeight => 40f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;
        var clear = new TextButtonNode
        {
            String   = Tweak.Text("History.Clear"),
            Position = origin + new Vector2(width - ButtonW - Pad, 6f),
            Size     = new Vector2(ButtonW, 26f),
        };
        clear.OnClick = () => { Tweak.ClearHistory(); RequestRebuild(); };
        clear.AttachNode(this);
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var history = Tweak.Current.AiHistory;
        if (history.Count == 0)
        {
            Note(Tweak.Text("History.Empty"), Pad, ContentW, Muted);
            return;
        }

        foreach (var entry in history)
        {
            var kind = Tweak.Text(entry.Kind == GoblinGlamour.HistoryCreate ? "History.Create" : "History.Judge");
            var provider = Tweak.Text($"Provider.{AiProviders.Get(entry.Provider).Key}");
            Note($"{entry.When.ToLocalTime():g} · {kind} · {provider} · {entry.Model} · {Tweak.Text($"History.{entry.Result}")}",
                Pad, ContentW, Muted);
            Note(entry.Text, Pad, ContentW, White, 13);

            if (entry.Kind == GoblinGlamour.HistoryCreate)
            {
                Space(2f);
                var text = entry.Text;
                var again = new TextButtonNode
                {
                    String   = Tweak.Text("History.Reuse"),
                    Position = new Vector2(Pad, Y),
                    Size     = new Vector2(ButtonW, 26f),
                };
                again.OnClick = () => Tweak.ReuseRequest(text);
                again.AttachNode(Host);
                Y += 30f;
            }
            Space(12f);
        }
    }

    /// <summary>Shows the history again (a request was added or answered).</summary>
    internal void Refresh() => RequestRebuild();
}
