using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native settings window of AutoGoblinRetainer: language of its windows, how the prices are chosen, how fast it
/// works, the retainers the Pinch tour visits, and the items whose price is never touched (a search box and the list).
/// </summary>
internal sealed unsafe class AutoGoblinRetainerSettingsAddon : SettingsPanelAddon
{
    public AutoGoblinRetainer? Tweak { get; init; }

    private readonly List<NodeBase> _rows = [];
    private CheckboxNode? _pinchAfter;
    private string        _term = string.Empty;
    private float         _rowsTop;
    private bool          _rowsDirty;

    protected override float HeaderHeight => 40f;

    protected override void BuildHeader(Vector2 origin, float width)
    {
        if (Tweak is null) return;

        TweakLanguageSelect.Add(this, Tweak, origin + new Vector2(width - TweakLanguageSelect.Width - Pad, 6f));
    }

    protected override void Build()
    {
        if (Tweak is null) return;
        var tweak   = Tweak;
        var options = tweak.Current;

        // A full rebuild disposed the nodes of the last one, the search box included.
        _rows.Clear();
        _term = string.Empty;

        Section(tweak.Text("Section.Prices"));

        Toggle(tweak.Text("Setting.UndercutPercent"), tweak.Text("Setting.UndercutPercent.Help"), options.UndercutIsPercent, on =>
        {
            options.UndercutIsPercent = on;
            tweak.SaveCurrent();
            RequestRebuild(); // shows the field of the chosen kind
        });

        if (options.UndercutIsPercent)
            Number(tweak.Text("Setting.UndercutPct"), tweak.Text("Setting.UndercutPct.Help"), 1, 50, 1, options.UndercutPercent,
                value => { options.UndercutPercent = value; tweak.SaveCurrent(); });
        else
            Number(tweak.Text("Setting.Undercut"), tweak.Text("Setting.Undercut.Help"), 0, 1_000_000, 1, options.UndercutAmount,
                value => { options.UndercutAmount = value; tweak.SaveCurrent(); });

        Number(tweak.Text("Setting.MaxCut"), tweak.Text("Setting.MaxCut.Help"), 1, 100, 1, options.MaxUndercutPercent,
            value => { options.MaxUndercutPercent = value; tweak.SaveCurrent(); });
        Number(tweak.Text("Setting.MinPrice"), tweak.Text("Setting.MinPrice.Help"), 1, 999_999_999, 10, options.MinPrice,
            value => { options.MinPrice = value; tweak.SaveCurrent(); });

        Section(tweak.Text("Section.Speed"));
        Number(tweak.Text("Setting.Delay"), tweak.Text("Setting.Delay.Help"), 500, 5000, 100, options.SearchDelayMs,
            value => { options.SearchDelayMs = value; tweak.SaveCurrent(); });

        Section(tweak.Text("Section.Listing"));
        _pinchAfter = Toggle(tweak.PinchAfterText, tweak.PinchAfterTooltip, options.PinchAfterListing, on => tweak.PinchAfter = on);

        Section(tweak.Text("Section.Retainers"));
        var retainers = tweak.Retainers();
        if (retainers.Count == 0)
        {
            Note(tweak.Text("Retainers.None"), Pad, ContentW, Muted);
        }
        else
        {
            Note(tweak.Text("Retainers.Help"), Pad, ContentW, Muted);
            Space(6f);
            foreach (var (id, name) in retainers)
            {
                var retainerId = id;
                Toggle(name, null, !options.ExcludedRetainers.Contains(id), on => tweak.SetExcluded(retainerId, !on));
            }
        }

        Section(tweak.Text("Section.Keep"));
        Note(tweak.Text("Keep.Help"), Pad, ContentW, Muted);
        Space(6f);

        var search = new TextInputNode
        {
            Position          = new Vector2(Pad, Y),
            Size              = new Vector2(ContentW, 28f),
            PlaceholderString = tweak.Text("Keep.Search"),
            MaxCharacters     = 64,
        };
        search.OnInputReceived = text =>
        {
            _term      = text.ToString().Trim();
            _rowsDirty = true;
        };
        search.AttachNode(Host);
        Y += 36f;

        _rowsTop = Y;
        RebuildRows();
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        // The same option lives next to the list button of the sell window; keep both in step.
        if (Tweak is not null && _pinchAfter is not null && _pinchAfter.IsChecked != Tweak.PinchAfter)
            _pinchAfter.IsChecked = Tweak.PinchAfter;

        if (_rowsDirty)
        {
            _rowsDirty = false;
            RebuildRows();
        }
    }

    protected override void OnClosed()
    {
        _rows.Clear();
        _pinchAfter = null;
        _term       = string.Empty;
        _rowsDirty  = false;
    }

    /// <summary>Redraws the search results and the kept items under the search box.</summary>
    private void RebuildRows()
    {
        if (Tweak is null) return;
        var tweak   = Tweak;
        var options = tweak.Current;

        NativeFocus.Release(InternalAddon);
        foreach (var row in _rows) row.Dispose();
        _rows.Clear();
        Y = _rowsTop;

        if (_term.Length >= 2)
        {
            var found = tweak.SearchItems(_term).Where(item => !options.KeepPrice.Contains(item.Id)).ToList();
            foreach (var (id, name) in found)
            {
                var add = new TextButtonNode
                {
                    String   = $"+ {name}",
                    Position = new Vector2(Pad, Y),
                    Size     = new Vector2(ContentW, 24f),
                };
                var itemId = id;
                add.OnClick = () =>
                {
                    options.KeepPrice.Add(itemId);
                    tweak.SaveCurrent();
                    _rowsDirty = true; // redrawn next frame, not while this button is handling its click
                };
                add.AttachNode(Host);
                _rows.Add(add);
                Y += 26f;
            }

            if (found.Count == 0)
                _rows.Add(Note(tweak.Text("Keep.NoResults"), Pad, ContentW, Muted));

            Space(10f);
        }

        if (options.KeepPrice.Count == 0)
        {
            _rows.Add(Note(tweak.Text("Keep.Empty"), Pad, ContentW, Muted));
        }
        else
        {
            foreach (var id in options.KeepPrice.ToList())
            {
                var remove = new TextButtonNode
                {
                    String   = "x",
                    Position = new Vector2(Pad, Y),
                    Size     = new Vector2(30f, 24f),
                };
                var itemId = id;
                remove.OnClick = () =>
                {
                    options.KeepPrice.Remove(itemId);
                    tweak.SaveCurrent();
                    _rowsDirty = true;
                };
                remove.AttachNode(Host);
                _rows.Add(remove);

                var name = new TextNode
                {
                    String    = AutoGoblinRetainer.ItemName(id),
                    Position  = new Vector2(Pad + 38f, Y + 3f),
                    Size      = new Vector2(ContentW - 38f, 18f),
                    TextColor = White,
                    FontSize  = 14,
                };
                name.AttachNode(Host);
                _rows.Add(name);

                Y += 28f;
            }
        }

        FinishLayout();
    }
}
