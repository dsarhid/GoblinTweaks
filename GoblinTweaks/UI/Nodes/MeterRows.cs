using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI.Nodes;

/// <summary>One line of a damage meter table: its bar, job icon and two texts, and a hit area when it can be clicked.</summary>
internal sealed class MeterRow
{
    public required ColorImageNode Track { get; init; }
    public required ColorImageNode Bar { get; init; }
    public required IconImageNode Icon { get; init; }
    public required TextNode Name { get; init; }
    public required TextNode Value { get; init; }
    public CollisionNode? Hit { get; init; }
    public TextNode[] Cells { get; init; } = [];

    public bool Visible
    {
        set
        {
            Track.IsVisible = Bar.IsVisible = Icon.IsVisible = Name.IsVisible = Value.IsVisible = value;
            if (Hit is not null) Hit.IsVisible = value;
            foreach (var cell in Cells) cell.IsVisible = value && cell.IsVisible;
        }
    }
}

/// <summary>Builds and fills the lines of the tables of the damage meter.</summary>
internal static unsafe class MeterRows
{
    private static readonly Vector4 White   = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Outline = new(0.05f, 0.05f, 0.05f, 1f);
    private static readonly Vector4 TrackBg = new(0f, 0f, 0f, 0.35f);

    public static TextNode Text(Vector2 position, Vector2 size, AlignmentType alignment, uint fontSize)
    {
        var text = new TextNode
        {
            Position         = position,
            Size             = size,
            AlignmentType    = alignment,
            FontSize         = fontSize,
            TextColor        = White,
            TextOutlineColor = Outline,
            IsVisible        = false,
        };
        text.AddTextFlags(TextFlags.Edge);
        return text;
    }

    /// <summary>Writes the text and, if it does not fit in the width of the node, cuts it with "..." at the end. Tells whether it had to.</summary>
    public static bool Fit(TextNode node, string full)
    {
        node.String = full;
        var max = node.Width;
        if (node.GetTextDrawSize(false).X <= max) return false;

        var text = full;
        while (text.Length > 1)
        {
            text = text[..^1];
            node.String = text.TrimEnd() + "...";
            if (node.GetTextDrawSize(false).X <= max) break;
        }
        return true;
    }

    public static void Click(CollisionNode area, NodeBase parent, Action onClick)
    {
        area.AddEvent(AtkEventType.MouseClick, (_, _, _, _, _) => onClick());
        area.AttachNode(parent);
    }

    /// <summary>One line, at its place under the header; <paramref name="onClick"/> gives it a hit area.</summary>
    /// <summary>The columns of a table with cells: their left edge and width, with the weight of each.</summary>
    public static (float X, float W)[] Slots(int count, float width, bool names, float reserve)
    {
        float[] weights = [1f, 1f, 1.1f, 1.4f, 1.4f];
        var left  = names ? width * 0.36f : 4f;
        var space = width - left - 6f - reserve;
        var total = 0f;
        for (var i = 0; i < count; i++) total += weights[i % weights.Length];

        var slots = new (float, float)[count];
        var x = left;
        for (var i = 0; i < count; i++)
        {
            var w = space * weights[i % weights.Length] / total;
            slots[i] = (x, w);
            x += w;
        }
        return slots;
    }

    public static MeterRow Build(NodeBase parent, float width, int index, Action? onClick, int cells = 0)
    {
        var y = DamageMeterNode.HeaderH + 2f + index * DamageMeterNode.RowH;
        var h = DamageMeterNode.RowH;
        var row = new MeterRow
        {
            Track = new ColorImageNode { Color = TrackBg, Position = new Vector2(2f, y), Size = new Vector2(width - 4f, h - 2f), IsVisible = false },
            Bar   = new ColorImageNode { Position = new Vector2(2f, y), Size = new Vector2(1f, h - 2f), IsVisible = false },
            Icon  = new IconImageNode { FitTexture = true, Position = new Vector2(4f, y), Size = new Vector2(DamageMeterNode.IconSize, DamageMeterNode.IconSize), IsVisible = false },
            Name  = Text(new Vector2(DamageMeterNode.IconSize + 10f, y + 2f), new Vector2(width * 0.42f, h - 4f), AlignmentType.Left, 14),
            Value = Text(new Vector2(width * 0.42f, y + 2f), new Vector2(width * 0.58f - 8f, h - 4f), AlignmentType.Right, 13),
            Cells = [.. Enumerable.Range(0, cells).Select(_ => Text(new Vector2(0f, y + 2f), new Vector2(40f, h - 4f), AlignmentType.Center, 13))],
            Hit   = onClick is null ? null : new CollisionNode { CollisionType = CollisionType.Hit, Position = new Vector2(2f, y), Size = new Vector2(width - 4f, h - 2f), IsVisible = false },
        };

        // Added after the texts they sit behind would put them on top: attach order is draw order.
        row.Track.AttachNode(parent);
        row.Bar.AttachNode(parent);
        row.Icon.AttachNode(parent);
        row.Name.AttachNode(parent);
        row.Value.AttachNode(parent);
        foreach (var cell in row.Cells) cell.AttachNode(parent);
        if (row.Hit is not null && onClick is not null) Click(row.Hit, parent, onClick);
        row.Visible = false;
        return row;
    }

    /// <summary>Sizes a line for a table of this width; without names, the amount takes the whole line.</summary>
    public static void Place(MeterRow row, float width, bool names)
    {
        row.Track.Width = width - 4f;
        if (row.Hit is not null) row.Hit.Width = width - 4f;

        if (names)
        {
            row.Value.X     = width * 0.42f;
            row.Value.Width = width * 0.58f - 8f;
        }
        else
        {
            row.Value.X     = 6f;
            row.Value.Width = width - 14f;
        }
    }

    public static void Fill(MeterRow row, DamageMeterRow data, float width, bool names)
    {
        row.Icon.IconId    = data.IconId != 0 ? data.IconId : data.JobId == 0 ? 0 : 62100 + data.JobId;
        row.Icon.IsVisible = names && (data.JobId != 0 || data.IconId != 0);
        row.Name.IsVisible = names;
        row.Name.String    = data.Name;
        row.Value.String   = data.Value;
        row.Value.IsVisible = data.Cells is null;
        for (var i = 0; i < row.Cells.Length; i++)
        {
            row.Cells[i].IsVisible = data.Cells is not null;
            if (data.Cells is not null && i < data.Cells.Length) row.Cells[i].String = data.Cells[i];
        }
        row.Bar.Color      = data.Color;
        row.Bar.Width      = Math.Max(1f, (width - 4f) * Math.Clamp(data.Fraction, 0f, 1f));
    }
}
