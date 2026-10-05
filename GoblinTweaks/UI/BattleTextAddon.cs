using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native settings window of Goblin Battle Text: general options, one tab per scroll area,
/// where each event goes, the cooldown alerts of the current job, and the colors.
/// While it is open, sample messages scroll in the areas so they can be positioned by eye.
/// </summary>
internal unsafe class BattleTextAddon : NativeAddon
{
    private static readonly Vector4 TitleGold = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 White     = new(0.93f, 0.93f, 0.93f, 1f);
    private static readonly Vector4 MutedGrey = new(0.60f, 0.60f, 0.60f, 1f);

    private const float TabsH     = 28f;
    private const float RowH      = 36f;
    private const float CellH     = 30f;
    private const float LabelW    = 260f;
    private const float HintH     = 40f;
    private const float DefaultsW = 190f;
    private const uint  TextSize  = 14;

    private static readonly string[] HelpTopics = ["Overview", "General", "Areas", "Events", "Cooldowns", "Highlights", "Colors"];

    /// <summary>One "order of the parts" list: the parts, which of them are hidden, and the labels showing them.</summary>
    private sealed record OrderList(List<GoblinBattleText.BattleTextPart> Order, List<GoblinBattleText.BattleTextPart> Hidden, string PartKey)
    {
        public List<TextNode> Labels { get; } = [];
    }

    /// <summary>A tab: its container and what to run to show the current settings again in its controls.</summary>
    private sealed record Page(ResNode Node)
    {
        public List<Action> Refresh { get; } = [];
    }

    private readonly List<Page> _pages = [];

    // Drop-downs are attached last, bottom one first, so an open list draws over the rows below it.
    private readonly List<NodeBase> _dropDowns = [];
    private TextHelpAddon? _helpAddon;
    private Page? _page;
    private int _selected;
    private float _width;
    private float _pageHeight;
    private float _y;

    public required GoblinBattleText Tweak { get; init; }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c  = ContentStartPosition;
        var cs = ContentSize;
        _width      = cs.X;
        _pageHeight = cs.Y - TabsH - 10f - HintH;

        var tabs = new TabBarNode
        {
            Position = c,
            Size     = new Vector2(cs.X, TabsH),
        };
        tabs.AttachNode(this);

        // One page per tab, in the order of BattleTextTab.
        var pagePos = new Vector2(c.X, c.Y + TabsH + 10f);
        foreach (var tab in Enum.GetValues<GoblinBattleText.BattleTextTab>())
        {
            var index = _pages.Count;
            tabs.AddTab(Tweak.Text($"Tab.{tab}"), () => Select(index));

            BeginPage(pagePos);
            switch (tab)
            {
                case GoblinBattleText.BattleTextTab.General:   BuildGeneral(); break;
                case GoblinBattleText.BattleTextTab.Outgoing:  BuildArea(Tweak.Config.Outgoing); break;
                case GoblinBattleText.BattleTextTab.Incoming:  BuildArea(Tweak.Config.Incoming); break;
                case GoblinBattleText.BattleTextTab.Center:    BuildArea(Tweak.Config.Center, withLimit: true); break;
                case GoblinBattleText.BattleTextTab.Events:    BuildEvents(); break;
                case GoblinBattleText.BattleTextTab.Cooldowns: BuildCooldowns(); break;
                case GoblinBattleText.BattleTextTab.Highlights: BuildHighlights(); break;
                case GoblinBattleText.BattleTextTab.Colors:    BuildColors(); break;
            }
            EndPage();
        }

        BuildBottomBar(new Vector2(c.X, c.Y + cs.Y - HintH), cs.X);

        Select(0);
        Tweak.Preview = true;
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        Tweak.Preview = false;
        Tweak.FlushSettings();

        _pages.Clear();
        _dropDowns.Clear();
        _page = null;

        _helpAddon?.Close();
        _helpAddon = null;

        base.OnFinalize(addon);
    }

    private void Select(int index)
    {
        _selected = index;
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].Node.IsVisible = i == index;
    }

    private void BuildBottomBar(Vector2 pos, float width)
    {
        new HorizontalLineNode
        {
            Position = pos,
            Size     = new Vector2(width, 2f),
        }.AttachNode(this);

        var help = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(pos.X + 4f, pos.Y + 10f),
            Size        = new Vector2(24f, 24f),
            TextTooltip = Tweak.Text("Help.Title"),
        };
        help.OnClick = OpenHelp;
        help.AttachNode(this);

        new TextNode
        {
            String    = Tweak.Text("Preview.Hint"),
            Position  = new Vector2(pos.X + 38f, pos.Y + 13f),
            Size      = new Vector2(width - 50f - DefaultsW, 18f),
            TextColor = MutedGrey,
            FontSize  = 13,
        }.AttachNode(this);

        var defaults = new TextButtonNode
        {
            String      = Tweak.Text("Defaults"),
            Position    = new Vector2(pos.X + width - DefaultsW - 4f, pos.Y + 9f),
            Size        = new Vector2(DefaultsW, 26f),
            TextTooltip = Tweak.Text("Defaults.Help"),
        };
        defaults.OnClick = RestoreDefaults;
        defaults.AttachNode(this);
    }

    /// <summary>Puts the tab on screen back to its defaults and shows them in its controls.</summary>
    private void RestoreDefaults()
    {
        if (_selected < 0 || _selected >= _pages.Count) return;

        Tweak.ResetTab((GoblinBattleText.BattleTextTab)_selected);
        foreach (var refresh in _pages[_selected].Refresh)
            refresh();
    }

    private void OpenHelp()
    {
        _helpAddon ??= new TextHelpAddon
        {
            InternalName = "GtkBattleTextHelp",
            Title        = Tweak.Text("Help.Title"),
            Size         = new Vector2(860f, 600f),
            Pages        = [.. HelpTopics.Select(topic => (Tweak.Text($"Help.{topic}.Title"), Tweak.Text($"Help.{topic}.Text")))],
        };
        _helpAddon.Open();
    }

    // ── Pages ───────────────────────────────────────────────────────────────────

    private void BuildGeneral()
    {
        var options = Tweak.Config;

        AddCheck("MergeHits",       () => options.MergeHits,       value => options.MergeHits = value);
        AddCheck("Abbreviate",      () => options.Abbreviate,      value => options.Abbreviate = value);
        AddCheck("IncludePets",     () => options.IncludePets,     value => options.IncludePets = value);
        AddCheck("ShowFading",      () => options.ShowFading,      value => options.ShowFading = value);
        AddDropDown("Font", "Font", () => options.Font, value => options.Font = value);
    }

    /// <param name="withLimit">Also offer how many messages show at once: for the centre, where they pile up.</param>
    private void BuildArea(GoblinBattleText.AreaOptions area, bool withLimit = false)
    {
        AddCheck("Area.Enabled", () => area.Enabled, value => area.Enabled = value);
        AddSlider("Area.OffsetX",  -900, 900, 5,   () => area.OffsetX,        value => area.OffsetX = value);
        AddSlider("Area.OffsetY",  -600, 600, 5,   () => area.OffsetY,        value => area.OffsetY = value);
        AddSlider("Area.Anchor",      0, 100, 1,   () => area.TextAnchor,     value => area.TextAnchor = value);
        AddSlider("Area.Height",     40, 700, 10,  () => area.Height,         value => area.Height = value);
        AddSlider("Area.FontSize",   12,  48, 1,   () => area.FontSize,       value => area.FontSize = value);
        AddSlider("Area.Duration",   10,  60, 1,   () => area.DurationTenths, value => area.DurationTenths = value);
        AddSlider("Area.MinAmount",   0, 20000, 100, () => area.MinAmount,    value => area.MinAmount = value);
        if (withLimit)
            AddSlider("Area.MaxMessages", 1, 10, 1, () => area.MaxMessages, value => area.MaxMessages = value);
        AddDropDown("Area.Style", "Style", () => area.Style, value => area.Style = value);

        AddHeader("Order");
        AddOrderList(new OrderList(area.Order, area.Hidden, "Part"));
    }

    private void BuildEvents()
    {
        foreach (var type in Enum.GetValues<GoblinBattleText.BattleTextEvent>())
        {
            // Cooldown alerts have their own tab.
            if (type != GoblinBattleText.BattleTextEvent.Cooldown)
                AddEventRow(type);
        }
    }

    private void BuildCooldowns()
    {
        var options = Tweak.Config;

        AddEventRow(GoblinBattleText.BattleTextEvent.Cooldown);

        AddHeader("CooldownOrder");
        AddOrderList(new OrderList(options.CooldownOrder, options.CooldownHidden, "CooldownPart"));

        AddHeader("Cooldowns");
        var actions = Tweak.CooldownActions();
        if (actions.Count == 0)
        {
            new TextNode
            {
                String    = Tweak.Text("Cooldowns.None"),
                Position  = new Vector2(4f, _y + 4f),
                Size      = new Vector2(_width - 8f, 18f),
                TextColor = MutedGrey,
                FontSize  = TextSize,
            }.AttachNode(_page!.Node);
            return;
        }

        // A grid filled column by column, with as many columns as the list needs to fit the page.
        var rows    = Math.Max(1, (int)((_pageHeight - _y) / CellH));
        var columns = (actions.Count + rows - 1) / rows;
        var cellW   = _width / columns;

        for (var i = 0; i < actions.Count; i++)
        {
            var (id, name, iconId) = actions[i];
            var x = i / rows * cellW;
            var y = _y + i % rows * CellH;

            new IconImageNode
            {
                IconId     = iconId,
                FitTexture = true,
                Position   = new Vector2(x + 4f, y + 2f),
                Size       = new Vector2(24f, 24f),
            }.AttachNode(_page!.Node);

            var check = new CheckboxNode
            {
                String    = name,
                IsChecked = !options.CooldownsOff.Contains(id),
                Position  = new Vector2(x + 34f, y + 3f),
                Size      = new Vector2(cellW - 38f, 22f),
            };
            check.OnClick = isChecked => Tweak.SetCooldown(id, isChecked);
            check.AttachNode(_page.Node);
            _page.Refresh.Add(() => check.IsChecked = !options.CooldownsOff.Contains(id));
        }
    }

    /// <summary>One block per kind of highlighted message: on/off and color, then font and animation, then size and intensity.</summary>
    private void BuildHighlights()
    {
        var half     = _width / 2f;
        var captionW = 100f;

        foreach (var kind in Enum.GetValues<GoblinBattleText.BattleTextHighlight>())
        {
            var options = Tweak.Highlight(kind);

            AddCheckAt(new Vector2(8f, _y + 7f), half - 16f, $"Highlight.{kind}", () => options.Enabled, value => options.Enabled = value, newRow: false);
            AddColorAt(new Vector2(half + 10f, _y + 4f), half - 22f, $"Highlight.Color.{kind}", () => options.Color, GoblinBattleText.DefaultHighlight(kind).Color, value => options.Color = value);
            _y += RowH;

            AddLabelAt(new Vector2(30f, _y + 9f), captionW, "Highlight.Font");
            AddDropDownAt(new Vector2(30f + captionW, _y + 6f), half - captionW - 50f, "Highlight.Font", "Font",
                () => options.Font, value => options.Font = value);
            AddLabelAt(new Vector2(half + 10f, _y + 9f), captionW, "Highlight.Animation");
            AddDropDownAt(new Vector2(half + 10f + captionW, _y + 6f), half - captionW - 22f, "Highlight.Animation", "Animation",
                () => options.Animation, value => options.Animation = value);
            _y += RowH;

            AddLabelAt(new Vector2(30f, _y + 9f), captionW, "Highlight.FontSize");
            AddSliderAt(new Vector2(30f + captionW, _y + 9f), half - captionW - 50f, "Highlight.FontSize", 12, 64, 1,
                () => options.FontSize, value => options.FontSize = value);
            AddLabelAt(new Vector2(half + 10f, _y + 9f), captionW, "Highlight.Intensity");
            AddSliderAt(new Vector2(half + 10f + captionW, _y + 9f), half - captionW - 22f, "Highlight.Intensity", 0, 300, 10,
                () => options.Intensity, value => options.Intensity = value);
            _y += RowH + 8f;
        }
    }

    private void BuildColors()
    {
        var colors = Tweak.Config.Colors;

        AddColor("Color.OutgoingDamage",    () => colors.OutgoingDamage,    GoblinBattleText.DefaultColors.OutgoingDamage,    value => colors.OutgoingDamage = value);
        AddColor("Color.IncomingDamage",    () => colors.IncomingDamage,    GoblinBattleText.DefaultColors.IncomingDamage,    value => colors.IncomingDamage = value);
        AddColor("Color.Heal",              () => colors.Heal,              GoblinBattleText.DefaultColors.Heal,              value => colors.Heal = value);
        AddColor("Color.Miss",              () => colors.Miss,              GoblinBattleText.DefaultColors.Miss,              value => colors.Miss = value);
        AddColor("Color.Mp",                () => colors.Mp,                GoblinBattleText.DefaultColors.Mp,                value => colors.Mp = value);
        AddColor("Color.Buff",              () => colors.Buff,              GoblinBattleText.DefaultColors.Buff,              value => colors.Buff = value);
        AddColor("Color.BuffEnd",           () => colors.BuffEnd,           GoblinBattleText.DefaultColors.BuffEnd,           value => colors.BuffEnd = value);
        AddColor("Color.Debuff",            () => colors.Debuff,            GoblinBattleText.DefaultColors.Debuff,            value => colors.Debuff = value);
        AddColor("Color.DebuffEnd",         () => colors.DebuffEnd,         GoblinBattleText.DefaultColors.DebuffEnd,         value => colors.DebuffEnd = value);
        AddColor("Color.Cooldown",          () => colors.Cooldown,          GoblinBattleText.DefaultColors.Cooldown,          value => colors.Cooldown = value);
    }

    private void BeginPage(Vector2 position)
    {
        var node = new ResNode
        {
            Position = position,
            Size     = new Vector2(_width, _pageHeight),
        };
        node.AttachNode(this);

        _page = new Page(node);
        _pages.Add(_page);
        _y = 0f;
    }

    private void EndPage()
    {
        for (var i = _dropDowns.Count - 1; i >= 0; i--)
            _dropDowns[i].AttachNode(_page!.Node);
        _dropDowns.Clear();
    }

    // ── Rows ────────────────────────────────────────────────────────────────────
    // Every row takes its label from "<key>" and its tooltip from "<key>.Help", reads its value with
    // "get" (again after the tab is put back to its defaults) and writes it with "set".

    private void AddCheck(string key, Func<bool> get, Action<bool> set)
        => AddCheckAt(new Vector2(8f, _y + 7f), _width - 16f, key, get, set, newRow: true);

    private void AddCheckAt(Vector2 position, float width, string key, Func<bool> get, Action<bool> set, bool newRow)
    {
        var check = new CheckboxNode
        {
            String      = Tweak.Text(key),
            IsChecked   = get(),
            Position    = position,
            Size        = new Vector2(width, 22f),
            TextTooltip = Tweak.Text(key + ".Help"),
        };
        check.OnClick = isChecked => { set(isChecked); Tweak.Changed(); };
        check.AttachNode(_page!.Node);
        _page.Refresh.Add(() => check.IsChecked = get());

        if (newRow)
            _y += RowH;
    }

    private void AddSlider(string key, int min, int max, int step, Func<int> get, Action<int> set)
    {
        AddLabel(key);
        AddSliderAt(new Vector2(LabelW, _y + 9f), _width - LabelW - 12f, key, min, max, step, get, set);
        _y += RowH;
    }

    private void AddSliderAt(Vector2 position, float width, string key, int min, int max, int step, Func<int> get, Action<int> set)
    {
        var slider = new SliderNode
        {
            Position    = position,
            Size        = new Vector2(width, 18f),
            Step        = step,
            TextTooltip = Tweak.Text(key + ".Help"),
        };

        // SliderNode.Range is a C# Range, which cannot start below zero: set the bounds on the native slider.
        slider.Component->SetMaxValue(max);
        slider.Component->SetMinValue(min);
        slider.Value = Math.Clamp(get(), min, max);

        slider.OnValueChanged = changed => { set(changed); Tweak.Changed(); };
        slider.AttachNode(_page!.Node);
        _page.Refresh.Add(() => slider.Value = Math.Clamp(get(), min, max));
    }

    private void AddDropDown<T>(string key, string labelKey, Func<T> get, Action<T> set) where T : struct, Enum
    {
        AddLabel(key);
        AddDropDownAt(new Vector2(LabelW, _y + 6f), _width - LabelW - 12f, key, labelKey, get, set);
        _y += RowH;
    }

    private void AddDropDownAt<T>(Vector2 position, float width, string key, string labelKey, Func<T> get, Action<T> set) where T : struct, Enum
    {
        var dropDown = new EnumDropDownNode<T>
        {
            Position         = position,
            Size             = new Vector2(width, 24f),
            MaxListOptions   = Math.Min(Enum.GetValues<T>().Length, 12),

            // Before the options: the text of the selected one is taken when it is set.
            GetLabelFunction = option => Tweak.Text($"{labelKey}.{option}"),
            Options          = [.. Enum.GetValues<T>()],
            SelectedOption   = get(),
            TextTooltip      = Tweak.Text(key + ".Help"),
        };
        dropDown.OnOptionSelected = selected => { set(selected); Tweak.Changed(); };
        _dropDowns.Add(dropDown);
        _page!.Refresh.Add(() => dropDown.SelectedOption = get());
    }

    /// <summary>One event: whether it shows, the area it appears in, and how it moves there.</summary>
    private void AddEventRow(GoblinBattleText.BattleTextEvent type)
    {
        var options = Tweak.Event(type);
        var nameW   = _width * 0.40f;
        var choiceW = _width * 0.30f;

        AddCheckAt(new Vector2(8f, _y + 7f), nameW - 16f, $"Event.{type}",
            () => options.Enabled, value => options.Enabled = value, newRow: false);
        AddDropDownAt(new Vector2(nameW, _y + 6f), choiceW - 12f, "Event.Area", "AreaName",
            () => options.Area, value => options.Area = value);
        AddDropDownAt(new Vector2(nameW + choiceW, _y + 6f), choiceW - 12f, "Event.Motion", "Motion",
            () => options.Motion ?? GoblinBattleText.BattleTextMotion.Up, value => options.Motion = value);

        _y += RowH;
    }

    private void AddColor(string key, Func<Vector4> get, Vector4 defaultColor, Action<Vector4> set)
    {
        AddColorAt(new Vector2(8f, _y + 4f), _width - 16f, key, get, defaultColor, set);
        _y += RowH;
    }

    private void AddColorAt(Vector2 position, float width, string key, Func<Vector4> get, Vector4? defaultColor, Action<Vector4> set)
    {
        var color = new ColorEditNode
        {
            String       = Tweak.Text(key),
            CurrentColor = get(),
            DefaultColor = defaultColor,
            Position     = position,
            Size         = new Vector2(width, 28f),
        };
        color.OnColorConfirmed = confirmed => { set(confirmed); Tweak.Changed(); };
        color.AttachNode(_page!.Node);
        _page.Refresh.Add(() => color.CurrentColor = get());
    }

    private void AddHeader(string key)
    {
        _y += 10f;

        new TextNode
        {
            String      = Tweak.Text(key),
            Position    = new Vector2(8f, _y + 2f),
            Size        = new Vector2(_width - 16f, 20f),
            TextColor   = TitleGold,
            FontSize    = 15,
            TextTooltip = Tweak.Text(key + ".Help"),
        }.AttachNode(_page!.Node);

        new HorizontalLineNode
        {
            Position = new Vector2(0f, _y + 24f),
            Size     = new Vector2(_width, 2f),
        }.AttachNode(_page.Node);

        _y += 32f;
    }

    private void AddLabel(string key) => AddLabelAt(new Vector2(8f, _y + 9f), LabelW - 16f, key);

    private void AddLabelAt(Vector2 position, float width, string key)
    {
        new TextNode
        {
            String      = Tweak.Text(key),
            Position    = position,
            Size        = new Vector2(width, 20f),
            TextColor   = White,
            FontSize    = TextSize,
            TextTooltip = Tweak.Text(key + ".Help"),
        }.AttachNode(_page!.Node);
    }

    // ── Order and visibility of the parts of a message ──────────────────────────

    /// <summary>One row per part, with buttons to move it earlier or later and to hide it.</summary>
    private void AddOrderList(OrderList list)
    {
        for (var i = 0; i < list.Order.Count; i++)
        {
            var index = i;
            var label = new TextNode
            {
                Position = new Vector2(8f, _y + 6f),
                Size     = new Vector2(_width - 130f, 20f),
                FontSize = TextSize,
            };
            label.AttachNode(_page!.Node);
            list.Labels.Add(label);

            AddOrderButton(CircleButtonIcon.UpArrow,   _width - 100f, "Order.Up",   () => Move(list, index, index - 1));
            AddOrderButton(CircleButtonIcon.ArrowDown, _width - 70f,  "Order.Down", () => Move(list, index, index + 1));
            AddOrderButton(CircleButtonIcon.Eye,       _width - 40f,  "Order.Hide", () => ToggleHidden(list, index));

            _y += CellH;
        }

        Refresh(list);
        _page!.Refresh.Add(() => Refresh(list));
    }

    private void AddOrderButton(CircleButtonIcon icon, float x, string tooltipKey, Action onClick)
    {
        var button = new CircleButtonNode
        {
            Icon        = icon,
            Position    = new Vector2(x, _y + 3f),
            Size        = new Vector2(24f, 24f),
            TextTooltip = Tweak.Text(tooltipKey),
        };
        button.OnClick = onClick;
        button.AttachNode(_page!.Node);
    }

    private void Move(OrderList list, int from, int to)
    {
        var order = list.Order;
        if (from < 0 || to < 0 || from >= order.Count || to >= order.Count) return;

        (order[from], order[to]) = (order[to], order[from]);
        Refresh(list);
        Tweak.Changed();
    }

    private void ToggleHidden(OrderList list, int index)
    {
        if (index >= list.Order.Count) return;

        var part = list.Order[index];
        if (!list.Hidden.Remove(part))
            list.Hidden.Add(part);

        Refresh(list);
        Tweak.Changed();
    }

    private void Refresh(OrderList list)
    {
        for (var i = 0; i < list.Labels.Count && i < list.Order.Count; i++)
        {
            var part   = list.Order[i];
            var hidden = list.Hidden.Contains(part);
            var name   = Tweak.Text($"{list.PartKey}.{part}");

            list.Labels[i].String    = hidden ? $"{i + 1}. {name} {Tweak.Text("Order.Hidden")}" : $"{i + 1}. {name}";
            list.Labels[i].TextColor = hidden ? MutedGrey : White;
        }
    }
}
