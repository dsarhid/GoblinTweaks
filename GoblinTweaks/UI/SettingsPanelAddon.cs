using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Base of the native settings windows: a scrolling column filled from top to bottom with section titles, toggles,
/// number fields and choices. A subclass only describes its options in <see cref="Build"/>; the layout cursor
/// <see cref="Y"/> moves down as each option is added, and <see cref="FinishLayout"/> sizes the scrolling area.
/// </summary>
internal abstract unsafe class SettingsPanelAddon : NativeAddon
{
    protected static readonly Vector4 Gold  = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    protected static readonly Vector4 Muted = new(0.62f, 0.62f, 0.62f, 1f);
    protected static readonly Vector4 White = new(1f, 1f, 1f, 1f);

    private const float ScrollBarW = 26f;
    protected const float Pad = 8f;

    private ScrollingNode<ResNode>? _scroll;

    /// <summary>Layout cursor inside the scrolling content.</summary>
    protected float Y;

    /// <summary>Width available inside the scrolling content.</summary>
    protected float ContentW;

    protected ResNode Host => _scroll!.ContentNode;

    /// <summary>Height of a fixed strip above the scrolling options (0 = none). It holds things that must not scroll away or be clipped, like a drop-down.</summary>
    protected virtual float HeaderHeight => 0f;

    /// <summary>Fills the fixed strip; <paramref name="origin"/> is its top-left corner in the window.</summary>
    protected virtual void BuildHeader(Vector2 origin, float width) { }

    /// <summary>Adds every option, top to bottom.</summary>
    protected abstract void Build();

    /// <summary>Called when the window closes, to forget the nodes the subclass kept.</summary>
    protected virtual void OnClosed() { }

    /// <summary>Top-left corner of the scrolling area in the window. By default it fills the window under the header strip.</summary>
    protected virtual Vector2 ScrollPosition => ContentStartPosition + new Vector2(0f, HeaderHeight);

    /// <summary>Size of the scrolling area.</summary>
    protected virtual Vector2 ScrollSize => ContentSize - new Vector2(0f, HeaderHeight);

    private bool _rebuild;

    /// <summary>Asks for the scrolling options to be built again on the next frame (never while one of them is handling its own click).</summary>
    protected void RequestRebuild() => _rebuild = true;

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        CreateScroll();

        // After the scrolling area, so its drop-down opens on top of the options.
        if (HeaderHeight > 0f) BuildHeader(ContentStartPosition, ContentSize.X);

        BuildContent();
    }

    private void CreateScroll()
    {
        _scroll = new ScrollingNode<ResNode>
        {
            Position          = ScrollPosition,
            Size              = ScrollSize,
            AutoHideScrollBar = true,
        };
        _scroll.AttachNode(this);
    }

    private void BuildContent()
    {
        ContentW = ScrollSize.X - ScrollBarW - Pad * 2;
        Y        = 4f;
        Build();
        FinishLayout();
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);
        if (!_rebuild || _scroll is null) return;

        _rebuild = false;
        _scroll.Dispose();
        CreateScroll();
        BuildContent();
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _scroll = null;
        OnClosed();
        base.OnFinalize(addon);
    }

    /// <summary>Makes the scrolling area exactly as tall as what was added, so it scrolls as far as there is content.</summary>
    protected void FinishLayout()
    {
        if (_scroll is null) return;

        var height = MathF.Max(_scroll.Height, Y + 12f);
        _scroll.ContentNode.Height = height;
        _scroll.RecalculateSizes();
    }

    // ── Building blocks ───────────────────────────────────────────────────────

    /// <summary>A block of wrapping text at the cursor; returns it and moves the cursor below it.</summary>
    protected TextNode Note(string text, float x, float width, Vector4 color, uint size = 12)
    {
        var node = new TextNode
        {
            String        = text,
            Position      = new Vector2(x, Y),
            Size          = new Vector2(width, size + 6f),
            TextColor     = color,
            FontSize      = size,
            LineSpacing   = 17,
            AlignmentType = AlignmentType.TopLeft,
        };
        node.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
        node.AttachNode(Host);

        node.Height = MathF.Max(size + 6f, node.GetTextDrawSize(false).Y + 4f);
        Y += node.Height;
        return node;
    }

    protected void Space(float height) => Y += height;

    /// <summary>A section title in the condensed game font, with a line under it.</summary>
    protected void Section(string text)
    {
        Y += 10f;
        new TextNode
        {
            String    = text,
            Position  = new Vector2(Pad, Y),
            Size      = new Vector2(ContentW, 24f),
            TextColor = Gold,
            FontType  = UiFont.Heading,
            FontSize  = UiFont.Size(20),
            CharSpacing = UiFont.Spacing,
        }.AttachNode(Host);
        Y += 26f;

        new HorizontalLineNode { Position = new Vector2(Pad, Y), Size = new Vector2(ContentW, 2f) }.AttachNode(Host);
        Y += 8f;
    }

    /// <summary>A checkbox with an optional explanation under it.</summary>
    protected CheckboxNode Toggle(string label, string? help, bool value, Action<bool> set)
    {
        var box = new CheckboxNode
        {
            String    = label,
            Position  = new Vector2(Pad, Y),
            Size      = new Vector2(ContentW, 24f),
            IsChecked = value,
        };
        box.OnClick = set;
        box.AttachNode(Host);
        Y += 28f;

        if (!string.IsNullOrEmpty(help))
            Note(help, Pad + 24f, ContentW - 24f, Muted);

        Y += 8f;
        return box;
    }

    /// <summary>A number field on the right, with its title and explanation on the left.</summary>
    protected NumericInputNode Number(string label, string? help, int min, int max, int step, int value, Action<int> set)
    {
        const float inputW = 150f;
        var textW = ContentW - inputW - 16f;
        var top   = Y;

        new TextNode
        {
            String    = label,
            Position  = new Vector2(Pad, Y),
            Size      = new Vector2(textW, 20f),
            TextColor = White,
            FontSize  = 14,
        }.AttachNode(Host);
        Y += 22f;

        if (!string.IsNullOrEmpty(help))
            Note(help, Pad, textW, Muted);

        var input = new NumericInputNode
        {
            Min      = min,
            Max      = max,
            Step     = step,
            Value    = Math.Clamp(value, min, max),
            Position = new Vector2(Pad + ContentW - inputW, top),
            Size     = new Vector2(inputW, 28f),
        };
        input.OnValueUpdate = newValue => set(Math.Clamp(newValue, min, max));
        input.AttachNode(Host);

        Y = MathF.Max(Y, top + 32f) + 8f;
        return input;
    }

    /// <summary>
    /// A language select: title on the left, a drop-down on the right and an explanation under them. Meant for
    /// <see cref="BuildHeader"/>: it is attached to the window itself, not to the scrolling options.
    /// </summary>
    protected void LanguageSelect(Vector2 origin, float width, string label, string? help,
        IReadOnlyList<(string Code, string Text)> options, Func<string> get, Action<string> set)
    {
        const float selectW = 250f;

        new TextNode
        {
            String    = label,
            Position  = origin + new Vector2(Pad, 8f),
            Size      = new Vector2(width - selectW - Pad * 3, 20f),
            TextColor = White,
            FontSize  = 14,
        }.AttachNode(this);

        // An empty code ("same as GoblinTweaks") would look like "nothing selected" to the drop-down, so it gets a name.
        const string Same = "same";
        string ToKey(string code) => code.Length == 0 ? Same : code;
        string FromKey(string key) => key == Same ? string.Empty : key;

        var keys   = options.Select(o => ToKey(o.Code)).ToList();
        var texts  = options.ToDictionary(o => ToKey(o.Code), o => o.Text);
        var select = new StringDropDownNode
        {
            Position         = origin + new Vector2(width - selectW - Pad, 4f),
            Size             = new Vector2(selectW, 26f),
            MaxListOptions   = options.Count,
            GetLabelFunction = key => texts.GetValueOrDefault(key, key),
        };
        select.OnOptionSelected = key => set(FromKey(key));
        select.AttachNode(this);

        // Set after attaching so the box shows the current choice's label instead of staying empty.
        select.Options        = keys;
        select.SelectedOption = keys.Contains(ToKey(get())) ? ToKey(get()) : Same;

        if (string.IsNullOrEmpty(help)) return;

        var note = new TextNode
        {
            String        = help,
            Position      = origin + new Vector2(Pad, 38f),
            Size          = new Vector2(width - Pad * 2 - 12f, 40f),
            TextColor     = Muted,
            FontSize      = 12,
            LineSpacing   = 17,
            AlignmentType = AlignmentType.TopLeft,
        };
        note.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
        note.AttachNode(this);
    }
}
