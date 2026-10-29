using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Tweaks;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native window with everything that was rolled for in the current duty: each item with who got it, Need or Greed
/// and the number. What you got is shown in gold, and a line on top counts it.
/// </summary>
internal sealed unsafe class LootSummaryAddon : SettingsPanelAddon
{
    private const float RowH = 40f;
    private const float Icon = 32f;

    private int _shownVersion = -1;

    public GoblinLoot? Tweak { get; init; }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        // Built again when a roll ends, never in the middle of a click.
        if (Tweak is not null && Tweak.Rolls.SummaryVersion != _shownVersion)
            RequestRebuild();

        base.OnUpdate(addon);
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var tweak  = Tweak;
        var awards = tweak.Rolls.Summary;
        _shownVersion = tweak.Rolls.SummaryVersion;

        Section(tweak.Text("Summary.Header"));

        if (awards.Count == 0)
        {
            Note(tweak.Text("Summary.Empty"), Pad, ContentW, Muted);
            return;
        }

        var mine = awards.Count(award => award.Mine);
        Note(string.Format(tweak.Text("Summary.Mine"), mine, awards.Count), Pad, ContentW, mine > 0 ? Gold : Muted, 13);
        Space(6f);

        // Newest first, like the window of the rolls.
        foreach (var award in Enumerable.Reverse(awards))
            Row(tweak, award);
    }

    private void Row(GoblinLoot tweak, LootAward award)
    {
        new IconImageNode
        {
            IconId      = award.IconId,
            FitTexture  = true,
            Position    = new Vector2(Pad, Y + (RowH - Icon) / 2f),
            Size        = new Vector2(Icon, Icon),
            ItemTooltip = GoblinLoot.PlainItemId(award.ItemId),
        }.AttachNode(Host);

        var textX = Pad + Icon + 8f;
        var textW = ContentW - Icon - 8f;

        var item = new TextNode
        {
            String    = award.Count > 1 ? $"{award.ItemName} x{award.Count}" : award.ItemName,
            Position  = new Vector2(textX, Y + 2f),
            Size      = new Vector2(textW, 18f),
            TextColor = award.Mine ? Gold : White,
            FontSize  = 14,
        };
        item.AddTextFlags(TextFlags.Ellipsis);
        item.AttachNode(Host);

        new TextNode
        {
            String    = WhoGotIt(tweak, award),
            Position  = new Vector2(textX, Y + 20f),
            Size      = new Vector2(textW, 16f),
            TextColor = award.Winner is null ? Muted : award.Mine ? Gold : Muted,
            FontSize  = 12,
        }.AttachNode(Host);

        Y += RowH;
    }

    /// <summary>"Name · Need 87", "Name (other party)", or that nobody got it.</summary>
    private static string WhoGotIt(GoblinLoot tweak, LootAward award)
    {
        if (award.Winner is null) return tweak.Text("NoWinner");

        var name = award.Mine ? $"{award.Winner} {tweak.Text("You")}"
                 : award.Outside ? $"{award.Winner} {tweak.Text("OtherParty")}"
                 : award.Winner;

        return award.Choice switch
        {
            LootChoice.Need  => $"{name}  ·  {tweak.Text("Choice.Need")} {award.Value}",
            LootChoice.Greed => $"{name}  ·  {tweak.Text("Choice.Greed")} {award.Value}",
            _                => name,
        };
    }
}
