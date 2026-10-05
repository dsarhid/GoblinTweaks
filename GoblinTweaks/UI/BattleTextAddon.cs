using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Tweaks;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Native settings window of Goblin Battle Text: general options and colors, the scroll areas,
/// where each event goes, the look of special hits, and the cooldown alerts of the current job.
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
    private const float ChipH     = 58f;
    private const float CaptionW  = 130f;
    private const float NumberW   = 100f;
    private const float PresetW   = 34f;
    private const float IconCell  = 46f;
    private const float HintH     = 40f;
    private const float DefaultsW = 100f;   // the hold button of the game has this width
    private const uint  TextSize  = 14;

    private static readonly string[] HelpTopics = ["Overview", "General", "Areas", "Events", "Highlights", "Cooldowns"];

    /// <summary>
    /// One "order of the parts" row: the parts, which of them are hidden, which of them apply, and
    /// the chips showing them. A part that does not apply has no chip.
    /// </summary>
    private sealed class OrderList
    {
        public required Func<List<GoblinBattleText.BattleTextPart>> Order { get; init; }
        public required Func<List<GoblinBattleText.BattleTextPart>> Hidden { get; init; }
        public required Func<GoblinBattleText.BattleTextPart, bool> Applies { get; init; }
        public required string PartKey { get; init; }
        public List<(ResNode Chip, TextNode Label)> Chips { get; } = [];
        public TextNode? Empty { get; set; }
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
    private Page? _areaPage;
    private OrderList? _areaOrder;
    private SegmentSwitchNode? _areaSwitch;
    private KamiToolKit.ContextMenu.ContextMenu? _areaMenu;
    private GoblinBattleText.BattleTextArea _area;
    private int _selected;
    private float _width;
    private float _pageHeight;
    private float _y;

    public required GoblinBattleText Tweak { get; init; }

    /// <summary>The area the controls of the Areas tab show and edit.</summary>
    private GoblinBattleText.AreaOptions CurrentArea => Tweak.Area(_area);

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c  = ContentStartPosition;
        var cs = ContentSize;
        _width      = cs.X;
        _pageHeight = cs.Y - TabsH - 10f - HintH;
        _area       = GoblinBattleText.BattleTextArea.Outgoing;

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
                case GoblinBattleText.BattleTextTab.General:    BuildGeneral(); break;
                case GoblinBattleText.BattleTextTab.Areas:      BuildAreas(); break;
                case GoblinBattleText.BattleTextTab.Events:     BuildEvents(); break;
                case GoblinBattleText.BattleTextTab.Highlights: BuildHighlights(); break;
                case GoblinBattleText.BattleTextTab.Cooldowns:  BuildCooldowns(); break;
            }
            EndPage();
        }

        BuildBottomBar(new Vector2(c.X, c.Y + cs.Y - HintH), cs.X);

        Select(0);
        Tweak.AreaMoved = OnAreaMoved;
        Tweak.Preview   = true;
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        Tweak.Preview   = false;
        Tweak.AreaMoved = null;
        Tweak.FlushSettings();

        _areaMenu?.Dispose();
        _areaMenu = null;

        _pages.Clear();
        _dropDowns.Clear();
        _page      = null;
        _areaPage   = null;
        _areaOrder  = null;
        _areaSwitch = null;

        _helpAddon?.Close();
        _helpAddon = null;

        base.OnFinalize(addon);
    }

    private void Select(int index)
    {
        _selected = index;
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].Node.IsVisible = i == index;

        Tweak.PreviewTab = (GoblinBattleText.BattleTextTab)index;

        // The parts that apply to an area depend on the events sent to it, which another tab may have changed.
        if (_areaOrder is not null)
            Refresh(_areaOrder);
    }

    /// <summary>Shows another area in the controls of the Areas tab.</summary>
    private void SelectArea(GoblinBattleText.BattleTextArea area)
    {
        _area = area;
        if (_areaPage is null) return;

        foreach (var refresh in _areaPage.Refresh)
            refresh();
    }

    /// <summary>An area was dragged by its handle: the Areas tab shows it, with its new position.</summary>
    private void OnAreaMoved(GoblinBattleText.BattleTextArea area)
    {
        _areaSwitch?.Selected = (int)area;
        SelectArea(area);
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

        // Held down, like the game's own buttons for what cannot be undone.
        var defaults = new HoldButtonNode
        {
            String           = Tweak.Text("Defaults"),
            Position         = new Vector2(pos.X + width - DefaultsW - 4f, pos.Y + 3f),
            Size             = new Vector2(DefaultsW, 36f),
            TextTooltip      = Tweak.Text("Defaults.Help"),
            UnlockAfterClick = true,
        };
        defaults.OnClick = RestoreDefaults;
        defaults.AttachNode(this);
    }

    /// <summary>Puts the tab on screen (in the Areas tab, the area on screen) back to its defaults and shows them in its controls.</summary>
    private void RestoreDefaults()
    {
        if (_selected < 0 || _selected >= _pages.Count) return;

        Tweak.ResetTab((GoblinBattleText.BattleTextTab)_selected, _area);
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
        var colors  = options.Colors;

        AddCheckCell(0, "MergeHits",  () => options.MergeHits,  value => options.MergeHits = value);
        AddCheckCell(1, "Abbreviate", () => options.Abbreviate, value => options.Abbreviate = value);
        _y += RowH;

        AddCheckCell(0, "IncludePets", () => options.IncludePets, value => options.IncludePets = value);
        AddDropDownCell(1, "Font", "Font", () => options.Font, value => options.Font = value);
        _y += RowH;

        AddHeader("Colors");

        AddColorCell(0, "Color.OutgoingDamage", () => colors.OutgoingDamage, GoblinBattleText.DefaultColors.OutgoingDamage, value => colors.OutgoingDamage = value);
        AddColorCell(1, "Color.IncomingDamage", () => colors.IncomingDamage, GoblinBattleText.DefaultColors.IncomingDamage, value => colors.IncomingDamage = value);
        _y += RowH;

        AddColorCell(0, "Color.Heal", () => colors.Heal, GoblinBattleText.DefaultColors.Heal, value => colors.Heal = value);
        AddColorCell(1, "Color.Miss", () => colors.Miss, GoblinBattleText.DefaultColors.Miss, value => colors.Miss = value);
        _y += RowH;

        AddColorCell(0, "Color.Buff",    () => colors.Buff,    GoblinBattleText.DefaultColors.Buff,    value => colors.Buff = value);
        AddColorCell(1, "Color.BuffEnd", () => colors.BuffEnd, GoblinBattleText.DefaultColors.BuffEnd, value => colors.BuffEnd = value);
        _y += RowH;

        AddColorCell(0, "Color.Debuff",    () => colors.Debuff,    GoblinBattleText.DefaultColors.Debuff,    value => colors.Debuff = value);
        AddColorCell(1, "Color.DebuffEnd", () => colors.DebuffEnd, GoblinBattleText.DefaultColors.DebuffEnd, value => colors.DebuffEnd = value);
        _y += RowH;

        AddColorCell(0, "Color.Mp",     () => colors.Mp,     GoblinBattleText.DefaultColors.Mp,     value => colors.Mp = value);
        AddColorCell(1, "Color.Action", () => colors.Action, GoblinBattleText.DefaultColors.Action, value => colors.Action = value);
        _y += RowH;
    }

    /// <summary>One set of controls for the three areas: the switch at the top chooses which one they show and edit.</summary>
    private void BuildAreas()
    {
        _areaPage = _page;

        // The switch of the game's Character window (DoW/DoM | DoH/DoL), here between the areas.
        var areas = Enum.GetValues<GoblinBattleText.BattleTextArea>();
        _areaSwitch = new SegmentSwitchNode([.. areas.Select(area => Tweak.Text($"AreaName.{area}"))], _width * 0.6f)
        {
            Position = new Vector2(0f, _y),
        };
        _areaSwitch.OnSelected = index => SelectArea(areas[index]);
        _areaSwitch.AttachNode(_page!.Node);

        var menu = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = new Vector2(_width - 32f, _y + 2f),
            Size        = new Vector2(24f, 24f),
            TextTooltip = Tweak.Text("Area.Menu"),
        };
        menu.OnClick = OpenAreaMenu;
        menu.AttachNode(_page.Node);
        _y += SegmentSwitchNode.SwitchHeight + 8f;

        AddCheckCell(0, "Area.Enabled", () => CurrentArea.Enabled, value => CurrentArea.Enabled = value);
        AddDropDownCell(1, "Area.Style", "Style", () => CurrentArea.Style, value => CurrentArea.Style = value);
        _y += RowH;

        AddPositionCell(0, "Area.OffsetX", GoblinBattleText.OffsetLimitX, () => CurrentArea.OffsetX, value => CurrentArea.OffsetX = value);
        AddPositionCell(1, "Area.OffsetY", GoblinBattleText.OffsetLimitY, () => CurrentArea.OffsetY, value => CurrentArea.OffsetY = value);
        _y += RowH;

        AddAnchorCell(0);
        AddSliderCell(1, "Area.Height", 40, 700, 10, () => CurrentArea.Height, value => CurrentArea.Height = value);
        _y += RowH;

        AddSliderCell(0, "Area.FontSize", 12, 48, 1, () => CurrentArea.FontSize, value => CurrentArea.FontSize = value);
        AddDurationCell(1);
        _y += RowH;

        AddSliderCell(0, "Area.MinAmount",   0, 20000, 100, () => CurrentArea.MinAmount, value => CurrentArea.MinAmount = value);
        AddSliderCell(1, "Area.MaxMessages", 1, BattleTextAreaNode.MaxMessages, 1, () => CurrentArea.MaxMessages, value => CurrentArea.MaxMessages = value);
        _y += RowH;

        AddHeader("Order");
        _areaOrder = new OrderList
        {
            Order   = () => CurrentArea.Order,
            Hidden  = () => CurrentArea.Hidden,
            Applies = part => Tweak.PartApplies(_area, part),
            PartKey = "Part",
        };
        AddOrderList(_areaOrder, Enum.GetValues<GoblinBattleText.BattleTextPart>().Length);
    }

    /// <summary>The game's own context menu, with what can be done to the area on screen.</summary>
    private void OpenAreaMenu()
    {
        _areaMenu ??= new KamiToolKit.ContextMenu.ContextMenu();
        _areaMenu.Clear();

        var from = _area;
        foreach (var area in Enum.GetValues<GoblinBattleText.BattleTextArea>())
        {
            if (area == from) continue;

            var to = area;
            _areaMenu.AddItem(string.Format(Tweak.Text("Area.CopyTo"), Tweak.Text($"AreaName.{to}")), () => Tweak.CopyAreaLook(from, to));
        }

        _areaMenu.Open();
    }

    /// <summary>
    /// A position: a slider for a rough value and a number box, with the game's + and - buttons, for an exact one.
    /// Each shows what the other sets.
    /// </summary>
    private void AddPositionCell(int column, string key, int limit, Func<int> get, Action<int> set)
    {
        var x = CellX(column);
        AddLabelAt(new Vector2(x + 8f, _y + 9f), CaptionW - 8f, key);

        var syncing = false;
        var number  = new NumericInputNode
        {
            Position    = new Vector2(x + _width / 2f - NumberW - 12f, _y + 3f),
            Size        = new Vector2(NumberW, 28f),
            Min         = -limit,
            Max         = limit,
            Step        = 1,
            TextTooltip = Tweak.Text(key + ".Help"),
        };
        number.Value = Math.Clamp(get(), -limit, limit);

        var slider = AddSliderAt(new Vector2(x + CaptionW, _y + 9f), _width / 2f - CaptionW - NumberW - 20f, key, -limit, limit, 1, get, value =>
        {
            if (syncing) return;

            syncing = true;
            try
            {
                set(value);
                number.Value = value;
            }
            finally
            {
                syncing = false;
            }
        });

        // The number box shows the value: the slider's own would be the same number twice.
        slider.ValueNode.IsVisible = false;

        number.OnValueUpdate = value =>
        {
            if (syncing) return;

            syncing = true;
            try
            {
                value = Math.Clamp(value, -limit, limit);
                set(value);
                slider.Value = value;
                Tweak.Changed();
            }
            finally
            {
                syncing = false;
            }
        };
        number.AttachNode(_page!.Node);

        // After the slider's own refresh, which reads the same value.
        _page.Refresh.Add(() =>
        {
            syncing = true;
            try
            {
                number.Value = Math.Clamp(get(), -limit, limit);
            }
            finally
            {
                syncing = false;
            }
        });
    }

    /// <summary>The alignment: any value with the slider, or its three usual ones with a button each.</summary>
    private void AddAnchorCell(int column)
    {
        const string key = "Area.Anchor";

        var x       = CellX(column);
        var presets = new[] { (Value: 0, Key: "Start"), (Value: 50, Key: "Middle"), (Value: 100, Key: "End") };
        var presetsW = presets.Length * PresetW;

        AddLabelAt(new Vector2(x + 8f, _y + 9f), CaptionW - 8f, key);
        var slider = AddSliderAt(new Vector2(x + CaptionW, _y + 9f), _width / 2f - CaptionW - presetsW - 20f, key, 0, 100, 1,
            () => CurrentArea.TextAnchor, value => CurrentArea.TextAnchor = value);

        for (var i = 0; i < presets.Length; i++)
        {
            var preset = presets[i];
            var button = new TextButtonNode
            {
                String      = preset.Value.ToString(),
                Position    = new Vector2(x + _width / 2f - presetsW - 12f + i * PresetW, _y + 5f),
                Size        = new Vector2(PresetW - 2f, 24f),
                TextTooltip = Tweak.Text($"{key}.{preset.Key}"),
            };
            button.OnClick = () =>
            {
                CurrentArea.TextAnchor = preset.Value;
                slider.Value = preset.Value;
                Tweak.Changed();
            };
            button.AttachNode(_page!.Node);
        }
    }

    /// <summary>The duration: saved in tenths of a second, which is what the slider moves, and shown in seconds in its label.</summary>
    private void AddDurationCell(int column)
    {
        const string key = "Area.Duration";

        var x     = CellX(column);
        var label = AddLabelAt(new Vector2(x + 8f, _y + 9f), CaptionW - 8f, key);

        void ShowSeconds()
            => label.String = string.Format(Tweak.Text("Area.Duration.Value"), (CurrentArea.DurationTenths / 10f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));

        var slider = AddSliderAt(new Vector2(x + CaptionW, _y + 9f), _width / 2f - CaptionW - 16f, key, 10, 60, 1,
            () => CurrentArea.DurationTenths, value =>
            {
                CurrentArea.DurationTenths = value;
                ShowSeconds();
            });

        // The label says it in seconds: the slider's own number, in tenths, would only confuse.
        slider.ValueNode.IsVisible = false;

        ShowSeconds();
        _page!.Refresh.Add(ShowSeconds);
    }

    private void BuildEvents()
    {
        var nameW   = _width * 0.40f;
        var choiceW = _width * 0.30f;

        AddCaptionAt(new Vector2(8f, _y + 4f), nameW - 16f, "Events.Column.Event");
        AddCaptionAt(new Vector2(nameW, _y + 4f), choiceW - 12f, "Events.Column.Area");
        AddCaptionAt(new Vector2(nameW + choiceW, _y + 4f), choiceW - 12f, "Events.Column.Motion");
        _y += 26f;

        foreach (var type in Enum.GetValues<GoblinBattleText.BattleTextEvent>())
        {
            // Cooldown alerts have their own tab.
            if (type != GoblinBattleText.BattleTextEvent.Cooldown)
                AddEventRow(type);
        }

        var options = Tweak.Config;
        _y += 8f;
        AddCheckAt(new Vector2(8f, _y + 7f), _width - 16f, "ShowFading", () => options.ShowFading, value => options.ShowFading = value);
        _y += RowH;
    }

    private void BuildHighlights()
    {
        foreach (var kind in Enum.GetValues<GoblinBattleText.BattleTextHighlight>())
        {
            // The look of the cooldown alert is with the rest of its settings, in its own tab.
            if (kind != GoblinBattleText.BattleTextHighlight.CooldownReady)
                AddHighlightBlock(kind);
        }
    }

    /// <summary>Everything about the cooldown alert: where it shows, its look, its order, and the actions it announces.</summary>
    private void BuildCooldowns()
    {
        var options = Tweak.Config;
        var colors  = options.Colors;
        var half    = _width / 2f;

        AddEventRow(GoblinBattleText.BattleTextEvent.Cooldown);
        AddHighlightBlock(GoblinBattleText.BattleTextHighlight.CooldownReady, () =>
            AddColorAt(new Vector2(half + 10f, _y + 4f), half - 22f, "Color.Cooldown",
                () => colors.Cooldown, GoblinBattleText.DefaultColors.Cooldown, value => colors.Cooldown = value));

        AddHeader("CooldownOrder");
        AddOrderList(new OrderList
        {
            Order   = () => options.CooldownOrder,
            Hidden  = () => options.CooldownHidden,
            Applies = _ => true,
            PartKey = "CooldownPart",
        }, options.CooldownOrder.Count);

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

        var search = new TextInputNode
        {
            Position          = new Vector2(4f, _y),
            Size              = new Vector2(200f, 28f),
            PlaceholderString = Tweak.Text("Cooldowns.Search"),
            MaxCharacters     = 32,
        };
        search.AttachNode(_page!.Node);

        new TextNode
        {
            String    = Tweak.Text("Cooldowns.Hint"),
            Position  = new Vector2(214f, _y + 6f),
            Size      = new Vector2(_width - 222f, 18f),
            TextColor = MutedGrey,
            FontSize  = 13,
        }.AttachNode(_page.Node);
        _y += 34f;

        // One button per action, lit when its cooldown is announced. They scroll if the job has more than fit.
        var scroll = new ScrollingNode<ResNode>
        {
            Position          = new Vector2(0f, _y),
            Size              = new Vector2(_width, Math.Max(IconCell, _pageHeight - _y)),
            AutoHideScrollBar = true,
        };
        scroll.AttachNode(_page.Node);

        var buttons = new List<(uint Id, string Name, IconButtonNode Button)>();
        foreach (var (id, name, iconId) in actions)
        {
            var button = new IconButtonNode
            {
                IconId      = iconId,
                Size        = new Vector2(IconCell - 2f, IconCell - 2f),
                TextTooltip = name,
            };
            button.OnClick = () =>
            {
                Tweak.SetCooldown(id, options.CooldownsOff.Contains(id));
                Light(button, id);
            };
            button.AttachNode(scroll.ContentNode);
            buttons.Add((id, name, button));
        }

        void Light(IconButtonNode button, uint id)
            => button.MultiplyColor = options.CooldownsOff.Contains(id) ? new Vector3(0.3f, 0.3f, 0.3f) : Vector3.One;

        // The actions whose name has the text searched for, row by row.
        void Arrange(string filter)
        {
            var columns = Math.Max(1, (int)((_width - 12f) / IconCell));
            var shown   = 0;
            foreach (var (id, name, button) in buttons)
            {
                button.IsVisible = filter.Length == 0 || name.Contains(filter, StringComparison.OrdinalIgnoreCase);
                if (!button.IsVisible) continue;

                button.Position = new Vector2(shown % columns * IconCell, shown / columns * IconCell);
                Light(button, id);
                shown++;
            }

            scroll.ContentNode.Height = Math.Max(IconCell, (shown + columns - 1) / columns * IconCell);
            scroll.RecalculateSizes();
        }

        search.OnInputReceived = text => Arrange(text.ToString().Trim());
        search.OnInputComplete = text => Arrange(text.ToString().Trim());

        Arrange(string.Empty);
        _page.Refresh.Add(() =>
        {
            foreach (var (id, _, button) in buttons)
                Light(button, id);
        });
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
    // Every control takes its label from "<key>" and its tooltip from "<key>.Help", reads its value with
    // "get" (again after the tab is put back to its defaults) and writes it with "set".
    // A page is two columns wide: a "cell" is a control in one of them, and the caller ends the row.

    private float CellX(int column) => column * _width / 2f;

    private void AddCheckCell(int column, string key, Func<bool> get, Action<bool> set)
        => AddCheckAt(new Vector2(CellX(column) + 8f, _y + 7f), _width / 2f - 16f, key, get, set);

    private void AddSliderCell(int column, string key, int min, int max, int step, Func<int> get, Action<int> set)
    {
        var x = CellX(column);
        AddLabelAt(new Vector2(x + 8f, _y + 9f), CaptionW - 8f, key);
        AddSliderAt(new Vector2(x + CaptionW, _y + 9f), _width / 2f - CaptionW - 16f, key, min, max, step, get, set);
    }

    private void AddDropDownCell<T>(int column, string key, string labelKey, Func<T> get, Action<T> set) where T : struct, Enum
    {
        var x = CellX(column);
        AddLabelAt(new Vector2(x + 8f, _y + 9f), CaptionW - 8f, key);
        AddDropDownAt(new Vector2(x + CaptionW, _y + 6f), _width / 2f - CaptionW - 16f, key, labelKey, get, set);
    }

    private void AddColorCell(int column, string key, Func<Vector4> get, Vector4 defaultColor, Action<Vector4> set)
        => AddColorAt(new Vector2(CellX(column) + 8f, _y + 4f), _width / 2f - 16f, key, get, defaultColor, set);

    private void AddCheckAt(Vector2 position, float width, string key, Func<bool> get, Action<bool> set)
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
    }

    private SliderNode AddSliderAt(Vector2 position, float width, string key, int min, int max, int step, Func<int> get, Action<int> set)
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
        return slider;
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
            () => options.Enabled, value => options.Enabled = value);
        AddDropDownAt(new Vector2(nameW, _y + 6f), choiceW - 12f, "Event.Area", "AreaName",
            () => options.Area, value => options.Area = value);
        AddDropDownAt(new Vector2(nameW + choiceW, _y + 6f), choiceW - 12f, "Event.Motion", "Motion",
            () => options.Motion ?? GoblinBattleText.BattleTextMotion.Up, value => options.Motion = value);

        _y += RowH;
    }

    /// <summary>
    /// The look of one kind of highlighted message: on/off and color, font and size, the animation (its
    /// kind, then the animation itself) and its intensity. A sample of it is shown as soon as any of them changes.
    /// </summary>
    /// <param name="lastCell">Adds a control of its own in the free cell of the last row.</param>
    private void AddHighlightBlock(GoblinBattleText.BattleTextHighlight kind, Action? lastCell = null)
    {
        var options  = Tweak.Highlight(kind);
        var half     = _width / 2f;
        var captionW = 100f;

        Action<T> Sampled<T>(Action<T> set) => value =>
        {
            set(value);
            Tweak.PreviewHighlightSoon(kind);
        };

        // The switch, then its colors: one for the alert, and for a hit one per whose hit it is.
        var defaults = GoblinBattleText.DefaultHighlight(kind);
        var checkW   = _width * 0.28f;
        var colorW   = (_width - checkW) / 3f;

        AddCheckAt(new Vector2(8f, _y + 7f), checkW - 16f, $"Highlight.{kind}", () => options.Enabled, Sampled<bool>(value => options.Enabled = value));
        if (kind == GoblinBattleText.BattleTextHighlight.CooldownReady)
        {
            AddColorAt(new Vector2(half + 10f, _y + 4f), half - 22f, $"Highlight.Color.{kind}", () => options.Color,
                defaults.Color, Sampled<Vector4>(value => options.Color = value));
        }
        else
        {
            AddColorAt(new Vector2(checkW, _y + 4f), colorW - 8f, "Highlight.Color.Dealt", () => options.Color,
                defaults.Color, Sampled<Vector4>(value => options.Color = value));
            AddColorAt(new Vector2(checkW + colorW, _y + 4f), colorW - 8f, "Highlight.Color.Taken", () => options.ColorTaken,
                defaults.ColorTaken, Sampled<Vector4>(value => options.ColorTaken = value));

            // Only healing can be critical besides damage; there are no direct heals.
            if (kind == GoblinBattleText.BattleTextHighlight.Critical)
            {
                AddColorAt(new Vector2(checkW + colorW * 2f, _y + 4f), colorW - 8f, "Highlight.Color.Heal", () => options.ColorHeal,
                    defaults.ColorHeal, Sampled<Vector4>(value => options.ColorHeal = value));
            }
        }
        _y += RowH;

        AddLabelAt(new Vector2(30f, _y + 9f), captionW, "Highlight.Font");
        AddDropDownAt(new Vector2(30f + captionW, _y + 6f), half - captionW - 50f, "Highlight.Font", "Font",
            () => options.Font, Sampled<GoblinBattleText.BattleTextFont>(value => options.Font = value));
        AddLabelAt(new Vector2(half + 10f, _y + 9f), captionW, "Highlight.FontSize");
        AddSliderAt(new Vector2(half + 10f + captionW, _y + 9f), half - captionW - 22f, "Highlight.FontSize", 12, 64, 1,
            () => options.FontSize, Sampled<int>(value => options.FontSize = value));
        _y += RowH;

        AddLabelAt(new Vector2(30f, _y + 9f), captionW, "Highlight.Animation");
        AddAnimationAt(new Vector2(30f + captionW, _y + 6f), half - captionW - 50f, new Vector2(half + 10f, _y + 6f), half - 22f, kind, options);
        _y += RowH;

        AddLabelAt(new Vector2(30f, _y + 9f), captionW, "Highlight.Intensity");
        AddSliderAt(new Vector2(30f + captionW, _y + 9f), half - captionW - 50f, "Highlight.Intensity", 0, 300, 10,
            () => options.Intensity, Sampled<int>(value => options.Intensity = value));
        lastCell?.Invoke();
        _y += RowH + 8f;
    }

    /// <summary>
    /// Two lists for the animation: its kind, and the animations of that kind. Choosing a kind plays
    /// its first animation, so going through the kinds shows what each one is like.
    /// </summary>
    private void AddAnimationAt(Vector2 groupPosition, float groupWidth, Vector2 position, float width,
        GoblinBattleText.BattleTextHighlight kind, GoblinBattleText.HighlightOptions options)
    {
        var shown = GoblinBattleText.AnimationsOf(GoblinBattleText.GroupOf(options.Animation));
        var animation = new EnumDropDownNode<GoblinBattleText.BattleTextAnimation>
        {
            Position         = position,
            Size             = new Vector2(width, 24f),
            MaxListOptions   = Math.Min(shown.Count, 12),
            GetLabelFunction = option => Tweak.Text($"Animation.{option}"),
            Options          = shown,
            SelectedOption   = options.Animation,
            TextTooltip      = Tweak.Text("Highlight.Animation.Help"),
        };

        var groups = new EnumDropDownNode<GoblinBattleText.BattleTextAnimationGroup>
        {
            Position         = groupPosition,
            Size             = new Vector2(groupWidth, 24f),
            MaxListOptions   = Enum.GetValues<GoblinBattleText.BattleTextAnimationGroup>().Length,
            GetLabelFunction = option => Tweak.Text($"AnimationGroup.{option}"),
            Options          = [.. Enum.GetValues<GoblinBattleText.BattleTextAnimationGroup>()],
            SelectedOption   = GoblinBattleText.GroupOf(options.Animation),
            TextTooltip      = Tweak.Text("Highlight.AnimationGroup.Help"),
        };

        // The length of the list first: the list is rebuilt to it when the options are set.
        void ShowAnimations(List<GoblinBattleText.BattleTextAnimation> list, GoblinBattleText.BattleTextAnimation selected)
        {
            animation.MaxListOptions = Math.Min(list.Count, 12);
            animation.Options        = list;
            animation.SelectedOption = selected;
        }

        void Apply(GoblinBattleText.BattleTextAnimation chosen)
        {
            options.Animation = chosen;
            Tweak.PreviewHighlightSoon(kind);
            Tweak.Changed();
        }

        animation.OnOptionSelected = Apply;
        groups.OnOptionSelected = group =>
        {
            // The first one after "None".
            var list  = GoblinBattleText.AnimationsOf(group);
            var first = list.Count > 1 ? list[1] : list[0];

            ShowAnimations(list, first);
            Apply(first);
        };

        _dropDowns.Add(groups);
        _dropDowns.Add(animation);
        _page!.Refresh.Add(() =>
        {
            var group = GoblinBattleText.GroupOf(options.Animation);
            groups.SelectedOption = group;
            ShowAnimations(GoblinBattleText.AnimationsOf(group), options.Animation);
        });
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

    private TextNode AddLabelAt(Vector2 position, float width, string key)
    {
        var label = new TextNode
        {
            String      = Tweak.Text(key),
            Position    = position,
            Size        = new Vector2(width, 20f),
            TextColor   = White,
            FontSize    = TextSize,
            TextTooltip = Tweak.Text(key + ".Help"),
        };
        label.AttachNode(_page!.Node);
        return label;
    }

    /// <summary>The title of a column of a table.</summary>
    private void AddCaptionAt(Vector2 position, float width, string key)
    {
        new TextNode
        {
            String    = Tweak.Text(key),
            Position  = position,
            Size      = new Vector2(width, 18f),
            TextColor = MutedGrey,
            FontSize  = 13,
        }.AttachNode(_page!.Node);
    }

    // ── Order and visibility of the parts of a message ──────────────────────────

    /// <summary>
    /// The parts side by side, in the order they have in a message: each one is a chip with its name
    /// and buttons to move it earlier or later and to hide it.
    /// </summary>
    /// <param name="parts">How many parts the list can show at most.</param>
    private void AddOrderList(OrderList list, int parts)
    {
        var chipW = _width / 4f;
        for (var i = 0; i < parts; i++)
        {
            var slot = i;
            var chip = new ResNode
            {
                Position = new Vector2(i * chipW, _y),
                Size     = new Vector2(chipW, ChipH),
            };
            chip.AttachNode(_page!.Node);

            var label = new TextNode
            {
                Position = new Vector2(8f, 4f),
                Size     = new Vector2(chipW - 12f, 20f),
                FontSize = 13,
            };
            label.AttachNode(chip);
            list.Chips.Add((chip, label));

            AddOrderButton(chip, CircleButtonIcon.LeftArrow,  6f,  "Order.Up",   () => Move(list, slot, -1));
            AddOrderButton(chip, CircleButtonIcon.RightArrow, 34f, "Order.Down", () => Move(list, slot, +1));
            AddOrderButton(chip, CircleButtonIcon.Eye,        62f, "Order.Hide", () => ToggleHidden(list, slot));
        }

        list.Empty = new TextNode
        {
            String    = Tweak.Text("Order.None"),
            Position  = new Vector2(8f, _y + 6f),
            Size      = new Vector2(_width - 16f, 18f),
            TextColor = MutedGrey,
            FontSize  = TextSize,
            IsVisible = false,
        };
        list.Empty.AttachNode(_page!.Node);

        _y += ChipH;

        Refresh(list);
        _page.Refresh.Add(() => Refresh(list));
    }

    private void AddOrderButton(ResNode chip, CircleButtonIcon icon, float x, string tooltipKey, Action onClick)
    {
        var button = new CircleButtonNode
        {
            Icon        = icon,
            Position    = new Vector2(x, 28f),
            Size        = new Vector2(24f, 24f),
            TextTooltip = Tweak.Text(tooltipKey),
        };
        button.OnClick = onClick;
        button.AttachNode(chip);
    }

    /// <summary>The parts with a chip, in order: those that apply.</summary>
    private static List<GoblinBattleText.BattleTextPart> Shown(OrderList list) => [.. list.Order().Where(list.Applies)];

    /// <summary>Swaps the part of a chip with the one of the chip next to it.</summary>
    private void Move(OrderList list, int slot, int step)
    {
        var shown = Shown(list);
        if (slot < 0 || slot >= shown.Count || slot + step < 0 || slot + step >= shown.Count) return;

        var order = list.Order();
        var from  = order.IndexOf(shown[slot]);
        var to    = order.IndexOf(shown[slot + step]);

        (order[from], order[to]) = (order[to], order[from]);
        Refresh(list);
        Tweak.Changed();
    }

    private void ToggleHidden(OrderList list, int slot)
    {
        var shown = Shown(list);
        if (slot < 0 || slot >= shown.Count) return;

        var hidden = list.Hidden();
        if (!hidden.Remove(shown[slot]))
            hidden.Add(shown[slot]);

        Refresh(list);
        Tweak.Changed();
    }

    private void Refresh(OrderList list)
    {
        var shown  = Shown(list);
        var hidden = list.Hidden();

        for (var i = 0; i < list.Chips.Count; i++)
        {
            var (chip, label) = list.Chips[i];
            chip.IsVisible = i < shown.Count;
            if (!chip.IsVisible) continue;

            var isHidden = hidden.Contains(shown[i]);
            var name     = Tweak.Text($"{list.PartKey}.{shown[i]}");

            label.String    = isHidden ? $"{name} {Tweak.Text("Order.Hidden")}" : name;
            label.TextColor = isHidden ? MutedGrey : White;
        }

        if (list.Empty is not null)
            list.Empty.IsVisible = shown.Count == 0;
    }
}
