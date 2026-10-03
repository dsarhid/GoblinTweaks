using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Secondary native window: a help/legend panel for the Crafting Materials window,
/// styled like FFXIV's own Help &amp; Settings windows (category sidebar + content).
/// All text comes from <see cref="CraftLoc"/>, so it follows the window language.
/// </summary>
internal unsafe class CraftingHelpAddon : NativeAddon
{
    private static readonly Vector4 TitleGold = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 White     = new(1f, 1f, 1f, 1f);

    // Keyword colors (match the recipe list / status colors)
    private static readonly Vector4 Green  = new(0.40f, 0.88f, 0.46f, 1f);
    private static readonly Vector4 Yellow = new(0.95f, 0.82f, 0.28f, 1f);
    private static readonly Vector4 Orange = new(0.96f, 0.58f, 0.20f, 1f);
    private static readonly Vector4 Red    = new(0.92f, 0.42f, 0.38f, 1f);
    private static readonly Vector4 Grey   = new(0.62f, 0.62f, 0.62f, 1f);

    private const float SidebarW = 180f;
    private const float KeywordW = 128f;

    /// <summary>One legend line: a colored keyword (optional) + white description.</summary>
    private readonly record struct Line(string KeywordKey, Vector4 Color, string TextKey, float Height = 24f);

    private static readonly (string Key, string TitleKey, Line[] Lines)[] Pages =
    [
        ("Guide", "help.nav.guide",
        [
            new Line("", White, "help.guide.p1", 56f),
            new Line("", White, "help.guide.p2", 56f),
        ]),
        ("Colors", "help.nav.colors",
        [
            new Line("help.colors.green.kw",  Green,     "help.colors.green.tx"),
            new Line("help.colors.yellow.kw", Yellow,    "help.colors.yellow.tx"),
            new Line("help.colors.orange.kw", Orange,    "help.colors.orange.tx"),
            new Line("help.colors.grey.kw",   Grey,      "help.colors.grey.tx"),
            new Line("", White, "", 10f),
            new Line("help.colors.chips.kw",  TitleGold, "help.colors.chips.tx", 40f),
        ]),
        ("Icons", "help.nav.icons",
        [
            // Status indicators
            new Line("help.icons.check.kw",       Green,     "help.icons.check.tx"),
            new Line("help.icons.dot.kw",          Grey,      "help.icons.dot.tx", 40f),
            new Line("", White, "", 8f),
            // Filters
            new Line("help.icons.hide.kw",         TitleGold, "help.icons.hide.tx"),
            new Line("help.icons.hidenonlog.kw",   TitleGold, "help.icons.hidenonlog.tx", 40f),
            new Line("help.icons.allrecipes.kw",   TitleGold, "help.icons.allrecipes.tx", 40f),
            new Line("", White, "", 8f),
            // Sort & class
            new Line("help.icons.sort.kw",         TitleGold, "help.icons.sort.tx", 56f),
            new Line("help.icons.class.kw",        TitleGold, "help.icons.class.tx"),
            new Line("help.icons.refresh.kw",      TitleGold, "help.icons.refresh.tx"),
        ]),
        ("Tips", "help.nav.tips",
        [
            new Line("", White, "help.tips.t1", 56f),
            new Line("", White, "help.tips.t2", 40f),
            new Line("", White, "help.tips.t3", 40f),
            new Line("", White, "help.tips.t4", 40f),
            new Line("", White, "help.tips.t5", 56f),
        ]),
    ];

    private readonly List<SelectableTextNode> _navItems     = [];
    private readonly List<TextNode>           _contentNodes = [];
    private TextNode? _title;
    private Vector2   _contentPos;
    private float     _contentW;
    private string    _selected = "Guide";

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c  = ContentStartPosition;
        var cs = ContentSize;

        MakeTitle(c.X + 8f, c.Y + 4f, SidebarW - 12f, CraftLoc.Get("help.cat"));

        var y = c.Y + 34f;
        foreach (var (key, titleKey, _) in Pages)
        {
            var item = new SelectableTextNode
            {
                String     = CraftLoc.Get(titleKey),
                Position   = new Vector2(c.X, y),
                Size       = new Vector2(SidebarW, 28f),
                IsSelected = key == _selected,
            };
            item.TextNode.TextColor = item.IsSelected ? TitleGold : White;
            item.TextNode.FontSize  = 14;

            var capturedKey = key;
            item.OnClick = _ => Select(capturedKey);
            item.AttachNode(this);
            _navItems.Add(item);
            y += 30f;
        }

        new VerticalLineNode { Position = new Vector2(c.X + SidebarW, c.Y), Height = cs.Y, Width = 3f }
            .AttachNode(this);

        _contentPos = new Vector2(c.X + SidebarW + 16f, c.Y + 8f);
        _contentW   = cs.X - SidebarW - 26f;

        _title = new TextNode
        {
            Position  = _contentPos,
            Size      = new Vector2(_contentW, 28f),
            TextColor = TitleGold,
            FontType  = FontType.TrumpGothic,
            FontSize  = 24,
        };
        _title.AttachNode(this);

        new HorizontalLineNode
        {
            Position = new Vector2(_contentPos.X, _contentPos.Y + 32f),
            Size     = new Vector2(_contentW, 2f),
        }.AttachNode(this);

        Select(_selected);
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _navItems.Clear();
        _contentNodes.Clear();
        _title = null;
        base.OnFinalize(addon);
    }

    private void Select(string key)
    {
        _selected = key;

        foreach (var (item, page) in _navItems.Zip(Pages))
        {
            var on = page.Key == key;
            item.IsSelected         = on;
            item.TextNode.TextColor = on ? TitleGold : White;
        }

        var selected = Pages.First(p => p.Key == key);
        if (_title is not null) _title.String = CraftLoc.Get(selected.TitleKey);

        RenderContent(selected.Lines);
    }

    private void RenderContent(Line[] lines)
    {
        foreach (var node in _contentNodes)
            node.Dispose();
        _contentNodes.Clear();

        var y = _contentPos.Y + 46f;

        foreach (var line in lines)
        {
            var textX = _contentPos.X;
            var textW = _contentW;

            if (!string.IsNullOrEmpty(line.KeywordKey))
            {
                var kw = new TextNode
                {
                    String    = CraftLoc.Get(line.KeywordKey),
                    Position  = new Vector2(_contentPos.X, y),
                    Size      = new Vector2(KeywordW - 8f, 20f),
                    TextColor = line.Color,
                    FontSize  = 14,
                };
                kw.AttachNode(this);
                _contentNodes.Add(kw);

                textX = _contentPos.X + KeywordW;
                textW = _contentW - KeywordW;
            }

            if (!string.IsNullOrEmpty(line.TextKey))
            {
                var body = new TextNode
                {
                    String      = CraftLoc.Get(line.TextKey),
                    Position    = new Vector2(textX, y),
                    Size        = new Vector2(textW, line.Height),
                    TextColor   = White,
                    FontSize    = 14,
                    LineSpacing = 20,
                };
                body.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
                body.AttachNode(this);
                _contentNodes.Add(body);
            }

            y += line.Height + 6f;
        }
    }

    private TextNode MakeTitle(float x, float y, float w, string text)
    {
        var node = new TextNode
        {
            String    = text,
            Position  = new Vector2(x, y),
            Size      = new Vector2(w, 24f),
            TextColor = TitleGold,
            FontType  = FontType.TrumpGothic,
            FontSize  = 20,
        };
        node.AttachNode(this);
        return node;
    }
}
