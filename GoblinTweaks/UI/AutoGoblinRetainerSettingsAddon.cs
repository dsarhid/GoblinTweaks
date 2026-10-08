using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>Native settings window of AutoGoblinRetainer: how the prices are chosen and how fast it works.</summary>
internal sealed unsafe class AutoGoblinRetainerSettingsAddon : NativeAddon
{
    private static readonly Vector4 Gold  = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 Muted = new(0.62f, 0.62f, 0.62f, 1f);

    private const float InputW = 150f;
    private const float RowH   = 74f;

    public AutoGoblinRetainer? Tweak { get; init; }

    private NumericInputNode? _undercut;
    private NumericInputNode? _maxCut;
    private NumericInputNode? _minPrice;
    private NumericInputNode? _delay;
    private CheckboxNode?     _pinchAfter;

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);
        if (Tweak is null) return;

        var c = ContentStartPosition;
        var y = c.Y + 6f;
        var options = Tweak.Current;

        _undercut = AddNumber(c, ref y, "Setting.Undercut", "Setting.Undercut.Help", 0, 1_000_000, 1, options.UndercutAmount,
            value => options.UndercutAmount = value);
        _maxCut = AddNumber(c, ref y, "Setting.MaxCut", "Setting.MaxCut.Help", 1, 100, 1, options.MaxUndercutPercent,
            value => options.MaxUndercutPercent = value);
        _minPrice = AddNumber(c, ref y, "Setting.MinPrice", "Setting.MinPrice.Help", 1, 999_999_999, 10, options.MinPrice,
            value => options.MinPrice = value);
        _delay = AddNumber(c, ref y, "Setting.Delay", "Setting.Delay.Help", 500, 5000, 100, options.SearchDelayMs,
            value => options.SearchDelayMs = value);

        _pinchAfter = new CheckboxNode
        {
            String      = Tweak.PinchAfterText,
            Position    = new Vector2(c.X + 6f, y + 2f),
            Size        = new Vector2(ContentSize.X - 12f, 24f),
            IsChecked   = options.PinchAfterListing,
            TextTooltip = Tweak.PinchAfterTooltip,
        };
        _pinchAfter.OnClick = isChecked => Tweak.PinchAfter = isChecked;
        _pinchAfter.AttachNode(this);
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        // The same option lives next to the list button of the sell window; keep both in step.
        if (Tweak is not null && _pinchAfter is not null && _pinchAfter.IsChecked != Tweak.PinchAfter)
            _pinchAfter.IsChecked = Tweak.PinchAfter;
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        _undercut = _maxCut = _minPrice = _delay = null;
        _pinchAfter = null;
        base.OnFinalize(addon);
    }

    /// <summary>A labelled number field: title and explanation on the left, the input on the right.</summary>
    private NumericInputNode AddNumber(Vector2 c, ref float y, string labelKey, string helpKey, int min, int max, int step, int value, Action<int> apply)
    {
        var textW = ContentSize.X - InputW - 28f;

        new TextNode
        {
            String    = Tweak!.Text(labelKey),
            Position  = new Vector2(c.X + 6f, y),
            Size      = new Vector2(textW, 20f),
            TextColor = Gold,
            FontSize  = 15,
        }.AttachNode(this);

        var help = new TextNode
        {
            String        = Tweak.Text(helpKey),
            Position      = new Vector2(c.X + 6f, y + 22f),
            Size          = new Vector2(textW, RowH - 28f),
            TextColor     = Muted,
            FontSize      = 12,
            LineSpacing   = 15,
            AlignmentType = AlignmentType.TopLeft,
        };
        help.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
        help.AttachNode(this);

        var input = new NumericInputNode
        {
            Min      = min,
            Max      = max,
            Step     = step,
            Value    = Math.Clamp(value, min, max),
            Position = new Vector2(c.X + ContentSize.X - InputW - 8f, y + 2f),
            Size     = new Vector2(InputW, 28f),
        };
        input.OnValueUpdate = newValue =>
        {
            apply(Math.Clamp(newValue, min, max));
            Tweak.SaveCurrent();
        };
        input.AttachNode(this);

        y += RowH;
        return input;
    }
}
