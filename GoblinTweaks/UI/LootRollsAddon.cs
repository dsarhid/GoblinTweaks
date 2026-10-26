using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Core;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native window of Goblin Loot: the items of the loot list on the left (the last rolls are kept), and on the right a
/// table of the party for the selected item, with a column for Need, Greed and Pass.
/// </summary>
internal unsafe class LootRollsAddon : NativeAddon
{
    private static readonly Vector4 TitleGold = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 White     = new(0.93f, 0.93f, 0.93f, 1f);
    private static readonly Vector4 Orange    = new(1f, 0.62f, 0.12f, 1f);
    private static readonly Vector4 Green     = new(0.35f, 0.90f, 0.42f, 1f);
    private static readonly Vector4 Muted     = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 Cross     = new(0.50f, 0.50f, 0.50f, 1f);

    private const float SideW      = 210f;
    private const float SideRowH   = 36f;
    private const float SideIcon   = 28f;
    private const float StatusW    = 44f;
    private const float HeadH      = 48f;
    private const float ColumnsH   = 22f;
    private const float RowH       = 26f;
    private const float CellW      = 64f;
    private const float FooterH    = 34f;
    private const float BottomH    = 34f;
    private const int   MaxPlayers = 8;

    private sealed record SideRow(SelectableTextNode Button, IconImageNode Icon, TextNode Status);

    private sealed record TableRow(TextNode Name, TextNode[] Cells);

    private readonly List<SideRow>  _side  = [];
    private readonly List<TableRow> _table = [];
    private readonly TextNode[]     _columns = new TextNode[4];

    private IconImageNode? _headIcon;
    private TextNode?      _headName;
    private TextNode?      _headStatus;
    private TextNode?      _footer;
    private TextNode?      _empty;

    private int  _selectedId;
    private int  _shownVersion = -1;
    private int  _shownSeconds;
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

        BuildSide(c);

        new VerticalLineNode { Position = new Vector2(c.X + SideW, c.Y), Height = cs.Y - BottomH, Width = 3f }
            .AttachNode(this);

        var x = c.X + SideW + 14f;
        var w = cs.X - SideW - 20f;
        BuildDetail(new Vector2(x, c.Y), w);

        new HorizontalLineNode { Position = new Vector2(c.X, c.Y + cs.Y - BottomH), Size = new Vector2(cs.X, 2f) }
            .AttachNode(this);
        // Bottom left: the help and the settings.
        var buttonY = c.Y + cs.Y - BottomH + (BottomH - 22f) / 2f;
        new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(c.X + 4f, buttonY),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Help.Tooltip"),
            OnClick     = Tweak.OpenHelp,
        }.AttachNode(this);

        new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = new Vector2(c.X + 32f, buttonY),
            Size        = new Vector2(22f, 22f),
            TextTooltip = Tweak.Text("Window.Settings"),
            OnClick     = Tweak.OpenSettings,
        }.AttachNode(this);

        _shownVersion = -1;
        _dirty        = true;
        Redraw();
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        if (_dirty || _shownVersion != Tweak.Rolls.Version || _shownSeconds != SecondsSignature())
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

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _side.Clear();
        _table.Clear();
        Array.Clear(_columns);
        _headIcon = null;
        _headName = _headStatus = _footer = _empty = null;

        base.OnFinalize(addon);
    }

    // ── Building ──────────────────────────────────────────────────────────────

    private void BuildSide(Vector2 c)
    {
        for (var index = 0; index < LootRolls.HistoryLimit; index++)
        {
            var y = c.Y + index * SideRowH;

            var icon = new IconImageNode
            {
                FitTexture = true,
                Position   = new Vector2(c.X + 2f, y + (SideRowH - SideIcon) / 2f),
                Size       = new Vector2(SideIcon, SideIcon),
                IsVisible  = false,
            };
            icon.AttachNode(this);

            var button = new SelectableTextNode
            {
                Position  = new Vector2(c.X + SideIcon + 6f, y),
                Size      = new Vector2(SideW - SideIcon - StatusW - 8f, SideRowH - 2f),
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
            button.AttachNode(this);
            SetInteractive(button, false);

            // Two lines: how many have chosen, and the seconds left.
            var status = new TextNode
            {
                Position      = new Vector2(c.X + SideW - StatusW - 14f, y + 2f),
                Size          = new Vector2(StatusW, SideRowH - 4f),
                FontSize      = 12,
                LineSpacing   = 15,
                AlignmentType = AlignmentType.Right,
                IsVisible     = false,
            };
            status.AddTextFlags(TextFlags.MultiLine);
            status.AttachNode(this);

            _side.Add(new SideRow(button, icon, status));
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
            Size      = new Vector2(width - 48f, 18f),
            TextColor = Muted,
            FontSize  = 12,
        };
        _headStatus.AttachNode(this);

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

        for (var index = 0; index < MaxPlayers; index++)
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

        _footer = new TextNode
        {
            Position      = new Vector2(origin.X + 4f, ContentSize.Y - BottomH - FooterH - 2f),
            Size          = new Vector2(width - 8f, FooterH),
            FontSize      = 12,
            LineSpacing   = 16,
            AlignmentType = AlignmentType.TopLeft,
        };
        _footer.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
        _footer.AttachNode(this);
    }

    // ── Drawing ───────────────────────────────────────────────────────────────

    private void Redraw()
    {
        _dirty        = false;
        _shownVersion = Tweak.Rolls.Version;
        _shownSeconds = SecondsSignature();

        var entries  = Tweak.Rolls.Entries;
        var selected = entries.FirstOrDefault(entry => entry.Id == _selectedId) ?? entries.FirstOrDefault();
        _selectedId  = selected?.Id ?? 0;

        for (var index = 0; index < _side.Count; index++)
            DrawSide(_side[index], index < entries.Count ? entries[index] : null, selected);

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
        if (entry is null) return;

        var on = entry == selected;
        row.Icon.IconId             = entry.IconId;
        row.Button.String           = entry.Count > 1 ? $"{entry.ItemName} x{entry.Count}" : entry.ItemName;
        row.Button.IsSelected       = on;
        row.Button.TextNode.TextColor = on ? TitleGold : White;

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
            foreach (var row in _table) HideRow(row);
            return;
        }

        _headIcon.IconId  = entry.IconId;
        _headName.String  = entry.Count > 1 ? $"{entry.ItemName} x{entry.Count}" : entry.ItemName;

        _headStatus.String = entry.Resolved
            ? Tweak.Text("Over")
            : string.Format(Tweak.Text("Chosen"), entry.Chosen, entry.Players.Count) + (entry.SecondsLeft > 0 ? $"  ·  {entry.SecondsLeft}s" : string.Empty);
        _headStatus.TextColor = entry.Resolved ? Muted : Orange;

        for (var index = 0; index < _table.Count; index++)
        {
            var row = _table[index];
            if (index >= entry.Players.Count) { HideRow(row); continue; }

            var player = entry.Players[index];
            row.Name.IsVisible = true;
            row.Name.String    = player.IsMe ? $"{player.Name} {Tweak.Text("You")}" : player.Name;
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
            _footer.String    = string.Format(Tweak.Text("Winner"), winner.Name, winner.Choice == LootChoice.Pass ? "-" : winner.Value.ToString());
            _footer.TextColor = Green;
        }
        else
        {
            _footer.String    = Tweak.Text("NoWinner");
            _footer.TextColor = Muted;
        }
    }

    private static void HideRow(TableRow row)
    {
        row.Name.IsVisible = false;
        foreach (var cell in row.Cells) cell.IsVisible = false;
    }

    /// <summary>What a column of the table says for a player: a "?" while it is not known, and once it is, the number where they rolled and a cross in the others.</summary>
    private (string Text, Vector4 Color) Cell(LootPlayer player, LootChoice column)
    {
        // Chosen, but the game does not say what until the roll is over.
        if (player.Choice == LootChoice.None) return ("?", player.Cast ? Muted : Orange);

        if (player.Choice != column) return ("X", Cross);
        if (column == LootChoice.Pass) return (Tweak.Text("Passed"), Green);

        // The highest roll is the one that got the item.
        return (player.Winner ? $"{player.Value} ↑" : player.Value.ToString(), Green);
    }
}
