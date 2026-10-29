using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native window of Goblin Loot: the items of the loot list on the left (the last rolls are kept, it scrolls when a
/// big chest drops), and on the right a table of the party for the selected item, with a column for Need, Greed
/// and Pass, and a row for a player of another party of the alliance when one got the item.
/// </summary>
internal unsafe class LootRollsAddon : NativeAddon
{
    private static readonly Vector4 TitleGold = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 White     = new(0.93f, 0.93f, 0.93f, 1f);
    private static readonly Vector4 Orange    = new(1f, 0.62f, 0.12f, 1f);
    private static readonly Vector4 Green     = new(0.35f, 0.90f, 0.42f, 1f);
    private static readonly Vector4 Muted     = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 Cross     = new(0.50f, 0.50f, 0.50f, 1f);

    private const uint  FramedJobIconBase = 62100;
    private const float SideW      = 220f;
    private const float SideRowH   = 36f;
    private const float SideIcon   = 28f;
    private const float JobMarkSz  = 16f;
    private const float StatusW    = 44f;
    private const float ScrollBarW = 16f;
    private const float HeadH      = 48f;
    private const float ColumnsH   = 22f;
    private const float RowH       = 26f;
    private const float CellW      = 64f;
    private const float FooterH    = 34f;
    private const float BottomH    = 34f;
    private const int   MaxPlayers = 8;

    // Every item the game can list at once (16) and one more said before it was seen.
    private const int SideRows = LootRolls.HistoryLimit + 1;

    private sealed record SideRow(SelectableTextNode Button, IconImageNode Icon, IconImageNode JobMark, TextNode Status);

    private sealed record TableRow(TextNode Name, TextNode[] Cells);

    private readonly List<SideRow>  _side  = [];
    private readonly List<TableRow> _table = [];
    private readonly TextNode[]     _columns = new TextNode[4];

    private ScrollingNode<ResNode>? _sideScroll;
    private IconImageNode? _headIcon;
    private TextNode?      _headName;
    private TextNode?      _headStatus;
    private TextNode?      _headJob;
    private TextNode?      _footer;
    private TextNode?      _empty;

    private int  _selectedId;
    private int  _shownVersion = -1;
    private int  _shownSeconds;
    private int  _shownRows = -1;
    private uint _shownJob;
    private bool _dirty = true;

    public required GoblinLoot Tweak { get; init; }

    /// <summary>Shows the roll of an item.</summary>
    internal void Select(int entryId)
    {
        _selectedId = entryId;
        _dirty      = true;
    }

    /// <summary>A new item: the table goes to it, unless you are looking at a roll that is still open.</summary>
    internal void FollowNew(LootEntry entry)
    {
        var current = Tweak.Rolls.Find(_selectedId);
        if (current is null || current.Resolved) Select(entry.Id);
    }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c  = ContentStartPosition;
        var cs = ContentSize;

        BuildSide(c, cs.Y - BottomH - 4f);

        new VerticalLineNode { Position = new Vector2(c.X + SideW, c.Y), Height = cs.Y - BottomH, Width = 3f }
            .AttachNode(this);

        var x = c.X + SideW + 14f;
        var w = cs.X - SideW - 20f;
        BuildDetail(new Vector2(x, c.Y), w);

        BuildBottomBar(new Vector2(c.X, c.Y + cs.Y - BottomH), new Vector2(cs.X, BottomH));

        _shownVersion = -1;
        _shownRows    = -1;
        _dirty        = true;
        Redraw();
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        if (_dirty || _shownVersion != Tweak.Rolls.Version || _shownSeconds != SecondsSignature() || _shownJob != CurrentJob())
            Redraw();
    }

    /// <summary>Changes whenever a second goes by on an open roll, so the timers are redrawn.</summary>
    private int SecondsSignature()
    {
        var sum = 0;
        foreach (var entry in Tweak.Rolls.Entries)
            if (!entry.Resolved) sum = sum * 31 + entry.SecondsLeft + 1;
        return sum;
    }

    private static uint CurrentJob() => Svc.PlayerState.IsLoaded ? Svc.PlayerState.ClassJob.RowId : 0;

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _side.Clear();
        _table.Clear();
        Array.Clear(_columns);
        _sideScroll = null;
        _headIcon   = null;
        _headName   = _headStatus = _headJob = _footer = _empty = null;

        base.OnFinalize(addon);
    }

    // ── Building ──────────────────────────────────────────────────────────────

    private void BuildSide(Vector2 c, float height)
    {
        _sideScroll = new ScrollingNode<ResNode>
        {
            Position          = c,
            Size              = new Vector2(SideW - 4f, height),
            AutoHideScrollBar = true,
        };
        _sideScroll.AttachNode(this);
        var host = _sideScroll.ContentNode;

        // Leaves room for the scroll bar when a big chest makes the list scroll.
        var rowW = SideW - 4f - ScrollBarW;

        for (var index = 0; index < SideRows; index++)
        {
            var y = index * SideRowH;

            var icon = new IconImageNode
            {
                FitTexture = true,
                Position   = new Vector2(2f, y + (SideRowH - SideIcon) / 2f),
                Size       = new Vector2(SideIcon, SideIcon),
                IsVisible  = false,
            };
            icon.AttachNode(host);

            // The icon of your job over the corner of items your job can wear.
            var jobMark = new IconImageNode
            {
                FitTexture = true,
                Position   = new Vector2(2f + SideIcon - JobMarkSz + 4f, y + (SideRowH + SideIcon) / 2f - JobMarkSz + 4f),
                Size       = new Vector2(JobMarkSz, JobMarkSz),
                IsVisible  = false,
            };
            jobMark.AttachNode(host);

            var button = new SelectableTextNode
            {
                Position  = new Vector2(SideIcon + 6f, y),
                Size      = new Vector2(rowW - SideIcon - StatusW - 8f, SideRowH - 2f),
                IsVisible = false,
            };
            button.TextNode.FontSize = 13;
            button.TextNode.AddTextFlags(TextFlags.Ellipsis);

            var row = index;
            button.OnClick = _ =>
            {
                var entries = Tweak.Rolls.Entries;
                if (row < entries.Count) Select(entries[row].Id);
            };
            button.AttachNode(host);
            SetInteractive(button, false);

            // Two lines: how many have chosen, and the seconds left.
            var status = new TextNode
            {
                Position      = new Vector2(rowW - StatusW - 4f, y + 2f),
                Size          = new Vector2(StatusW, SideRowH - 4f),
                FontSize      = 12,
                LineSpacing   = 15,
                AlignmentType = AlignmentType.Right,
                IsVisible     = false,
            };
            status.AddTextFlags(TextFlags.MultiLine);
            status.AttachNode(host);

            _side.Add(new SideRow(button, icon, jobMark, status));
        }
    }

    private void BuildDetail(Vector2 origin, float width)
    {
        _headIcon = new IconImageNode
        {
            FitTexture = true,
            Position   = origin + new Vector2(0f, 2f),
            Size       = new Vector2(40f, 40f),
            IsVisible  = false,
        };
        _headIcon.AttachNode(this);

        _headName = new TextNode
        {
            Position  = origin + new Vector2(48f, 2f),
            Size      = new Vector2(width - 48f, 22f),
            TextColor = TitleGold,
            FontSize  = 16,
        };
        _headName.AddTextFlags(TextFlags.Ellipsis);
        _headName.AttachNode(this);

        _headStatus = new TextNode
        {
            Position  = origin + new Vector2(48f, 25f),
            Size      = new Vector2(width - 48f - 110f, 18f),
            TextColor = Muted,
            FontSize  = 12,
        };
        _headStatus.AttachNode(this);

        _headJob = new TextNode
        {
            Position      = origin + new Vector2(width - 110f, 25f),
            Size          = new Vector2(110f, 18f),
            TextColor     = Green,
            FontSize      = 12,
            AlignmentType = AlignmentType.Right,
            String        = Tweak.Text("ForYourJob"),
            IsVisible     = false,
        };
        _headJob.AttachNode(this);

        _empty = new TextNode
        {
            Position      = origin + new Vector2(0f, 90f),
            Size          = new Vector2(width, 40f),
            TextColor     = Muted,
            FontSize      = 14,
            AlignmentType = AlignmentType.Center,
            String        = Tweak.Text("Waiting"),
        };
        _empty.AttachNode(this);

        // Table: player, Need, Greed, Pass.
        var nameW  = width - CellW * 3f;
        var headY  = origin.Y + HeadH;
        string[] titles = [Tweak.Text("Col.Player"), Tweak.Text("Col.Need"), Tweak.Text("Col.Greed"), Tweak.Text("Col.Pass")];
        for (var column = 0; column < 4; column++)
        {
            _columns[column] = new TextNode
            {
                String        = titles[column],
                Position      = new Vector2(column == 0 ? origin.X + 4f : origin.X + nameW + (column - 1) * CellW, headY),
                Size          = new Vector2(column == 0 ? nameW - 4f : CellW, 18f),
                TextColor     = TitleGold,
                FontSize      = 12,
                AlignmentType = column == 0 ? AlignmentType.Left : AlignmentType.Center,
                IsVisible     = false,
            };
            _columns[column].AttachNode(this);
        }

        // The party, and one more row for a player of another party that got the item.
        for (var index = 0; index <= MaxPlayers; index++)
        {
            var y = headY + ColumnsH + index * RowH;

            var name = new TextNode
            {
                Position  = new Vector2(origin.X + 4f, y + 4f),
                Size      = new Vector2(nameW - 8f, 18f),
                TextColor = White,
                FontSize  = 14,
                IsVisible = false,
            };
            name.AddTextFlags(TextFlags.Ellipsis);
            name.AttachNode(this);

            var cells = new TextNode[3];
            for (var column = 0; column < 3; column++)
            {
                cells[column] = new TextNode
                {
                    Position      = new Vector2(origin.X + nameW + column * CellW, y + 4f),
                    Size          = new Vector2(CellW, 18f),
                    FontSize      = 14,
                    AlignmentType = AlignmentType.Center,
                    IsVisible     = false,
                };
                cells[column].AttachNode(this);
            }

            _table.Add(new TableRow(name, cells));
        }

        // Under the last possible row, so it never covers a player.
        _footer = new TextNode
        {
            Position      = new Vector2(origin.X + 4f, headY + ColumnsH + (MaxPlayers + 1) * RowH + 6f),
            Size          = new Vector2(width - 8f, FooterH),
            FontSize      = 12,
            LineSpacing   = 16,
            AlignmentType = AlignmentType.TopLeft,
        };
        _footer.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
        _footer.AttachNode(this);
    }

    private void BuildBottomBar(Vector2 pos, Vector2 size)
    {
        new HorizontalLineNode { Position = pos, Size = new Vector2(size.X, 2f) }.AttachNode(this);

        // Bottom left: the help and the settings.
        var buttonY = pos.Y + (size.Y - 22f) / 2f;
        new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(pos.X + 4f, buttonY),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Help.Tooltip"),
            OnClick     = Tweak.OpenHelp,
        }.AttachNode(this);

        new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = new Vector2(pos.X + 32f, buttonY),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Window.Settings"),
            OnClick     = Tweak.OpenSettings,
        }.AttachNode(this);

        // Bottom right: the summary of the duty and clearing what is kept.
        const float buttonW = 120f;
        var textY = pos.Y + (size.Y - 26f) / 2f + 1f;

        var clear = new TextButtonNode
        {
            String      = Tweak.Text("Button.Clear"),
            Position    = new Vector2(pos.X + size.X - buttonW, textY),
            Size        = new Vector2(buttonW, 26f),
            TextTooltip = Tweak.Text("Button.Clear.Help"),
        };
        clear.OnClick = Tweak.ClearHistory;
        clear.AttachNode(this);

        var summary = new TextButtonNode
        {
            String      = Tweak.Text("Button.Summary"),
            Position    = new Vector2(pos.X + size.X - buttonW * 2f - 6f, textY),
            Size        = new Vector2(buttonW, 26f),
            TextTooltip = Tweak.Text("Button.Summary.Help"),
        };
        summary.OnClick = Tweak.OpenSummary;
        summary.AttachNode(this);
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    private void Redraw()
    {
        _dirty        = false;
        _shownVersion = Tweak.Rolls.Version;
        _shownSeconds = SecondsSignature();
        _shownJob     = CurrentJob();

        var entries  = Tweak.Rolls.Entries;
        var selected = entries.FirstOrDefault(entry => entry.Id == _selectedId) ?? entries.FirstOrDefault();
        _selectedId  = selected?.Id ?? 0;

        for (var index = 0; index < _side.Count; index++)
            DrawSide(_side[index], index < entries.Count ? entries[index] : null, selected);

        // The list scrolls exactly as far as there are items.
        var rows = Math.Min(entries.Count, _side.Count);
        if (rows != _shownRows && _sideScroll is not null)
        {
            _shownRows = rows;
            _sideScroll.ContentNode.Height = MathF.Max(_sideScroll.Height, rows * SideRowH);
            _sideScroll.RecalculateSizes();
        }

        DrawDetail(selected);
    }

    /// <summary>An empty row is hidden but its area still answers the mouse (hover sound, hand cursor), so it is told not to.</summary>
    private static void SetInteractive(SelectableTextNode button, bool on)
    {
        button.EnableHighlight    = on;
        button.EnableSelection    = on;
        button.ShowClickableCursor = on;
    }

    private void DrawSide(SideRow row, LootEntry? entry, LootEntry? selected)
    {
        row.Button.IsVisible = row.Icon.IsVisible = row.Status.IsVisible = entry is not null;
        SetInteractive(row.Button, entry is not null);
        if (entry is null)
        {
            row.JobMark.IsVisible = false;
            return;
        }

        var on = entry == selected;
        row.Icon.IconId             = entry.IconId;
        row.Icon.ItemTooltip        = GoblinLoot.PlainItemId(entry.ItemId);
        row.Button.String           = entry.Count > 1 ? $"{entry.ItemName} x{entry.Count}" : entry.ItemName;
        row.Button.IsSelected       = on;
        row.Button.TextNode.TextColor = on ? TitleGold : White;

        var forMe = Tweak.ForMyJob(entry.ItemId);
        row.JobMark.IsVisible = forMe;
        if (forMe) row.JobMark.IconId = FramedJobIconBase + _shownJob;

        if (!entry.Resolved)
        {
            row.Status.String    = $"{entry.Chosen}/{entry.Players.Count}" + (entry.SecondsLeft > 0 ? $"\n{entry.SecondsLeft}s" : string.Empty);
            row.Status.TextColor = Orange;
        }
        else if (entry.Winner is { } winner && winner.Choice != LootChoice.Pass)
        {
            row.Status.String    = winner.Value > 0 ? winner.Value.ToString() : "OK";
            row.Status.TextColor = Green;
        }
        else
        {
            row.Status.String    = "-";
            row.Status.TextColor = Muted;
        }
    }

    private void DrawDetail(LootEntry? entry)
    {
        var any = entry is not null;
        _empty!.IsVisible = !any;
        _headIcon!.IsVisible = _headName!.IsVisible = _headStatus!.IsVisible = _footer!.IsVisible = any;
        foreach (var column in _columns) column.IsVisible = any;

        if (entry is null)
        {
            _headJob!.IsVisible = false;
            foreach (var row in _table) HideRow(row);
            return;
        }

        _headIcon.IconId      = entry.IconId;
        _headIcon.ItemTooltip = GoblinLoot.PlainItemId(entry.ItemId);
        _headName.String      = entry.Count > 1 ? $"{entry.ItemName} x{entry.Count}" : entry.ItemName;
        _headJob!.IsVisible   = Tweak.ForMyJob(entry.ItemId);

        _headStatus.String = entry.Resolved
            ? Tweak.Text("Over")
            : string.Format(Tweak.Text("Chosen"), entry.Chosen, entry.Players.Count) + (entry.SecondsLeft > 0 ? $"  ·  {entry.SecondsLeft}s" : string.Empty);
        _headStatus.TextColor = entry.Resolved ? Muted : Orange;

        // Your party, then the player of another party that got the item, if any.
        var outside = entry.Others.FirstOrDefault(player => player.Winner);
        var players = entry.Players.Take(MaxPlayers).ToList();
        if (outside is not null) players.Add(outside);

        for (var index = 0; index < _table.Count; index++)
        {
            var row = _table[index];
            if (index >= players.Count) { HideRow(row); continue; }

            var player = players[index];
            row.Name.IsVisible = true;
            row.Name.String    = PlayerName(player);
            row.Name.TextColor = player.Winner ? Green : White;

            LootChoice[] columns = [LootChoice.Need, LootChoice.Greed, LootChoice.Pass];
            for (var column = 0; column < 3; column++)
            {
                var (text, color) = Cell(player, columns[column]);
                row.Cells[column].IsVisible = true;
                row.Cells[column].String    = text;
                row.Cells[column].TextColor = color;
            }
        }

        if (!entry.Resolved)
        {
            var missing = entry.Players.Where(player => player.Pending).Select(player => player.Name).ToList();
            var names   = string.Join(", ", missing.Take(3));
            if (missing.Count > 3) names += " " + string.Format(Tweak.Text("More"), missing.Count - 3);
            _footer.String    = missing.Count > 0 ? string.Format(Tweak.Text("Missing"), names) : Tweak.Text("AllChose");
            _footer.TextColor = missing.Count > 0 ? Orange : Muted;
        }
        else if (entry.Winner is { } winner)
        {
            var value = winner.Choice is LootChoice.Need or LootChoice.Greed ? winner.Value.ToString() : "-";
            _footer.String    = string.Format(Tweak.Text("Winner"), PlayerName(winner), value);
            _footer.TextColor = Green;
        }
        else
        {
            _footer.String    = Tweak.Text("NoWinner");
            _footer.TextColor = Muted;
        }
    }

    private string PlayerName(LootPlayer player)
    {
        if (player.IsMe)    return $"{player.Name} {Tweak.Text("You")}";
        if (player.Outside) return $"{player.Name} {Tweak.Text("OtherParty")}";
        return player.Name;
    }

    private static void HideRow(TableRow row)
    {
        row.Name.IsVisible = false;
        foreach (var cell in row.Cells) cell.IsVisible = false;
    }

    /// <summary>
    /// What a column of the table says for a player: a "?" while it is not known, and once it is, the number where
    /// they rolled and a cross in the others. A player that let the time run out shows "Auto" under Pass.
    /// </summary>
    private (string Text, Vector4 Color) Cell(LootPlayer player, LootChoice column)
    {
        // Chosen, but the game does not say what until the roll is over.
        if (player.Choice == LootChoice.None) return ("?", player.Cast ? Muted : Orange);

        if (player.Choice != column) return ("X", Cross);
        if (column == LootChoice.Pass) return player.TimedOut ? (Tweak.Text("Auto"), Muted) : (Tweak.Text("Passed"), Green);

        // The highest roll is the one that got the item.
        return (player.Winner ? $"{player.Value} ↑" : player.Value.ToString(), Green);
    }
}
