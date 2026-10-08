using System.Numerics;
using System.Reflection;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using GoblinTweaks.Core;
using GoblinTweaks.Localization;
using GoblinTweaks.Native;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// The main GoblinTweaks window, all native. Top: tweak count, search and settings button, then the category tabs.
/// Left: the list of tweaks, each with its switch. Right: the selected tweak (name, state, on/off button, description,
/// command and options). Bottom: version and links. With no tweak selected the right side shows the welcome
/// and "What's new" cards.
/// </summary>
internal sealed unsafe class MainAddon : SettingsPanelAddon
{
    private const string FilterEnabled  = "__enabled";
    private const string FilterDisabled = "__disabled";

    private const float LeftW          = 390f;
    private const float Gap            = 14f;
    private const float TopH           = 84f;
    private const float DetailHeaderH  = 72f;
    private const float FooterH        = 44f;
    private const float RowH           = 64f;
    private const float ListBarW       = 24f;
    private const float IconSize       = 52f;
    private const float HeaderTextX    = Pad + IconSize + 12f;
    private const float RowInset       = 6f;   // keeps the selection highlight off the frame

    private static readonly Vector4 Green = new(0.40f, 0.88f, 0.46f, 1f);
    private static readonly Vector4 Red   = new(0.92f, 0.36f, 0.33f, 1f);

    public required TweakManager Manager { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action OpenChangelog { get; init; }

    private readonly string _version = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
    private readonly List<(Tweak Tweak, TextNode Sub, TweakRowNode Row)> _rows = [];
    private readonly List<NodeBase> _rowNodes = [];
    private readonly Dictionary<Tweak, TextButtonNode> _marks = [];
    private readonly Dictionary<Tweak, WrapImageNode> _icons = [];

    private Tweak? _selected;
    private string? _filter;
    private string _search = string.Empty;

    private ScreenshotViewerAddon? _viewer;
    private AtkUnitBase*    _window;
    private DateTime        _viewerNotBefore;
    private int             _viewerCount;
    private TextNode?       _count;
    private TabBarNode?     _tabs;
    private ScrollingNode<ResNode>? _list;
    private TextNode?       _title;
    private TextNode?       _status;
    private float           _statusX, _statusY;
    private readonly List<NodeBase> _tagNodes = [];
    private TweakCategory?  _tagCategory;
    private TextButtonNode? _toggle;
    private TextButtonNode? _open;

    private bool _tabsDirty = true;
    private bool _rowsDirty = true;
    private bool _headerDirty = true;
    private string _signature = string.Empty;
    private DateTime _nextCheck;
    private DateTime _nextMemory;

    protected override Vector2 ScrollPosition => ContentStartPosition + new Vector2(LeftW + Gap, TopH + DetailHeaderH);

    protected override Vector2 ScrollSize
        => new(ContentSize.X - LeftW - Gap, ContentSize.Y - TopH - DetailHeaderH - FooterH);

    private bool Home => _selected is null;

    // ── Setup ─────────────────────────────────────────────────────────────────

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        _window = addon;
        Manager.UpdateMemoryUsage();

        // Existing users that updated, or a first run: start on the welcome / what's new cards.
        _selected = WelcomePending || WhatsNewPending ? null : SortedTweaks().FirstOrDefault();

        // First child: everything else is drawn above it.
        BuildBackground();

        base.OnSetup(addon, atkValues);

        BuildTop();
        BuildList();
        BuildDetailHeader();
        BuildFooter();

        _signature = Signature();
        _selectedSignature = SelectedSignature();
    }

    /// <summary>Frees the window of the big picture; the plugin calls it before it frees this window.</summary>
    internal void DisposeViewer()
    {
        _viewer?.Dispose();
        _viewer = null;
    }

    protected override void OnClosed()
    {
        _rows.Clear();
        _rowNodes.Clear();
        _marks.Clear();
        _icons.Clear();
        _viewer?.Dispose();
        _viewer = null;
        _window = null;
        _count = null;
        _tabs = null;
        _list = null;
        _title = null;
        _status = null;
        _tagNodes.Clear();
        _tagCategory = null;
        _toggle = null;
        _open = null;
        _tabsDirty = _rowsDirty = _headerDirty = true;
    }

    private void BuildTop()
    {
        var c  = ContentStartPosition;
        var cs = ContentSize;

        _count = new TextNode
        {
            Position  = c + new Vector2(Pad, 8f),
            Size      = new Vector2(cs.X - 320f, 20f),
            TextColor = Muted,
            FontSize  = 13,
        };
        _count.AttachNode(this);

        var settings = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = c + new Vector2(cs.X - 28f - Pad, 4f),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Loc.Get("Settings.Title"),
        };
        settings.OnClick = OpenSettings;
        settings.AttachNode(this);

        var search = new SearchInputNode
        {
            Position          = c + new Vector2(cs.X - 28f - Pad - 8f - 250f, 4f),
            Size              = new Vector2(250f, 28f),
            PlaceholderString = Loc.Get("Window.Search"),
            MaxCharacters     = 64,
        };
        search.OnInputReceived = text =>
        {
            var typed  = text.ExtractText();
            _search    = (string.IsNullOrEmpty(typed) ? text.ToString() : typed).Trim();
            _rowsDirty = true;
        };
        search.AttachNode(this);

        _tabs = new TabBarNode
        {
            Position = c + new Vector2(0f, 40f),
            Size     = new Vector2(cs.X, 30f),
        };
        _tabs.AttachNode(this);
    }

    private void BuildList()
    {
        var c  = ContentStartPosition;
        var cs = ContentSize;

        _list = new ScrollingNode<ResNode>
        {
            Position          = c + new Vector2(0f, TopH),
            Size              = new Vector2(LeftW, cs.Y - TopH - FooterH),
            AutoHideScrollBar = true,
        };
        _list.AttachNode(this);

    }

    private void BuildDetailHeader()
    {
        var c  = ContentStartPosition;
        var cs = ContentSize;
        var x  = c.X + LeftW + Gap;
        var w  = cs.X - LeftW - Gap;

        // Square for the tweak's icon (no content yet), as in the window design.
        Frame(null, new Vector2(x + Pad, c.Y + TopH + 8f), new Vector2(IconSize, IconSize), box: true);

        // One image per tweak that has an icon; the selected one is shown.
        foreach (var tweak in Manager.Tweaks)
        {
            if (tweak.IconKey is not { } key || !ScreenshotStore.TryGet(key, out var texture)) continue;

            var icon = new WrapImageNode(texture)
            {
                Position  = new Vector2(x + Pad, c.Y + TopH + 8f),
                Size      = new Vector2(IconSize, IconSize),
                IsVisible = false,
            };
            icon.AttachNode(this);
            _icons[tweak] = icon;
        }

        _title = new TextNode
        {
            Position  = new Vector2(x + HeaderTextX, c.Y + TopH + 2f),
            Size      = new Vector2(w - 310f - HeaderTextX - Pad, 36f),
            TextColor = Gold,
            FontType  = UiFont.Heading,
            FontSize  = UiFont.Size(26),
            CharSpacing = UiFont.Spacing,
        };
        _title.AddTextFlags(TextFlags.Ellipsis);
        _title.AttachNode(this);

        _statusX = x + HeaderTextX;
        _statusY = c.Y + TopH + 42f;
        _status = new TextNode
        {
            Position  = new Vector2(_statusX, _statusY),
            Size      = new Vector2(w - 310f - HeaderTextX - Pad, 18f),
            TextColor = Muted,
            FontSize  = 13,
        };
        _status.AttachNode(this);

        _toggle = new TextButtonNode
        {
            Position = new Vector2(x + w - 140f - Pad, c.Y + TopH + 10f),
            Size     = new Vector2(140f, 30f),
        };
        _toggle.OnClick = ToggleSelected;
        _toggle.AttachNode(this);

        // The window of the tweak: next to the on/off button, only while the tweak is on.
        _open = new TextButtonNode
        {
            Position = new Vector2(x + w - 140f - Pad - 8f - 150f, c.Y + TopH + 10f),
            Size     = new Vector2(150f, 30f),
        };
        _open.OnClick = () => _selected?.OpenButton?.Click();
        _open.AttachNode(this);

        new HorizontalLineNode
        {
            Position = new Vector2(x, c.Y + TopH + DetailHeaderH - 4f),
            Size     = new Vector2(w, 2f),
        }.AttachNode(this);
    }

    private void BuildFooter()
    {
        var c      = ContentStartPosition;
        var cs     = ContentSize;
        var top    = c.Y + cs.Y - FooterH;
        const float buttonH = 28f;
        var y      = top + (FooterH - buttonH) / 2f + 2f;

        new HorizontalLineNode { Position = new Vector2(c.X, top), Size = new Vector2(cs.X, 2f) }.AttachNode(this);

        // Version, centered on the same line as the buttons.
        var version = new TextNode
        {
            String        = _version,
            Position      = new Vector2(c.X + Pad, y),
            Size          = new Vector2(60f, buttonH),
            TextColor     = Muted,
            FontSize      = 13,
            AlignmentType = AlignmentType.Left,
        };
        version.AttachNode(this);

        var x = c.X + Pad + 70f;
        x += FooterButton(Loc.Get("Window.Footer.Repository"), x, y + 4f, buttonH, () => Util.OpenLink(PluginInfo.RepositoryUrl)) + 12f;
        FooterButton(Loc.Get("Window.Footer.Changelog"), x, y + 4f, buttonH, OpenChangelog);

        var issues = Loc.Get("Window.Footer.Issues");
        var width  = Measure(issues) + 44f;
        FooterButton(issues, c.X + cs.X - Pad - width, y + 4f, buttonH, () => Util.OpenLink(PluginInfo.RepositoryUrl + "/issues"));
    }

    /// <summary>Width of a text as the game draws it (12 pt by default).</summary>
    private float Measure(string text, uint size = 12, FontType? font = null, uint spacing = 0)
    {
        var probe = new TextNode { String = text, FontSize = size, Size = new Vector2(800f, 24f) };
        if (font is { } type) { probe.FontType = type; probe.CharSpacing = spacing; }
        probe.AttachNode(this);
        var width = probe.GetTextDrawSize(false).X;
        probe.Dispose();
        return width;
    }

    /// <summary>A footer button as wide as its text needs plus room around it; returns its width.</summary>
    private float FooterButton(string text, float x, float y, float height, Action click)
    {
        var width  = Measure(text) + 44f;
        var button = new TextButtonNode { String = text, Position = new Vector2(x, y), Size = new Vector2(width, height) };
        button.OnClick = click;
        button.AttachNode(this);
        return width;
    }

    // ── State and refresh ─────────────────────────────────────────────────────

    private bool WelcomePending  => Manager.Store.Data.ShowWelcome;

    private bool WhatsNewPending
        => Changelog.Latest is { } latest && Manager.Store.Data.LastSeenVersion is { } seen && seen != latest.Version;

    private IEnumerable<Tweak> SortedTweaks()
        => Manager.Tweaks
            .OrderBy(tweak => tweak.Category)
            .ThenBy(tweak => tweak.Name, StringComparer.CurrentCultureIgnoreCase);

    private string _tabsSignature = string.Empty;
    private string _selectedSignature = string.Empty;

    private string TabsSignature() => string.Join('|', TabDefinitions().Select(t => $"{t.Key}:{t.Text}:{t.Count}"));

    private string SelectedSignature() => _selected is { } t ? $"{t.Id}:{(int)t.State}:{t.ErrorMessage}" : string.Empty;

    private string Signature()
        => string.Join('|', Manager.Tweaks.Select(tweak => $"{tweak.Id}:{(int)tweak.State}:{tweak.ErrorMessage}"));

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        var now = DateTime.UtcNow;
        if (now >= _nextMemory)
        {
            _nextMemory = now.AddSeconds(3);
            Manager.UpdateMemoryUsage();
            foreach (var (tweak, sub, _) in _rows)
                sub.String = FormatMemory(tweak.MemoryBytes);
        }

        // Tweaks turn on and off from the game thread (and fail on their own): follow what they do.
        if (now >= _nextCheck)
        {
            _nextCheck = now.AddMilliseconds(250);
            var signature = Signature();
            if (signature != _signature)
            {
                _signature  = signature;
                _headerDirty = true;
                _tabsDirty   = TabsSignature() != _tabsSignature;

                // Rows are only rebuilt when the filter changes which ones are listed: freeing the nodes under the
                // mouse right after a click crashed the game's input handling when switching tweaks quickly.
                if (_filter is FilterEnabled or FilterDisabled) _rowsDirty = true;
                else RefreshMarks();

                // The detail panel only changes when the selected tweak did; freeing its nodes (which may hold the
                // keyboard focus) on every other change was crashing the game.
                var selected = SelectedSignature();
                if (selected != _selectedSignature)
                {
                    _selectedSignature = selected;
                    RequestRebuild();
                }
            }
        }

        if (_tabsDirty)   { _tabsDirty = false;   RebuildTabs(); }
        if (_rowsDirty)   { _rowsDirty = false;   RebuildRows(); }
        if (_headerDirty) { _headerDirty = false; RefreshHeader(); }
    }

    private string TabLabel(string key, string text, int count) => $"{text}    {count}";

    private IEnumerable<(string? Key, string Text, int Count)> TabDefinitions()
    {
        var all = Manager.Tweaks;
        yield return (null, Loc.Get("Window.Filter.All"), all.Count);
        yield return (FilterEnabled, Loc.Get("Window.Filter.Enabled"), all.Count(t => t.State == TweakState.Enabled));
        yield return (FilterDisabled, Loc.Get("Window.Filter.Disabled"), all.Count(t => t.State != TweakState.Enabled));

        foreach (var category in all.Select(tweak => tweak.Category).Distinct().Order())
        {
            var key = category.ToString();
            yield return (key, Loc.Get($"Category.{key}", key), all.Count(t => t.Category == category));
        }
    }

    private void RebuildTabs()
    {
        if (_tabs is null) return;

        _tabsSignature = TabsSignature();

        _tabs.Clear();
        string? current = null;
        foreach (var (key, text, count) in TabDefinitions())
        {
            var label = TabLabel(key ?? string.Empty, text, count);
            var filter = key;
            if (filter == _filter) current = label;
            _tabs.AddTab(label, () =>
            {
                if (_filter == filter) return;
                _filter    = filter;
                _rowsDirty = true;
            });
        }

        if (current is not null) _tabs.SelectTab(current);
    }

    private bool MatchesFilter(Tweak tweak) => _filter switch
    {
        null           => true,
        FilterEnabled  => tweak.State == TweakState.Enabled,
        FilterDisabled => tweak.State != TweakState.Enabled,
        _              => tweak.Category.ToString() == _filter,
    };

    // Searches the name in every language, whatever language is selected; case and accents do not matter,
    // and every word typed has to be in the name.
    private bool MatchesSearch(Tweak tweak)
    {
        var words = Normalize(_search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;

        return tweak.SearchTexts.Select(Normalize).Any(name => words.All(name.Contains));
    }

    private static string Normalize(string text)
    {
        var decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var plain      = decomposed.Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string([.. plain]).ToLowerInvariant();
    }

    private void RebuildRows()
    {
        if (_list is null) return;

        foreach (var node in _rowNodes) node.Dispose();
        _rowNodes.Clear();
        _rows.Clear();
        _marks.Clear();

        var width = LeftW - RowInset - 10f;
        var y     = 0f;
        foreach (var tweak in SortedTweaks().Where(MatchesFilter).Where(MatchesSearch))
        {
            AddRow(tweak, y, width);
            y += RowH;
        }

        if (y == 0f)
        {
            var empty = new TextNode
            {
                String    = Loc.Get("Window.Empty"),
                Position  = new Vector2(Pad, 10f),
                Size      = new Vector2(width - Pad * 2, 20f),
                TextColor = Muted,
                FontSize  = 13,
            };
            empty.AttachNode(_list.ContentNode);
            _rowNodes.Add(empty);
            y = 40f;
        }

        _list.ContentNode.Height = MathF.Max(_list.Height, y + 4f);
        _list.RecalculateSizes();
    }

    private void AddRow(Tweak tweak, float y, float width)
    {
        var row = new TweakRowNode
        {
            Position    = new Vector2(RowInset, y),
            Size        = new Vector2(width, RowH - 2f),
            IsSelected  = tweak == _selected,
        };
        row.OnClick = _ => Select(tweak);
        row.AttachNode(_list!.ContentNode);
        _rowNodes.Add(row);

        const float switchW = 56f;
        var textW = width - switchW - Pad * 3;

        var name = new TextNode
        {
            String    = tweak.Name,
            Position  = new Vector2(Pad, 9f),
            Size      = new Vector2(textW, 22f),
            TextColor = tweak.State == TweakState.Error ? Red : White,
            FontType  = UiFont.Heading,
            FontSize  = UiFont.Size(17),
            CharSpacing = UiFont.Spacing,
        };
        name.AddTextFlags(TextFlags.Ellipsis);
        name.AttachNode(row);
        if (Measure(tweak.Name, UiFont.Size(17), UiFont.Heading, UiFont.Spacing) > textW) row.TextTooltip = tweak.Name;

        // Category as a small colored tag, then the memory the tweak uses.
        var tagW = AddTag(row, CategoryName(tweak), CategoryColor(tweak.Category), Pad, 36f);
        var sub = new TextNode
        {
            String    = FormatMemory(tweak.MemoryBytes),
            Position  = new Vector2(Pad + tagW + 8f, 36f),
            Size      = new Vector2(MathF.Max(0f, textW - tagW - 8f), 18f),
            TextColor = Muted,
            FontSize  = 12,
        };
        sub.AddTextFlags(TextFlags.Ellipsis);
        sub.AttachNode(row);
        _rows.Add((tweak, sub, row));

        // Green check when on, red cross when off; click to switch. A plain game button: it brings its own mouse handling.
        var mark = new TextButtonNode
        {
            Position = new Vector2(width - switchW - Pad, (RowH - 2f - 30f) / 2f),
            Size     = new Vector2(switchW, 30f),
        };
        mark.OnClick = () =>
        {
            Manager.SetEnabled(tweak, tweak.State == TweakState.Disabled);
            _headerDirty = true;
        };
        mark.AttachNode(row);

        _marks[tweak] = mark;
        RefreshMarks();
    }

    /// <summary>Updates the check / cross of every listed tweak in place.</summary>
    private void RefreshMarks()
    {
        foreach (var (tweak, mark) in _marks)
        {
            var on = tweak.State == TweakState.Enabled;
            mark.String = on ? "✓" : "×";
            mark.LabelNode.TextColor = on ? Green : Red;
        }
    }

    /// <summary>A tag: softly rounded colored background with the text in the same color; returns its width.</summary>
    private float AddTag(NodeBase? parent, string text, Vector4 color, float x, float y, List<NodeBase>? created = null)
    {
        var width = Measure(text, 11) + 14f;

        RoundedFill(parent, x, y, width, 19f, color with { W = 0.28f }, created);
        var label = new TextNode
        {
            String        = text,
            Position      = new Vector2(x, y + 2f),
            Size          = new Vector2(width, 15f),
            TextColor     = color,
            FontSize      = 11,
            AlignmentType = AlignmentType.Center,
        };
        Attach(label, parent);
        created?.Add(label);
        return width;
    }

    /// <summary>A filled box with its corners cut by two pixels, drawn as bands that do not overlap (so the transparency stays even).</summary>
    private void RoundedFill(NodeBase? parent, float x, float y, float width, float height, Vector4 color, List<NodeBase>? created = null)
    {
        void Band(float top, float h, float inset)
        {
            var band = new ColorImageNode { Color = color, Position = new Vector2(x + inset, y + top), Size = new Vector2(width - inset * 2f, h) };
            Attach(band, parent);
            created?.Add(band);
        }

        Band(0f, 1f, 2f);
        Band(1f, 1f, 1f);
        Band(2f, height - 4f, 0f);
        Band(height - 2f, 1f, 1f);
        Band(height - 1f, 1f, 2f);
    }

    private static Vector4 CategoryColor(TweakCategory category) => category switch
    {
        TweakCategory.Crafting  => new Vector4(1.00f, 0.66f, 0.30f, 1f),
        TweakCategory.Gathering => new Vector4(0.50f, 0.85f, 0.45f, 1f),
        TweakCategory.Interface => new Vector4(0.45f, 0.72f, 1.00f, 1f),
        TweakCategory.Inventory => new Vector4(0.95f, 0.82f, 0.40f, 1f),
        TweakCategory.Chat      => new Vector4(0.80f, 0.58f, 1.00f, 1f),
        _                       => new Vector4(0.72f, 0.72f, 0.76f, 1f),
    };

    private static string FormatMemory(long bytes) => bytes switch
    {
        < 0           => string.Empty,
        < 1024        => $"~{bytes} B",
        < 1024 * 1024 => $"~{bytes / 1024.0:0.#} KB",
        _             => $"~{bytes / (1024.0 * 1024.0):0.##} MB",
    };

    private static string CategoryName(Tweak tweak) => Loc.Get($"Category.{tweak.Category}", tweak.Category.ToString());

    private static string SubText(Tweak tweak)
    {
        var memory = FormatMemory(tweak.MemoryBytes);
        return memory.Length == 0 ? CategoryName(tweak) : $"{CategoryName(tweak)} · {memory}";
    }

    // ── Selection and detail ──────────────────────────────────────────────────

    private void Select(Tweak tweak)
    {
        if (_selected == tweak) return;
        _selected    = tweak;
        _selectedSignature = SelectedSignature();
        _headerDirty = true;
        foreach (var (other, _, row) in _rows) row.IsSelected = other == tweak;
        RequestRebuild();
    }

    private void ToggleSelected()
    {
        if (_selected is not { } tweak) return;
        Manager.SetEnabled(tweak, tweak.State == TweakState.Disabled);
    }

    private void RefreshHeader()
    {
        if (_title is null || _status is null || _toggle is null) return;

        if (_count is not null)
            _count.String = Loc.Format("Window.Subtitle", Manager.EnabledCount, Manager.Tweaks.Count);

        if (_selected is not { } tweak)
        {
            _title.String     = Loc.Get(WhatsNewPending ? "Window.WhatsNew.Title" : "Window.Welcome.Title");
            _status.String    = string.Empty;
            foreach (var icon in _icons.Values) icon.IsVisible = false;
            foreach (var node in _tagNodes) node.Dispose();
            _tagNodes.Clear();
            _tagCategory = null;
            _toggle.IsVisible = false;
            if (_open is not null) _open.IsVisible = false;
            return;
        }

        foreach (var (owner, icon) in _icons) icon.IsVisible = owner == tweak;

        var (state, color) = tweak.State switch
        {
            TweakState.Enabled => (Loc.Get("Window.Status.On"), Green),
            TweakState.Error   => (Loc.Get("Window.Status.Error"), Red),
            _                  => (Loc.Get("Window.Status.Off"), Muted),
        };

        _title.String      = tweak.Name;
        _title.TextTooltip = _title.GetTextDrawSize(false).X > _title.Width || Measure(tweak.Name, UiFont.Size(26), UiFont.Heading, UiFont.Spacing) > _title.Width ? tweak.Name : string.Empty;
        // The category as a tag under the name, then memory and state next to it.
        if (_tagCategory != tweak.Category)
        {
            foreach (var node in _tagNodes) node.Dispose();
            _tagNodes.Clear();
            _tagCategory = tweak.Category;

            var tagW = AddTag(null, CategoryName(tweak), CategoryColor(tweak.Category), _statusX, _statusY - 1f, _tagNodes);
            _status.Position = new Vector2(_statusX + tagW + 8f, _statusY);
        }

        var memory = FormatMemory(tweak.MemoryBytes);
        _status.String    = memory.Length == 0 ? state : $"{memory} · {state}";
        _status.TextColor = color;
        _toggle.IsVisible = true;
        _toggle.String    = Loc.Get(tweak.State == TweakState.Disabled ? "Window.Enable" : "Window.Disable");

        if (_open is not null)
        {
            var open = tweak.OpenButton;
            _open.IsVisible = open is not null && tweak.State == TweakState.Enabled;
            if (open is { } button)
            {
                _open.String      = button.Label;
                _open.TextTooltip = button.Tooltip ?? string.Empty;
            }
        }
    }

    protected override void Build()
    {
        if (_selected is { } tweak)
            BuildTweak(tweak);
        else
            BuildHome();
    }

    private void BuildTweak(Tweak tweak)
    {
        if (!string.IsNullOrEmpty(tweak.Description))
        {
            Section(Loc.Get("Window.Description"));
            Note(tweak.Description, Pad, ContentW, White, 14);
        }

        if (tweak.State == TweakState.Error)
        {
            Section(Loc.Get("Window.Status.Error"));
            Note(tweak.ErrorMessage ?? string.Empty, Pad, ContentW, Red);
            Note(Loc.Get("Window.Status.ErrorHint"), Pad, ContentW, Muted);
            Space(6f);
            Button(Loc.Get("Window.Retry"), null, Pad, () =>
            {
                Manager.SetEnabled(tweak, false);
                Manager.SetEnabled(tweak, true);
            });
            Y += 32f;
        }

        if (tweak.Commands.Count > 0)
        {
            Section(Loc.Get("Window.Commands"));
            CommandsPanel(tweak.Commands);
        }

        if (tweak.HasSettings) BuildSettings(tweak);

        BuildScreenshots(tweak);
    }

    private void BuildSettings(Tweak tweak)
    {
        Section(Loc.Get("Window.Settings"));

        if (tweak.State != TweakState.Enabled)
        {
            Note(Loc.Get("Window.Settings.Locked"), Pad, ContentW, Muted);
            return;
        }

        Space(10f);
        var buttons = new List<TweakButton>();
        if (tweak.HelpButton is { } help) buttons.Add(help);
        buttons.AddRange(tweak.Buttons);

        var x = Pad;
        foreach (var button in buttons)
        {
            var width = MathF.Max(130f, Measure(button.Label) + 44f);
            if (x > Pad && x + width > ContentW) { x = Pad; Y += 34f; }
            Button(button.Label, button.Tooltip, x, button.Click, width);
            x += width + 8f;
        }
        if (buttons.Count > 0) Y += 40f;

        foreach (var toggle in tweak.Toggles)
        {
            var current = toggle;
            Toggle(current.Label, current.Help, current.Get(), current.Set);
        }
    }

    /// <summary>Up to three screenshots as thumbnails; a click opens the picture big in its own window.</summary>
    private void BuildScreenshots(Tweak tweak)
    {
        var keys = tweak.Screenshots.Take(3).Where(key => ScreenshotStore.TryGet(key, out _)).ToList();
        if (keys.Count == 0) return;

        Section(Loc.Get("Window.Screenshots"));

        var gallery = new ScreenshotGalleryNode(keys, ContentW) { Position = new Vector2(Pad, Y) };
        // Opened a moment later, once the click is over: a window that appears while the button is still down
        // was closing again with the release.
        gallery.OnOpen = index => Svc.Framework.RunOnTick(() =>
        {
            if (_window != null) OpenViewer(tweak, keys, index);
        }, TimeSpan.FromMilliseconds(250));
        gallery.AttachNode(Host);
        Y += gallery.TotalHeight + 8f;
    }

    private void OpenViewer(Tweak tweak, IReadOnlyList<string> keys, int index)
    {
        // One click can reach the thumbnail twice (press and release): the second one is the same click.
        if (DateTime.UtcNow < _viewerNotBefore) return;
        _viewerNotBefore = DateTime.UtcNow.AddMilliseconds(500);

        // Next to the main window, never over it: on the side with more room, as big as that room allows.
        var size   = new Vector2(900f, 600f);
        var origin = Vector2.Zero;
        var device = Device.Instance();
        if (_window != null && device != null)
        {
            var scale = MathF.Max(0.1f, _window->Scale);
            var left  = (float)_window->X;
            var right = _window->X + _window->GetScaledWidth(true);
            var room  = device->Width - right;
            var onRight = room >= left;
            var space = (onRight ? room : left) - 16f;

            size   = new Vector2(MathF.Min(size.X, space / scale), MathF.Min(size.Y, (device->Height - 48f) / scale));
            origin = new Vector2(onRight ? right + 8f : left - size.X * scale - 8f, _window->Y);
            origin.Y = MathF.Min(origin.Y, MathF.Max(0f, device->Height - size.Y * scale - 8f));
        }

        // Built when opened, so it shows the screenshots of the tweak that was clicked. Each one has its own name:
        // the game finds a window by name, so freeing the old one under the same name would close the new one too.
        var previous = _viewer;
        _viewer = new ScreenshotViewerAddon
        {
            InternalName    = $"GtkScreenshots{++_viewerCount}",
            Title           = tweak.Name,
            Size            = size,
            RespectCloseAll = false,
            Origin          = origin,
            Keys            = keys,
            Index           = index,
        };
        _viewer.Open();
        previous?.Dispose();
    }

    /// <summary>A dark box with a gold border: the command in gold on the left, what it does on the right.</summary>
    private void CommandsPanel(IReadOnlyList<TweakCommand> commands)
    {
        const float rowH = 24f, inner = 8f;
        var commandW = commands.Max(c => Measure(c.Command, 13)) + 24f;
        var height   = commands.Count * rowH + inner * 2f;

        Frame(Host, new Vector2(Pad, Y), new Vector2(ContentW, height), box: true);

        var y = Y + inner;
        foreach (var (command, description) in commands)
        {
            new TextNode
            {
                String    = command,
                Position  = new Vector2(Pad + 12f, y + 2f),
                Size      = new Vector2(commandW, 20f),
                TextColor = Gold,
                FontType  = FontType.Axis,
                FontSize  = 13,
            }.AttachNode(Host);

            var text = new TextNode
            {
                String    = description,
                Position  = new Vector2(Pad + 12f + commandW, y + 2f),
                Size      = new Vector2(MathF.Max(0f, ContentW - commandW - 24f), 20f),
                TextColor = Muted,
                FontSize  = 13,
            };
            text.AddTextFlags(TextFlags.Ellipsis);
            text.AttachNode(Host);
            y += rowH;
        }

        Y += height + 8f;
    }

    /// <summary>Frames the two sides of the window with a thin, translucent outline; the window keeps its native background.</summary>
    private void BuildBackground()
    {
        var c  = ContentStartPosition;
        var cs = ContentSize;

        var top    = TopH - 4f;
        var height = cs.Y - top - FooterH - 4f;
        Frame(null, c + new Vector2(0f, top), new Vector2(LeftW, height));
        Frame(null, c + new Vector2(LeftW + Gap, top), new Vector2(cs.X - LeftW - Gap, height));
    }

    private static Vector4 Hex(int rgb, float alpha = 1f)
        => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha);

    // How many pixels each of the four first rows of a corner is pulled in: a 4 px radius.
    private static readonly float[] CornerInset = [3f, 2f, 1f, 1f];

    /// <summary>
    /// A framed area with rounded corners over the window's own background. Panel (<paramref name="box"/> false):
    /// 24 % black with an inset bevel, dark on top and left, light on bottom and right. Box: 40 % black with a flat
    /// #3a3a3a border. Built from bands and one pixel lines, so it lines up exactly with the given box.
    /// </summary>
    private void Frame(NodeBase? parent, Vector2 position, Vector2 size, bool box = false)
    {
        var fill        = new Vector4(0f, 0f, 0f, box ? 0.40f : 0.24f);
        var topLeft     = box ? Hex(0x3a3a3a) : Hex(0x1a1a1a);
        var bottomRight = box ? Hex(0x3a3a3a) : Hex(0x4a4a4a);
        var r           = CornerInset.Length;
        var w           = size.X;
        var h           = size.Y;

        void Rect(float x, float y, float width, float height, Vector4 color)
            => Attach(new ColorImageNode { Color = color, Position = position + new Vector2(x, y), Size = new Vector2(width, height) }, parent);

        // Fill: bands that do not overlap, so the transparency stays even.
        for (var i = 0; i < r; i++)
        {
            Rect(CornerInset[i], i, w - CornerInset[i] * 2f, 1f, fill);
            Rect(CornerInset[i], h - 1f - i, w - CornerInset[i] * 2f, 1f, fill);
        }
        Rect(0f, r, w, h - r * 2f, fill);

        // Border: straight edges, then the pixels that step around each corner.
        Rect(CornerInset[0], 0f, w - CornerInset[0] * 2f, 1f, topLeft);
        Rect(CornerInset[0], h - 1f, w - CornerInset[0] * 2f, 1f, bottomRight);
        Rect(0f, r, 1f, h - r * 2f, topLeft);
        Rect(w - 1f, r, 1f, h - r * 2f, bottomRight);

        for (var i = 1; i < r; i++)
        {
            var step = MathF.Max(1f, CornerInset[i - 1] - CornerInset[i]);
            Rect(CornerInset[i], i, step, 1f, topLeft);
            Rect(w - CornerInset[i] - step, i, step, 1f, bottomRight);
            Rect(CornerInset[i], h - 1f - i, step, 1f, topLeft);
            Rect(w - CornerInset[i] - step, h - 1f - i, step, 1f, bottomRight);
        }
    }

    /// <summary>Attaches to <paramref name="parent"/>, or to the window itself when it is null.</summary>
    private void Attach(NodeBase node, NodeBase? parent)
    {
        if (parent is null) node.AttachNode(this);
        else node.AttachNode(parent);
    }

    private void BuildHome()
    {
        if (WhatsNewPending && Changelog.Latest is { } latest)
        {
            Note($"v{latest.Version} — {latest.Date}", Pad, ContentW, Muted);
            Space(4f);
            Note(latest.LocalSummary, Pad, ContentW, White, 14);
            Space(8f);
            foreach (var change in latest.LocalChanges)
                Note("• " + change, Pad + 8f, ContentW - 8f, White, 13);

            Space(10f);
            Button(Loc.Get("Window.WhatsNew.SeeAll"), null, Pad, OpenChangelog, 190f);
            Button(Loc.Get("Window.WhatsNew.Dismiss"), null, Pad + 198f, () =>
            {
                Manager.Store.Data.LastSeenVersion = latest.Version;
                Manager.Store.Save();
                GoToTweaks();
            }, 130f);
            Y += 36f;
        }

        if (WelcomePending)
        {
            Section(Loc.Get("Window.Welcome.Title"));
            Note(Loc.Get("Window.Welcome.Text"), Pad, ContentW, White, 14);
            Space(8f);
            Button(Loc.Get("Window.Welcome.Dismiss"), null, Pad, () =>
            {
                Manager.Store.Data.ShowWelcome = false;
                Manager.Store.Save();
                GoToTweaks();
            }, 130f);
            Y += 36f;
        }
    }

    /// <summary>Leaves the welcome / what's new cards once nothing is left to show there.</summary>
    private void GoToTweaks()
    {
        if (WelcomePending || WhatsNewPending)
        {
            RequestRebuild();
            _headerDirty = true;
            return;
        }

        _selected    = SortedTweaks().FirstOrDefault();
        _rowsDirty   = true;
        _headerDirty = true;
        RequestRebuild();
    }

    private TextButtonNode Button(string label, string? tooltip, float x, Action click, float width = 150f)
    {
        var button = new TextButtonNode
        {
            String   = label,
            Position = new Vector2(x, Y),
            Size     = new Vector2(width, 28f),
        };
        if (!string.IsNullOrEmpty(tooltip)) button.TextTooltip = tooltip;
        button.OnClick = click;
        button.AttachNode(Host);
        return button;
    }
}
