using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native help window shared by the tweak windows, styled like FFXIV's own help: a list of topics
/// on the left and the selected topic on the right. With a single page the topic list is left out.
/// </summary>
/// <remarks>
/// Native text does not scroll by itself: the text is inside a scrolling area, as tall as the text is.
/// </remarks>
internal unsafe class TextHelpAddon : NativeAddon
{
    private static readonly Vector4 TitleGold = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 White     = new(1f, 1f, 1f, 1f);

    private const float SidebarW = 190f;
    private const float TitleH   = 40f;
    private const float ScrollBarW = 24f;

    private readonly List<SelectableTextNode> _navItems = [];
    private TextNode? _title;
    private TextNode? _body;
    private ScrollingNode<ResNode>? _scroll;
    private int _selected;

    public required IReadOnlyList<(string Title, string Text)> Pages { get; init; }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c  = ContentStartPosition;
        var cs = ContentSize;
        var contentX = c.X + 8f;

        if (Pages.Count > 1)
        {
            var y = c.Y + 4f;
            for (var index = 0; index < Pages.Count; index++)
            {
                var item = new SelectableTextNode
                {
                    String   = Pages[index].Title,
                    Position = new Vector2(c.X, y),
                    Size     = new Vector2(SidebarW, 28f),
                };
                item.TextNode.FontSize = 14;

                var page = index;
                item.OnClick = _ => Select(page);
                item.AttachNode(this);
                _navItems.Add(item);
                y += 30f;
            }

            new VerticalLineNode { Position = new Vector2(c.X + SidebarW, c.Y), Height = cs.Y, Width = 3f }
                .AttachNode(this);

            contentX = c.X + SidebarW + 16f;
        }

        var contentW = c.X + cs.X - contentX - 10f;
        var bodyY    = c.Y + 6f;

        if (Pages.Count > 1)
        {
            _title = new TextNode
            {
                Position  = new Vector2(contentX, c.Y + 6f),
                Size      = new Vector2(contentW, 28f),
                TextColor = TitleGold,
                FontType  = FontType.TrumpGothic,
                FontSize  = 24,
            };
            _title.AttachNode(this);

            new HorizontalLineNode
            {
                Position = new Vector2(contentX, c.Y + TitleH - 2f),
                Size     = new Vector2(contentW, 2f),
            }.AttachNode(this);

            bodyY = c.Y + TitleH + 8f;
        }

        _scroll = new ScrollingNode<ResNode>
        {
            Position          = new Vector2(contentX, bodyY),
            Size              = new Vector2(contentW, c.Y + cs.Y - bodyY - 6f),
            AutoHideScrollBar = true,
        };
        _scroll.AttachNode(this);

        // Top aligned: centered, a text taller than its box would grow upwards over the title.
        _body = new TextNode
        {
            Position      = Vector2.Zero,
            Size          = new Vector2(contentW - ScrollBarW, 100f),
            TextColor     = White,
            FontSize      = 14,
            LineSpacing   = 20,
            AlignmentType = AlignmentType.TopLeft,
        };
        _body.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
        _body.AttachNode(_scroll.ContentNode);

        Select(Math.Clamp(_selected, 0, Pages.Count - 1));
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _navItems.Clear();
        _title = null;
        _body  = null;
        _scroll = null;
        base.OnFinalize(addon);
    }

    private void Select(int page)
    {
        _selected = page;

        for (var index = 0; index < _navItems.Count; index++)
        {
            var on = index == page;
            _navItems[index].IsSelected         = on;
            _navItems[index].TextNode.TextColor = on ? TitleGold : White;
        }

        if (_title is not null) _title.String = Pages[page].Title;
        if (_body is not null && _scroll is not null)
        {
            _body.String = Pages[page].Text;

            // As tall as the text, so the area scrolls exactly as far as there is text.
            var height = MathF.Max(_scroll.Height, _body.GetTextDrawSize(false).Y + 12f);
            _body.Height               = height;
            _scroll.ContentNode.Height = height;
            _scroll.RecalculateSizes();
        }
    }
}
