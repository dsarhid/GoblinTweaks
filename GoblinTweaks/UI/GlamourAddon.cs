using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GoblinTweaks.Glamour;
using GoblinTweaks.Glamour.Game;
using GoblinTweaks.Tweaks;
using GoblinTweaks.UI.Nodes;
using KamiToolKit.BaseTypes;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;

namespace GoblinTweaks.UI;

/// <summary>
/// Main window of Goblin Glamour: the character's colors on top, the filters (theme, job only, only owned,
/// accessories, dyes, facewear, free words) and the Generate button, then tabs (the proposals, the AI's outfits, the favorites)
/// over the outfit cards in a scrolling list, the AI request, and at the bottom the status line, settings and help.
/// </summary>
internal sealed unsafe class GlamourAddon : NativeAddon
{
    private static readonly Vector4 Gold  = new(216 / 255f, 187 / 255f, 125 / 255f, 1f);
    private static readonly Vector4 Body  = new(204 / 255f, 204 / 255f, 204 / 255f, 1f);
    private static readonly Vector4 Muted = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 Warn  = new(0.92f, 0.70f, 0.38f, 1f);

    private const float TopH       = 154f;
    private const float TabsH      = 36f;
    private const float BottomBarH = 38f;
    private const float AiStripH   = 74f;
    private const float ScrollBarW = 26f;
    private const float CardGap    = 12f;
    private const string NoTheme   = "none";

    // An AI opinion gets as many lines as it needs, up to this many (the rest shows on hover).
    private const int MaxOpinionLines = 4;
    private const float CharWidth = 6.4f;   // average width of a 12 px character, to guess the lines

    private PaletteNode? _palette;
    private TextNode? _who;
    private TextButtonNode? _generate;
    private TabBarNode? _tabs;
    private string _tabsSignature = string.Empty;
    private GoblinGlamour.CardView _shownView;
    private int _shownList = -1;
    private TextNode? _status;
    private ScrollingNode<ResNode>? _scroll;
    private TextButtonNode? _create;
    private TextNode? _aiStatus;
    private TextInputNode? _requestInput;

    private int _shownVersion = -1;
    private bool _glamourer;
    private bool _aiConfigured;
    private DateTime _nextStatus = DateTime.MinValue;

    public required GoblinGlamour Tweak { get; init; }

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValues)
    {
        base.OnSetup(addon, atkValues);

        var c  = ContentStartPosition;
        var cs = ContentSize;

        BuildTop(c, cs.X);
        _tabs = new TabBarNode
        {
            Position = new Vector2(c.X, c.Y + TopH),
            Size     = new Vector2(cs.X, 30f),
        };
        _tabs.AttachNode(this);
        CreateScroll();
        BuildAiStrip(new Vector2(c.X, c.Y + cs.Y - BottomBarH - AiStripH), cs.X);
        BuildBottomBar(new Vector2(c.X, c.Y + cs.Y - BottomBarH), new Vector2(cs.X, BottomBarH));

        // Last, so its list opens on top of everything else.
        BuildThemeSelect(new Vector2(c.X, c.Y + 40f));

        Tweak.RefreshCharacter();
        ShowCharacter();
        _shownVersion = -1;
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        base.OnUpdate(addon);

        if (_shownVersion != Tweak.CardsVersion)
        {
            _shownVersion = Tweak.CardsVersion;
            ShowCharacter();
            RebuildTabs();
            RebuildCards(addon);
        }

        if (DateTime.UtcNow >= _nextStatus)
        {
            _nextStatus = DateTime.UtcNow.AddSeconds(0.25);
            UpdateStatus();
        }
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        Tweak.CancelPending();
        NativeFocus.Forget(addon);
        _palette   = null;
        _who       = null;
        _generate  = null;
        _tabs      = null;
        _tabsSignature = string.Empty;
        _shownList = -1;
        _status    = null;
        _scroll    = null;
        _create    = null;
        _aiStatus  = null;
        _requestInput = null;
        base.OnFinalize(addon);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildTop(Vector2 pos, float width)
    {
        var options = Tweak.Current;

        Label(Tweak.Text("Window.Colors"), new Vector2(pos.X + 4f, pos.Y + 4f), 110f, Gold);
        _palette = new PaletteNode(7) { Position = new Vector2(pos.X + 118f, pos.Y + 2f) };
        _palette.AttachNode(this);

        Check(new Vector2(pos.X + 330f, pos.Y + 1f), 200f, Tweak.Text("Window.UseMyColors"), Tweak.Text("Window.UseMyColors.Help"),
            options.UseMyColors, on => { options.UseMyColors = on; Tweak.SaveCurrent(); });
        Check(new Vector2(pos.X + 560f, pos.Y + 1f), 130f, Tweak.Text("Window.Weapon"), Tweak.Text("Window.Weapon.Help"),
            options.IncludeWeapon, on => { options.IncludeWeapon = on; Tweak.SaveCurrent(); });

        _who = new TextNode
        {
            Position      = new Vector2(pos.X + width - 200f, pos.Y + 4f),
            Size          = new Vector2(196f, 18f),
            TextColor     = Body,
            FontSize      = 13,
            AlignmentType = AlignmentType.Right,
        };
        _who.AttachNode(this);

        // Row 2: theme (the drop-down is added last), job only, only owned.
        Label(Tweak.Text("Window.Theme"), new Vector2(pos.X + 4f, pos.Y + 44f), 60f, Gold);
        Check(new Vector2(pos.X + 330f, pos.Y + 41f), 200f, Tweak.Text("Window.JobOnly"), Tweak.Text("Window.JobOnly.Help"),
            options.JobOnly, on => { options.JobOnly = on; Tweak.SaveCurrent(); });
        Check(new Vector2(pos.X + 560f, pos.Y + 41f), width - 564f, Tweak.Text("Window.OnlyOwned"), Tweak.Text("Window.OnlyOwned.Help"),
            options.OnlyOwned, on => { options.OnlyOwned = on; Tweak.SaveCurrent(); });

        // Row 3: what goes into an outfit, and Generate.
        Check(new Vector2(pos.X + 4f, pos.Y + 80f), 170f, Tweak.Text("Window.Accessories"), Tweak.Text("Window.Accessories.Help"),
            options.IncludeAccessories, on => { options.IncludeAccessories = on; Tweak.SaveCurrent(); });
        Check(new Vector2(pos.X + 190f, pos.Y + 80f), 130f, Tweak.Text("Window.Dyes"), Tweak.Text("Window.Dyes.Help"),
            options.IncludeDyes, on => { options.IncludeDyes = on; Tweak.SaveCurrent(); });
        Check(new Vector2(pos.X + 330f, pos.Y + 80f), 170f, Tweak.Text("Window.Facewear"), Tweak.Text("Window.Facewear.Help"),
            options.IncludeFacewear, on => { options.IncludeFacewear = on; Tweak.SaveCurrent(); });

        Check(new Vector2(pos.X + 510f, pos.Y + 80f), 160f, Tweak.Text("Window.MatchRole"), Tweak.Text("Window.MatchRole.Help"),
            options.MatchRole, on => { options.MatchRole = on; Tweak.SaveCurrent(); });

        _generate = new TextButtonNode
        {
            String      = Tweak.Text("Window.Generate"),
            Position    = new Vector2(pos.X + width - 184f, pos.Y + 78f),
            Size        = new Vector2(180f, 28f),
            TextTooltip = Tweak.Text("Window.Generate.Help"),
        };
        _generate.OnClick = Tweak.Generate;
        _generate.AttachNode(this);

        // Row 4: free words for Generate (shapes in English or Spanish, words of item names).
        Label(Tweak.Text("Window.Words"), new Vector2(pos.X + 4f, pos.Y + 122f), 66f, Gold);
        var words = TextBox(new Vector2(pos.X + 70f, pos.Y + 118f), new Vector2(width - 74f, 28f), Tweak.Text("Window.Words.Placeholder"),
            Tweak.Text("Window.Words.Help"), 120,
            text => Tweak.SetGenerateWords(text, save: false), text => Tweak.SetGenerateWords(text, save: true));
        if (options.GenerateWords.Length > 0) words.String = options.GenerateWords;

        new HorizontalLineNode { Position = new Vector2(pos.X, pos.Y + TopH - 4f), Size = new Vector2(width, 2f) }.AttachNode(this);
    }

    private void BuildThemeSelect(Vector2 pos)
    {
        var keys = new List<string> { NoTheme, GoblinGlamour.RandomTheme };
        keys.AddRange(GoblinGlamour.AllThemes.Select(theme => theme.Key)
            .OrderBy(key => Tweak.Text($"Theme.{key}"), StringComparer.CurrentCultureIgnoreCase));

        var select = new StringDropDownNode
        {
            Position         = new Vector2(pos.X + 70f, pos.Y),
            Size             = new Vector2(240f, 26f),
            MaxListOptions   = 14,
            GetLabelFunction = key => Tweak.Text($"Theme.{key}"),
        };
        select.OnOptionSelected = key =>
        {
            Tweak.Current.Theme = key == NoTheme ? string.Empty : key;
            Tweak.SaveCurrent();
        };
        select.AttachNode(this);

        // Set after attaching (see the memory note on KamiToolKit drop-downs).
        select.Options        = keys;
        select.SelectedOption = keys.Contains(Tweak.Current.Theme) ? Tweak.Current.Theme : NoTheme;
    }

    /// <summary>The AI: what to ask for and Create, the privacy check, and which provider and model answer.</summary>
    private void BuildAiStrip(Vector2 pos, float width)
    {
        new HorizontalLineNode { Position = pos, Size = new Vector2(width, 2f) }.AttachNode(this);

        Label(Tweak.Text("Window.Ai"), new Vector2(pos.X + 4f, pos.Y + 14f), 40f, Gold);
        _requestInput = TextBox(new Vector2(pos.X + 44f, pos.Y + 10f), new Vector2(width - 44f - 266f, 28f), Tweak.Text("Window.AiRequest"),
            null, 200, text => Tweak.RequestText = text, text => Tweak.RequestText = text);
        ShowRequest();

        var history = new TextButtonNode
        {
            String      = Tweak.Text("Window.History"),
            Position    = new Vector2(pos.X + width - 256f, pos.Y + 10f),
            Size        = new Vector2(110f, 28f),
            TextTooltip = Tweak.Text("Window.History.Help"),
        };
        history.OnClick = Tweak.OpenHistory;
        history.AttachNode(this);

        _create = new TextButtonNode
        {
            String      = Tweak.Text("Window.Create"),
            Position    = new Vector2(pos.X + width - 140f, pos.Y + 10f),
            Size        = new Vector2(136f, 28f),
            TextTooltip = Tweak.Text("Window.Create.Help"),
        };
        // Without a key the button leads to the settings, where the key goes, instead of sitting greyed out.
        _create.OnClick = () =>
        {
            if (Tweak.AiConfigured) Tweak.Create(Tweak.RequestText);
            else Tweak.ShowSettings();
        };
        _create.AttachNode(this);

        Check(new Vector2(pos.X + 40f, pos.Y + 44f), 260f, Tweak.Text("Window.Privacy"), Tweak.Text("Settings.Privacy"),
            Tweak.Current.PrivacyAccepted, Tweak.SetPrivacyAccepted);
        Check(new Vector2(pos.X + 300f, pos.Y + 44f), 200f, Tweak.Text("Window.Review"), Tweak.Text("Window.Review.Help"),
            Tweak.Current.ReviewAfterCreate, Tweak.SetReviewAfterCreate);

        _aiStatus = new TextNode
        {
            Position      = new Vector2(pos.X + 510f, pos.Y + 48f),
            Size          = new Vector2(width - 514f, 16f),
            TextColor     = Muted,
            FontSize      = 12,
            AlignmentType = AlignmentType.Right,
        };
        _aiStatus.AddTextFlags(TextFlags.Ellipsis);
        _aiStatus.AttachNode(this);
    }

    private void BuildBottomBar(Vector2 pos, Vector2 size)
    {
        new HorizontalLineNode { Position = pos, Size = new Vector2(size.X, 2f) }.AttachNode(this);

        var cy = pos.Y + (size.Y - 26f) / 2f;

        // Size of the game's Try On window, right of the status line.
        const float sizeW = 110f, sizeLabelW = 120f;
        var sizeX = pos.X + size.X - 72f - sizeW;
        new TextNode
        {
            String        = Tweak.Text("Window.TryOnSize"),
            Position      = new Vector2(sizeX - sizeLabelW - 6f, pos.Y + (size.Y - 16f) / 2f),
            Size          = new Vector2(sizeLabelW, 16f),
            TextColor     = Gold,
            FontSize      = 12,
            AlignmentType = AlignmentType.Right,
            TextTooltip   = Tweak.Text("Window.TryOnSize.Help"),
        }.AttachNode(this);
        var tryOnSize = new NumericInputNode
        {
            Min         = 25,
            Max         = 300,
            Step        = 5,
            Value       = Tweak.Current.TryOnPercent,
            Position    = new Vector2(sizeX, cy),
            Size        = new Vector2(sizeW, 26f),
            TextTooltip = Tweak.Text("Window.TryOnSize.Help"),
        };
        tryOnSize.OnValueUpdate = Tweak.SetTryOnSize;
        tryOnSize.AttachNode(this);

        _status = new TextNode
        {
            Position  = new Vector2(pos.X + 6f, pos.Y + (size.Y - 16f) / 2f),
            Size      = new Vector2(Math.Max(0f, sizeX - sizeLabelW - 12f - (pos.X + 6f)), 16f),
            TextColor = Muted,
            FontSize  = 12,
        };
        _status.AddTextFlags(TextFlags.Ellipsis);
        _status.AttachNode(this);

        var settings = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.GearCog,
            Position    = new Vector2(pos.X + size.X - 64f, cy),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Tweak.Text("Settings"),
        };
        settings.OnClick = Tweak.OpenSettings;
        settings.AttachNode(this);

        var help = new CircleButtonNode
        {
            Icon        = CircleButtonIcon.QuestionMark,
            Position    = new Vector2(pos.X + size.X - 32f, cy),
            Size        = new Vector2(28f, 28f),
            TextTooltip = Tweak.Text("Help.Title"),
        };
        help.OnClick = Tweak.ToggleHelp;
        help.AttachNode(this);
    }

    private void CreateScroll()
    {
        var c  = ContentStartPosition;
        var cs = ContentSize;
        _scroll = new ScrollingNode<ResNode>
        {
            Position          = new Vector2(c.X, c.Y + TopH + TabsH),
            Size              = new Vector2(cs.X, cs.Y - TopH - TabsH - BottomBarH - AiStripH - 4f),
            AutoHideScrollBar = true,
        };
        _scroll.AttachNode(this);
    }

    /// <summary>Puts <see cref="GoblinGlamour.RequestText"/> in the request box (a request from the history).</summary>
    internal void ShowRequest()
    {
        if (_requestInput is not null && Tweak.RequestText.Length > 0) _requestInput.String = Tweak.RequestText;
    }

    /// <summary>A one-line text box without the game's "n/max" counter, which would sit on top of the text.</summary>
    private TextInputNode TextBox(Vector2 position, Vector2 size, string placeholder, string? tooltip, int max,
                                  Action<string> typed, Action<string> done)
    {
        var box = new TextInputNode
        {
            Position          = position,
            Size              = size,
            PlaceholderString = placeholder,
            MaxCharacters     = max,
            ShowLimitText     = false,
        };
        if (tooltip is not null) box.TextTooltip = tooltip;
        box.OnInputReceived = text => typed(text.ToString());
        box.OnInputComplete = text => done(text.ToString());
        box.AttachNode(this);
        // The game shows its own counter again whenever the box updates: it is also made see-through.
        box.TextLimitsNode.IsVisible = false;
        box.TextLimitsNode.TextColor = Vector4.Zero;
        box.TextLimitsNode.TextOutlineColor = Vector4.Zero;
        return box;
    }

    private void Label(string text, Vector2 position, float width, Vector4 color)
        => new TextNode
        {
            String    = text,
            Position  = position,
            Size      = new Vector2(width, 18f),
            TextColor = color,
            FontSize  = 13,
        }.AttachNode(this);

    private void Check(Vector2 position, float width, string label, string tooltip, bool value, Action<bool> set)
    {
        var box = new CheckboxNode
        {
            String      = label,
            Position    = position,
            Size        = new Vector2(width, 24f),
            IsChecked   = value,
            TextTooltip = tooltip,
        };
        box.OnClick = set;
        box.AttachNode(this);
    }

    // ── Content ───────────────────────────────────────────────────────────────

    private void ShowCharacter()
    {
        if (Tweak.Character is not { } who) return;

        var p = who.Palette;
        var colors = new List<(string, Rgb)>
        {
            (Tweak.Text("Color.Skin"), p.Skin), (Tweak.Text("Color.Hair"), p.Hair), (Tweak.Text("Color.Highlights"), p.Highlights),
            (Tweak.Text("Color.LeftEye"), p.LeftEye), (Tweak.Text("Color.RightEye"), p.RightEye), (Tweak.Text("Color.Lips"), p.Lips),
        };
        if (p.FacePaint is { } paint) colors.Add((Tweak.Text("Color.FacePaint"), paint));
        _palette?.SetColors(colors);

        if (_who is not null) _who.String = GlamourNames.Who(who);
    }

    /// <summary>The tab labels carry the counts: rebuilt when a count changes, and the shown view selected.</summary>
    private void RebuildTabs()
    {
        if (_tabs is null) return;

        var tabs = new (GoblinGlamour.CardView View, string Label)[]
        {
            (GoblinGlamour.CardView.Outfits, string.Format(Tweak.Text("Tab.Outfits"), Tweak.Count(GoblinGlamour.CardView.Outfits))),
            (GoblinGlamour.CardView.Ai, string.Format(Tweak.Text("Tab.Ai"), Tweak.Count(GoblinGlamour.CardView.Ai))),
            (GoblinGlamour.CardView.Favorites, string.Format(Tweak.Text("Tab.Favorites"), Tweak.Count(GoblinGlamour.CardView.Favorites))),
        };
        var signature = string.Join('|', tabs.Select(tab => tab.Label));
        var current = tabs.First(tab => tab.View == Tweak.View).Label;

        if (signature != _tabsSignature)
        {
            _tabsSignature = signature;
            NativeFocus.Release(InternalAddon);
            _tabs.Clear();
            foreach (var (view, label) in tabs)
                _tabs.AddTab(label, () => Tweak.ShowView(view));
        }
        else if (_shownView == Tweak.View) return;

        _shownView = Tweak.View;
        _tabs.SelectTab(current);
    }

    /// <summary>
    /// Replaces the cards with the current ones. Never runs inside a click of one of them: it is called from the update.
    /// The list keeps its place unless it shows new content (<see cref="GoblinGlamour.ListVersion"/>).
    /// </summary>
    private void RebuildCards(AtkUnitBase* addon)
    {
        if (_scroll is null) return;

        var keep = _shownList == Tweak.ListVersion ? _scroll.ScrollBarNode.ScrollPosition : 0;
        _shownList = Tweak.ListVersion;

        NativeFocus.Release(addon);
        _scroll.Dispose();
        CreateScroll();

        var width = _scroll.Width - ScrollBarW - 8f;
        var y = 6f;
        var opinionLine = (int)(width / CharWidth);
        var cards = Tweak.Cards();
        foreach (var card in cards)
        {
            var favorite = card.FavoriteIndex >= 0;
            var buttons = new List<CardButton> { new(Tweak.Text("Card.TryOn"), null, () => Tweak.TryOn(card)) };
            if (_glamourer)
            {
                var worn = Tweak.IsWorn(card);
                buttons.Add(new(Tweak.Text(worn ? "Card.Unwear" : "Card.Wear"), Tweak.Text("Card.Wear.Help"), () => Tweak.ToggleWear(card)));
                buttons.Add(new(Tweak.Text("Card.Glamourer"), Tweak.Text("Card.Glamourer.Help"), () => Tweak.SendToGlamourer(card)));
            }
            buttons.Add(favorite
                ? new(Tweak.Text("Card.Remove"), null, () => Tweak.RemoveFavorite(card.FavoriteIndex))
                : new(Tweak.Text("Card.Favorite"), null, () => Tweak.AddFavorite(card)));
            if (Tweak.AiConfigured)
                buttons.Add(new(Tweak.Text("Card.Judge"), Tweak.Text("Card.Judge.Help"), () => Tweak.Judge(card)));

            var actions = new CardActions(
                buttons,
                Tweak.Text("Card.Lock"),
                favorite ? null : (piece, on) => Tweak.SetLock(piece.Slot, piece.Piece, on),
                Tweak.Text("Card.Dislike"),
                favorite ? null : piece => Tweak.Dislike(piece.Piece.ItemId));

            // An AI outfit shows its whole opinion; the others keep their reason on one line.
            var lines = Tweak.View == GoblinGlamour.CardView.Ai
                ? Math.Clamp((card.Reason.Length + opinionLine - 1) / Math.Max(1, opinionLine), 1, MaxOpinionLines)
                : 1;
            new OutfitCardNode(width, card.Title, card.Reason, lines, Pieces(card), actions)
            {
                Position = new Vector2(8f, y),
            }.AttachNode(_scroll.ContentNode);
            y += OutfitCardNode.HeightFor(lines) + CardGap;
        }

        if (cards.Count == 0)
        {
            var empty = new TextNode
            {
                String        = EmptyText(),
                Position      = new Vector2(8f, y + 20f),
                Size          = new Vector2(width, 60f),
                TextColor     = Muted,
                FontSize      = 13,
                AlignmentType = AlignmentType.TopLeft,
            };
            empty.AddTextFlags(TextFlags.MultiLine | TextFlags.WordWrap);
            empty.AttachNode(_scroll.ContentNode);
            y += 90f;
        }

        _scroll.ContentNode.Height = MathF.Max(_scroll.Height, y + 8f);
        _scroll.RecalculateSizes();
        if (keep > 0) _scroll.ScrollBarNode.ScrollPosition = keep;
        NativeFocus.RefreshCollisions(addon);
    }

    private string EmptyText()
    {
        if (Tweak.View == GoblinGlamour.CardView.Favorites) return Tweak.Text("Status.FavoritesEmpty");
        if (Tweak.View == GoblinGlamour.CardView.Ai) return Tweak.Text("Status.AiEmpty");
        if (!Tweak.CatalogReady) return Tweak.Text("Status.Waiting");
        return Tweak.Text("Status.Ready");
    }

    private List<CardPiece> Pieces(GoblinGlamour.OutfitCard card)
    {
        var pieces = new List<CardPiece>();
        foreach (var slot in GoblinGlamour.SlotOrder)
        {
            if (!card.Pieces.TryGetValue(slot, out var piece) || piece.ItemId == 0) continue;

            var item  = Tweak.Item(piece.ItemId);
            var dye0  = Tweak.StainOf(piece.Stain0);
            var dye1  = Tweak.StainOf(piece.Stain1);
            var lines = new List<string> { $"{Tweak.Text($"Slot.{slot}")}: {item?.Name ?? $"#{piece.ItemId}"}" };
            if (dye0 is not null) lines.Add(dye0.Name);
            if (dye1 is not null) lines.Add(dye1.Name);

            pieces.Add(new CardPiece(slot, piece, item?.IconId ?? 0, string.Join("\n", lines),
                dye0?.Color, dye1?.Color, Tweak.IsLocked(slot, piece)));
        }
        return pieces;
    }

    private void UpdateStatus()
    {
        // Glamourer loaded or unloaded, or the AI key set or removed: the cards gain or lose their buttons.
        if (Tweak.GlamourerAvailable != _glamourer)
        {
            _glamourer = !_glamourer;
            _shownVersion = -1;
        }
        if (Tweak.AiConfigured != _aiConfigured)
        {
            _aiConfigured = !_aiConfigured;
            _shownVersion = -1;
        }

        var unavailable = Tweak.AiUnavailableReason();
        if (_create is not null)
        {
            _create.IsEnabled = unavailable is null || !Tweak.AiConfigured;
            _create.String = Tweak.Text(!Tweak.AiConfigured ? "Window.AddKey" : Tweak.CreateNeedsConfirm ? "Window.Create" : "Window.CreateConfirm");
        }
        if (_aiStatus is not null)
        {
            _aiStatus.String = $"{Tweak.AiLabel} · {unavailable ?? Tweak.Text("Window.Ai.Ready")}";
            _aiStatus.TextColor = unavailable is null ? Muted : Warn;
        }

        if (_generate is not null) _generate.IsEnabled = Tweak.CatalogReady && !Tweak.Generating;

        if (_status is null) return;

        var (notice, until) = Tweak.Notice;
        string text;
        var color = Muted;
        if (DateTime.UtcNow < until) { text = notice; color = Gold; }
        else if (Tweak.CatalogFailed) { text = Tweak.Text("Status.CatalogFailed"); color = Warn; }
        else if (!Tweak.CatalogReady) text = string.Format(Tweak.Text("Status.Building"), Tweak.CatalogPercent);
        else if (Tweak.Generating) text = Tweak.Text("Status.Generating");
        else if (Tweak.Current.OnlyOwned && Tweak.DresserUnknown && Tweak.View == GoblinGlamour.CardView.Outfits) { text = Tweak.Text("DresserUnknown"); color = Warn; }
        else text = string.Empty;

        _status.String = text;
        _status.TextColor = color;
    }
}
