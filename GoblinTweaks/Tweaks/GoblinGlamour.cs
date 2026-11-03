using System.IO;
using System.Numerics;
using System.Reflection;
using GoblinTweaks.Core;
using GoblinTweaks.Glamour;
using GoblinTweaks.Glamour.Game;
using GoblinTweaks.Localization;
using GoblinTweaks.UI;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// Goblin Glamour: proposes outfits that go with the character's own colors (skin, hair, eyes), mixing pieces of
/// any set, and shows them in the game's Try On window. Works without AI; the optional AI (the user's own key for a
/// free model) is added on top in the same window.
/// </summary>
/// <remarks>
/// The item catalog (every wearable item and the dominant colors of its icon) is built in the background when the
/// tweak is enabled and cached on disk. Every background result carries a generation number; results of an older
/// generation (the window was closed, the tweak disabled) are dropped.
/// </remarks>
[Tweak(TweakCategory.Interface)]
public sealed partial class GoblinGlamour : Tweak<GoblinGlamour.Options>
{
    public sealed class Options
    {
        /// <summary>Earrings, necklace, bracelets and ring.</summary>
        public bool IncludeAccessories { get; set; }

        public bool IncludeDyes { get; set; } = true;

        public bool IncludeFacewear { get; set; }

        /// <summary>Main hand and off hand. Off: outfits (and what is sent to the AI) leave the weapon as it is.</summary>
        public bool IncludeWeapon { get; set; } = true;

        /// <summary>Only items the character owns (armoire, dresser, bags, armoury chest, equipped).</summary>
        public bool OnlyOwned { get; set; }

        /// <summary>Armor the current job can equip only (weapons always follow the job).</summary>
        public bool JobOnly { get; set; }

        /// <summary>Pieces made for the same role count as sharing a style (off: a samurai may wear tank armor).</summary>
        public bool MatchRole { get; set; }

        /// <summary>Build outfits around the character's colors; off, each outfit gets a palette of its own.</summary>
        public bool UseMyColors { get; set; } = true;

        /// <summary>Key of the chosen theme in <c>themes.json</c>; empty = none; <see cref="RandomTheme"/> = a few at random.</summary>
        public string Theme { get; set; } = string.Empty;

        public List<SavedOutfit> Favorites { get; set; } = [];

        /// <summary>Key of the AI provider (<see cref="AiProviders"/>); the default needs no key.</summary>
        public string Provider { get; set; } = "pollinations";

        /// <summary>Model of the provider; empty = its first free model.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>Full chat completions URL, for the "custom" provider.</summary>
        public string CustomBaseUrl { get; set; } = string.Empty;

        /// <summary>The user's own key for each provider. Stored as plain JSON; never written to the log.</summary>
        public Dictionary<string, string> ApiKeys { get; set; } = [];

        /// <summary>The one key of earlier builds, moved into <see cref="ApiKeys"/> on Enable.</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>The user read the notice about what is sent to the AI provider.</summary>
        public bool PrivacyAccepted { get; set; }

        /// <summary>Pieces the user does not want proposed again.</summary>
        public HashSet<uint> Disliked { get; set; } = [];

        /// <summary>Size of the game's Try On window, in percent of its normal size; applies to every Try On window.</summary>
        public int TryOnPercent { get; set; } = 60;

        /// <summary>The old default (75) was moved to the new one (60) once.</summary>
        public bool TryOnDefaultMoved { get; set; }

        /// <summary>Free words for Generate ("capa, botas, pirata"): shapes in English or Spanish, or words of item names.</summary>
        public string GenerateWords { get; set; } = string.Empty;

        /// <summary>After Create, the AI looks at its own outfit in the Try On picture and improves it (vision models only).</summary>
        public bool ReviewAfterCreate { get; set; } = true;

        /// <summary>The AI tab, kept between sessions (newest first).</summary>
        public List<SavedAiOutfit> AiOutfits { get; set; } = [];

        /// <summary>The AI requests, newest first.</summary>
        public List<AiHistoryEntry> AiHistory { get; set; } = [];
    }

    /// <summary>An outfit the AI made, as kept in the AI tab.</summary>
    public sealed class SavedAiOutfit
    {
        public string Name { get; set; } = string.Empty;

        public Dictionary<GlamourSlot, OutfitPiece> Pieces { get; set; } = [];

        public string Opinion { get; set; } = string.Empty;
    }

    /// <summary>One AI request: <c>Kind</c> = <see cref="HistoryCreate"/> or <see cref="HistoryJudge"/>; <c>Result</c> = Pending, Ok, Failed or Cancelled.</summary>
    public sealed class AiHistoryEntry
    {
        public DateTime When { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string Provider { get; set; } = string.Empty;

        public string Model { get; set; } = string.Empty;

        public string Result { get; set; } = string.Empty;
    }

    /// <summary>An outfit the user kept.</summary>
    public sealed class SavedOutfit
    {
        public string Name { get; set; } = string.Empty;

        public Dictionary<GlamourSlot, OutfitPiece> Pieces { get; set; } = [];
    }

    /// <summary>What the window lists.</summary>
    internal enum CardView { Outfits, Ai, Favorites }

    /// <summary>What the window shows on one card.</summary>
    internal sealed record OutfitCard(string Title, IReadOnlyDictionary<GlamourSlot, OutfitPiece> Pieces, string Reason, int FavoriteIndex);

    /// <summary>The order pieces are shown in on a card.</summary>
    internal static readonly GlamourSlot[] SlotOrder =
    [
        GlamourSlot.MainHand, GlamourSlot.OffHand, GlamourSlot.Head, GlamourSlot.Body, GlamourSlot.Hands,
        GlamourSlot.Legs, GlamourSlot.Feet, GlamourSlot.Ears, GlamourSlot.Neck, GlamourSlot.Wrists,
        GlamourSlot.Ring, GlamourSlot.Facewear,
    ];

    private const int OutfitCount = 15;

    /// <summary>The "random" choice of theme: every press picks <see cref="RandomThemeCount"/> themes, three outfits each.</summary>
    internal const string RandomTheme = "random";
    private const int RandomThemeCount = 5;

    private static IReadOnlyList<Theme>? _themes;
    private static IReadOnlyList<string>? _costumes;
    private static ShapeTable? _shapes;

    private GlamourAddon? _addon;
    private TryOnScaler? _scaler;
    private GlamourerBridge? _glamourer;

    // The pieces worn through Glamourer, if any.
    private IReadOnlyDictionary<GlamourSlot, OutfitPiece>? _worn;
    private GlamourSettingsAddon? _settingsAddon;
    private TextHelpAddon? _help;
    private GlamourHistoryAddon? _historyAddon;

    // Lives as long as the tweak is enabled (the catalog build).
    private CancellationTokenSource? _lifetime;

    // Every generation / AI request gets a number; only the latest may publish its result.
    private int _generation;

    private volatile GlamourCatalog? _catalog;
    private volatile Dictionary<uint, CatalogItem>? _byId;
    private volatile Dictionary<byte, Stain>? _stains;
    private float _catalogProgress;
    private volatile bool _catalogFailed;

    private IReadOnlyList<OutfitProposal> _proposals = [];

    // With the random theme: the theme each proposal was made for (same order as the proposals).
    private IReadOnlyList<string?> _proposalThemes = [];
    private readonly Dictionary<GlamourSlot, OutfitPiece> _locked = [];

    // The pieces each of the last presses of Generate proposed (locked ones left out): the next press avoids them.
    private const int RememberedPresses = 3;
    private readonly Queue<List<uint>> _recent = new();

    /// <summary>Changes whenever the cards to show change (new proposals, favorites, locks, view).</summary>
    internal int CardsVersion { get; private set; }

    /// <summary>
    /// Changes when the list shows new content (another view, new proposals, a new AI outfit): the window then goes
    /// back to the top. Other changes (a lock, a piece left out) keep the list where it was.
    /// </summary>
    internal int ListVersion { get; private set; }

    /// <summary>What the window lists: the proposals, the AI's outfits or the favorites.</summary>
    internal CardView View { get; private set; }

    /// <summary>The character the last proposals were made for (or read when the window opened).</summary>
    internal CharacterInfo? Character { get; private set; }

    /// <summary>A short message for the status line, shown for a few seconds.</summary>
    internal (string Text, DateTime Until) Notice { get; private set; }

    /// <summary>"Only what I own" is on but the Glamour Dresser was not opened this session: its items are unknown.</summary>
    internal bool DresserUnknown { get; private set; }

    internal bool Generating { get; private set; }

    internal Options Current => Settings;

    internal string Text(string key) => T(key);

    internal static IReadOnlyList<Theme> AllThemes => _themes ??= Themes.Parse(Resource("Glamour.themes.json") ?? "[]");

    /// <summary>Words that mark a costume (event outfits, swimwear...), kept out unless the theme asks for them.</summary>
    private static IReadOnlyList<string> CostumeWords
        => _costumes ??= System.Text.Json.JsonSerializer.Deserialize<string[]>(Resource("Glamour.costumes.json") ?? "[]") ?? [];

    /// <summary>Shapes read from item names ("Cape", "Robe", "Sabatons") and the shapes that clash.</summary>
    internal static ShapeTable AllShapes => _shapes ??= ShapeTags.Parse(Resource("Glamour.shapes.json") ?? "{}");

    private static string? Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private GlamourAddon NewAddon() => new()
    {
        InternalName = "GtkGoblinGlamour",
        Title        = Text("Name"),
        Size         = new Vector2(920f, 856f),
        Tweak        = this,
    };

    private GlamourHistoryAddon NewHistoryAddon() => new()
    {
        InternalName    = "GtkGoblinGlamourHistory",
        Title           = $"{Text("Name")} — {Text("History.Title")}",
        Size            = new Vector2(560f, 600f),
        Tweak           = this,
        RespectCloseAll = false,
    };

    private GlamourSettingsAddon NewSettingsAddon() => new()
    {
        InternalName    = "GtkGoblinGlamourSettings",
        Title           = $"{Text("Name")} — {Text("Settings")}",
        Size            = new Vector2(560f, 560f),
        Tweak           = this,
        RespectCloseAll = false,
    };

    protected internal override void Enable()
    {
        _lifetime = new CancellationTokenSource();
        _aiBlocked = AiFailureKind.None;
        _aiWaitUntil = DateTime.MinValue;

        // Earlier builds kept one key for every provider, so choosing another provider sent it there. It belongs to
        // the provider chosen with it (Gemini when that one needs no key: the only one earlier builds suggested).
        if (Settings.ApiKey.Length > 0)
        {
            var owner = AiProviders.Get(Settings.Provider).NeedsKey ? Settings.Provider : "gemini";
            Settings.ApiKeys.TryAdd(owner, Settings.ApiKey);
            Settings.ApiKey = string.Empty;
            SaveSettings(notify: false);
        }

        // Earlier builds defaulted to Gemini, which needs a key: without one, use the free provider.
        if (Settings.Provider == "gemini" && ApiKey.Length == 0)
        {
            Settings.Provider = "pollinations";
            Settings.Model = string.Empty;
            SaveSettings(notify: false);
        }
        _proposals = [];
        _proposalThemes = [];
        _locked.Clear();
        _recent.Clear();
        View = CardView.Outfits;
        Character = null;
        CardsVersion++;
        ListVersion++;

        _addon = NewAddon();
        _settingsAddon = NewSettingsAddon();
        _historyAddon = NewHistoryAddon();
        _aiCards.Clear();
        _aiCards.AddRange(Settings.AiOutfits.Take(MaxAiCards).Select(outfit => new OutfitCard(outfit.Name, outfit.Pieces, outfit.Opinion, -1)));

        // The user found 75 % too big: the old default becomes the new one, a size they chose stays.
        if (!Settings.TryOnDefaultMoved)
        {
            if (Settings.TryOnPercent == 75) Settings.TryOnPercent = 60;
            Settings.TryOnDefaultMoved = true;
            SaveSettings(notify: false);
        }
        _scaler = new TryOnScaler(ReportFailure);
        _scaler.SetFactor(Settings.TryOnPercent / 100f);
        _glamourer = new GlamourerBridge();
        _worn = null;

        StartCatalog(_lifetime.Token);
        RefreshModels(() => { });   // with a key, the models the provider has now
    }

    protected internal override void Disable()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        Interlocked.Increment(ref _generation);

        CancelAi();
        _aiCards.Clear();
        _glamourer?.Revert();
        _glamourer = null;
        _worn = null;
        _scaler?.Dispose();
        _scaler = null;

        _help?.Dispose();
        _help = null;
        _settingsAddon?.Dispose();
        _settingsAddon = null;
        _historyAddon?.Dispose();
        _historyAddon = null;
        _addon?.Dispose();
        _addon = null;

        _catalog = null;
        _byId = null;
        _stains = null;
        _proposals = [];
        _locked.Clear();
        _recent.Clear();
    }

    protected override void OnLanguageChanged()
    {
        if (State != TweakState.Enabled) return;

        Rebuild(ref _addon, NewAddon);
        Rebuild(ref _settingsAddon, NewSettingsAddon);
        Rebuild(ref _historyAddon, NewHistoryAddon);

        var helpWasOpen = _help is { IsOpen: true };
        _help?.Dispose();
        _help = null;
        if (helpWasOpen) Svc.Framework.RunOnTick(ToggleHelp, delayTicks: 6);
    }

    // ── Catalog ───────────────────────────────────────────────────────────────

    private void StartCatalog(CancellationToken token)
    {
        _catalogFailed = false;
        _catalogProgress = 0f;
        var progress = new Progress<float>(value => Volatile.Write(ref _catalogProgress, value));
        _ = Task.Run(async () =>
        {
            try
            {
                var catalog = await new CatalogBuilder().BuildAsync(token, progress).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                // What kind of piece each item is, from its English name.
                var shapes = AllShapes;
                catalog = catalog with { Items = [.. catalog.Items.Select(item => item with { Shapes = ShapeTags.FromName(item.NameEn, shapes) })] };
                Svc.Log.Info($"GoblinGlamour: shapes for {catalog.Items.Count(item => item.Shapes != ShapeTag.None)} of {catalog.Items.Count} pieces.");
                var byId = new Dictionary<uint, CatalogItem>();
                foreach (var item in catalog.Items) byId.TryAdd(item.Id, item);
                _byId = byId;
                _stains = catalog.Stains.ToDictionary(stain => stain.Id);
                _catalog = catalog;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _catalogFailed = true;
                Svc.Log.Warning(ex, "GoblinGlamour: could not build the item catalog.");
            }
        }, token);
    }

    internal bool CatalogReady => _catalog is not null;

    internal bool CatalogFailed => _catalogFailed;

    internal int CatalogPercent => (int)Math.Round(Math.Clamp(Volatile.Read(ref _catalogProgress), 0f, 1f) * 100f);

    internal CatalogItem? Item(uint id) => _byId?.GetValueOrDefault(id);

    internal Stain? StainOf(byte id) => id == 0 ? null : _stains?.GetValueOrDefault(id);

    // ── Generating ────────────────────────────────────────────────────────────

    /// <summary>Reads the character again (the window shows its colors). Framework thread.</summary>
    internal void RefreshCharacter() => Character = CharacterReader.Read() ?? Character;

    /// <summary>What a generation (or an AI request) works with.</summary>
    private sealed record Prepared(
        GlamourCatalog Catalog, CharacterInfo Who, OwnedSnapshot? Owned, GenerateOptions Options,
        IReadOnlyDictionary<GlamourSlot, OutfitPiece> Locked);

    /// <summary>
    /// Reads the character and what it owns and builds the options from the filters, or null (with a message) when
    /// the catalog is not ready or no character is logged in. Framework thread.
    /// </summary>
    private Prepared? Prepare()
    {
        if (_catalog is not { } catalog) return null;

        var who = CharacterReader.Read();
        if (who is null) { Say(Text("Status.NoCharacter")); return null; }
        Character = who;

        var owned = Settings.OnlyOwned ? OwnedItems.Read() : null;
        DresserUnknown = owned is { DresserKnown: false };

        var slots = SlotMask.Armor;
        if (Settings.IncludeWeapon) slots |= SlotMask.MainHand;
        if (Settings.IncludeWeapon && who.HasOffHand) slots |= SlotMask.OffHand;
        if (Settings.IncludeAccessories) slots |= SlotMask.Accessories;
        if (Settings.IncludeFacewear) slots |= SlotMask.Facewear;

        var theme = AllThemes.FirstOrDefault(t => t.Key == Settings.Theme);
        var locked = _locked.Where(pair => slots.Has(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var liked = Settings.Favorites.SelectMany(outfit => outfit.Pieces.Values).Select(piece => piece.ItemId).ToHashSet();
        var recentItems = _recent.SelectMany(press => press).GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
        var recentSets = new Dictionary<uint, int>();
        foreach (var (id, times) in recentItems)
            if (Item(id) is { SetKey: not 0 } item)
                recentSets[item.SetKey] = recentSets.GetValueOrDefault(item.SetKey) + times;
        // Free words for Generate: shapes ("capa", "boots") and words of English item names ("pirate").
        var words = Settings.GenerateWords.Split([' ', ',', ';', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3).ToList();
        var options = new GenerateOptions(slots, who.HasOffHand, Settings.IncludeDyes, theme, words, locked, OutfitCount)
        {
            WantedShapes = ShapeTags.FromRequest(Settings.GenerateWords, AllShapes),
            CostumeWords = CostumeWords,
            Clashes      = AllShapes.Clashes,
            Disliked     = Settings.Disliked.ToHashSet(),
            Liked        = liked,
            RecentItems  = recentItems,
            RecentSets   = recentSets,
            StyleThemes  = AllThemes,
            MatchRole    = Settings.MatchRole,
            UseCharacterColors = Settings.UseMyColors,
        };
        return new Prepared(catalog, who, owned, options, locked);
    }

    /// <summary>Proposes new outfits with the current filters, keeping the locked pieces. Framework thread.</summary>
    internal void Generate() => Guard(GenerateCore);

    private void GenerateCore()
    {
        if (Generating || Prepare() is not { } prepared) return;
        var (catalog, who, owned, options, locked) = prepared;
        var theme = options.Theme;

        var generation = Interlocked.Increment(ref _generation);
        var token = _lifetime?.Token ?? CancellationToken.None;
        Generating = true;
        ShowView(CardView.Outfits);

        var random = new Random();
        IReadOnlyList<Theme?> themes = Settings.Theme == RandomTheme
            ? [.. AllThemes.OrderBy(_ => random.Next()).Take(RandomThemeCount)]
            : [theme];
        if (Settings.Theme == RandomTheme)
            Say(string.Format(Text("Status.RandomThemes"), string.Join(", ", themes.Select(t => Text($"Theme.{t!.Key}")))));

        _ = Task.Run(() =>
        {
            var candidates = CatalogFilter.For(catalog, who, owned, Settings.JobOnly);
            if (themes.Count <= 1)
                return (OutfitEngine.Generate(candidates, catalog.Stains, who.Palette, options with { Theme = themes.FirstOrDefault() }, random),
                        (IReadOnlyList<string?>)[]);

            // One theme after another, three outfits each, continuing the anchor colors and avoiding what the
            // earlier themes of this press already used.
            var perTheme = OutfitCount / themes.Count;
            var proposals = new List<OutfitProposal>();
            var keys = new List<string?>();
            var seen = new Dictionary<uint, int>(options.RecentItems);
            foreach (var each in themes)
            {
                var batch = OutfitEngine.Generate(candidates, catalog.Stains, who.Palette,
                    options with { Theme = each, Count = perTheme, AnchorOffset = proposals.Count, RecentItems = new Dictionary<uint, int>(seen) },
                    random);
                foreach (var proposal in batch)
                {
                    proposals.Add(proposal);
                    keys.Add(each?.Key);
                    foreach (var piece in proposal.Pieces.Values)
                        seen[piece.ItemId] = seen.GetValueOrDefault(piece.ItemId) + 1;
                }
            }
            return ((IReadOnlyList<OutfitProposal>)proposals, (IReadOnlyList<string?>)keys);
        }, token).ContinueWith(task => Svc.Framework.RunOnFrameworkThread(() =>
        {
            if (generation != Volatile.Read(ref _generation) || State != TweakState.Enabled) return;
            Generating = false;
            if (task.IsCompletedSuccessfully)
            {
                (_proposals, _proposalThemes) = task.Result;
                Remember(_proposals, locked);
                ListVersion++;
            }
            else if (task.Exception is { } ex)
            {
                Svc.Log.Warning(ex, "GoblinGlamour: could not generate outfits.");
                Say(Text("Status.Failed"));
            }
            CardsVersion++;
        }), TaskScheduler.Default);
    }

    private void Remember(IReadOnlyList<OutfitProposal> proposals, IReadOnlyDictionary<GlamourSlot, OutfitPiece> locked)
    {
        _recent.Enqueue([.. proposals.SelectMany(proposal => proposal.Pieces)
            .Where(pair => !locked.TryGetValue(pair.Key, out var held) || held != pair.Value)
            .Select(pair => pair.Value.ItemId).Distinct()]);
        while (_recent.Count > RememberedPresses)
            _recent.Dequeue();
    }

    /// <summary>Drops results still on their way (the window closed).</summary>
    internal void CancelPending()
    {
        Interlocked.Increment(ref _generation);
        Generating = false;
        CancelAi();
        Unwear();
    }

    // ── Try On size and Glamourer ─────────────────────────────────────────────

    internal void SetTryOnSize(int percent)
    {
        Settings.TryOnPercent = Math.Clamp(percent, 25, 300);
        SaveSettings(notify: false);
        _scaler?.SetFactor(Settings.TryOnPercent / 100f);
    }

    internal bool GlamourerAvailable => _glamourer?.Available ?? false;

    internal bool IsWorn(OutfitCard card)
        => _worn is { } worn && (_glamourer?.Wearing ?? false)
           && worn.Count == card.Pieces.Count && worn.All(pair => card.Pieces.TryGetValue(pair.Key, out var piece) && piece == pair.Value);

    /// <summary>Wears the outfit on the character through Glamourer, or takes it off if it is the one worn.</summary>
    internal void ToggleWear(OutfitCard card) => Guard(() => ToggleWearCore(card));

    private void ToggleWearCore(OutfitCard card)
    {
        if (_glamourer is null) return;
        if (IsWorn(card)) { Unwear(); return; }
        _glamourer.Revert();   // another outfit worn: start from the player's own clothes, so none of its pieces stay
        _worn = null;

        if (_glamourer.Wear(card.Pieces))
        {
            _worn = card.Pieces;
            Say(string.Format(Text("Status.Worn"), card.Title));
        }
        else
        {
            _glamourer.Revert();   // some pieces were refused: take off the ones that went on, never half an outfit
            Say(Text("Status.GlamourerFailed"));
        }
        CardsVersion++;
    }

    private void Unwear()
    {
        if (_glamourer is not { Wearing: true }) { _worn = null; return; }
        _glamourer.Revert();
        _worn = null;
        CardsVersion++;
    }

    /// <summary>Saves the outfit as a Glamourer design.</summary>
    internal void SendToGlamourer(OutfitCard card) => Guard(() => SendToGlamourerCore(card));

    private void SendToGlamourerCore(OutfitCard card)
    {
        if (_glamourer is null) return;
        // Saved from the player's own clothes, so no piece of an outfit being worn ends up in the design; that
        // outfit goes back on afterwards.
        var worn = _glamourer.Wearing ? _worn : null;
        _glamourer.Revert();
        var name = $"Goblin Glamour - {card.Title} ({DateTime.Now:g})";
        var saved = _glamourer.SaveDesign(name, card.Pieces);
        _worn = worn is not null && _glamourer.Wear(worn) ? worn : null;
        if (_worn is null) _glamourer.Revert();
        Say(saved ? string.Format(Text("Status.GlamourerSaved"), name) : Text("Status.GlamourerFailed"));
        CardsVersion++;
    }

    /// <summary>Runs a window action; an unexpected error turns the tweak off safely instead of leaving it half working.</summary>
    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    /// <summary>The cards of the current view.</summary>
    internal IReadOnlyList<OutfitCard> Cards() => View switch
    {
        CardView.Favorites => [.. Settings.Favorites.Select((outfit, i) => new OutfitCard(outfit.Name, outfit.Pieces, string.Empty, i))],
        CardView.Ai        => _aiCards,
        _                  => [.. _proposals.Select((proposal, i) => new OutfitCard(CardTitle(i), proposal.Pieces, ReasonLine(proposal.Reasons), -1))],
    };

    /// <summary>How many cards each view has, for the tabs.</summary>
    internal int Count(CardView view) => view switch
    {
        CardView.Favorites => Settings.Favorites.Count,
        CardView.Ai        => _aiCards.Count,
        _                  => _proposals.Count,
    };

    /// <summary>"Outfit 3", or "Outfit 3 · Pirate" when the themes were chosen at random.</summary>
    private string CardTitle(int index)
    {
        var title = string.Format(Text("Card.Title"), index + 1);
        return index < _proposalThemes.Count && _proposalThemes[index] is { } key ? $"{title} · {Text($"Theme.{key}")}" : title;
    }

    private string ReasonLine(IReadOnlyList<Reason> reasons)
        => string.Join(" · ", reasons.Select(reason => reason.Code == ReasonCode.SlotEmpty
            ? string.Format(Text("Reason.SlotEmpty"), Text($"Slot.{reason.Slot}"))
            : Text($"Reason.{reason.Code}")).Distinct());

    internal bool IsLocked(GlamourSlot slot, OutfitPiece piece) => _locked.TryGetValue(slot, out var held) && held == piece;

    internal void SetLock(GlamourSlot slot, OutfitPiece piece, bool locked)
    {
        if (locked) _locked[slot] = piece;
        else if (IsLocked(slot, piece)) _locked.Remove(slot);
        CardsVersion++;
    }

    /// <summary>The user does not want this piece again: it is never proposed from the next Generate on.</summary>
    internal void Dislike(uint itemId)
    {
        if (!Settings.Disliked.Add(itemId)) return;
        _locked.Where(pair => pair.Value.ItemId == itemId).Select(pair => pair.Key).ToList().ForEach(slot => _locked.Remove(slot));
        SaveSettings(notify: false);
        Say(string.Format(Text("Status.Disliked"), Item(itemId)?.Name ?? $"#{itemId}"));
        CardsVersion++;
    }

    internal void ClearDisliked()
    {
        Settings.Disliked.Clear();
        SaveSettings(notify: false);
    }

    internal void ShowView(CardView view)
    {
        if (View == view) return;
        View = view;
        CardsVersion++;
        ListVersion++;
    }

    internal void AddFavorite(OutfitCard card)
    {
        var name = string.Format(Text("Favorite.Name"), Settings.Favorites.Count + 1, DateTime.Now.ToString("d"));
        Settings.Favorites.Add(new SavedOutfit { Name = name, Pieces = card.Pieces.ToDictionary() });
        SaveSettings(notify: false);
        Say(string.Format(Text("Status.Saved"), name));
        CardsVersion++;   // the Favorites tab counts it
    }

    internal void RemoveFavorite(int index)
    {
        if (index < 0 || index >= Settings.Favorites.Count) return;
        Settings.Favorites.RemoveAt(index);
        SaveSettings(notify: false);
        CardsVersion++;
    }

    /// <summary>Shows the outfit in the game's Try On window.</summary>
    internal void TryOn(OutfitCard card)
    {
        _ = TryOnPreview.ShowAsync(card.Pieces).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully || !task.Result)
                Svc.Framework.RunOnFrameworkThread(() => Say(Text("Status.TryOnFailed")));
        }, TaskScheduler.Default);
    }

    internal void Say(string text) => Notice = (text, DateTime.UtcNow.AddSeconds(5));

    internal void SaveCurrent(bool cardsChanged = false)
    {
        SaveSettings(notify: false);
        if (cardsChanged) CardsVersion++;
    }

    // ── Windows ───────────────────────────────────────────────────────────────

    /// <summary>Help topics, in order; each has a "Help.{topic}.Title" and "Help.{topic}.Text".</summary>
    private static readonly string[] HelpTopics = ["About", "Colors", "Filters", "TryOn", "Glamourer", "Ai", "AiModels"];

    internal void ToggleHelp()
    {
        _help ??= new TextHelpAddon
        {
            InternalName    = "GtkGoblinGlamourHelp",
            Title           = Text("Help.Title"),
            Size            = new Vector2(760f, 560f),
            RespectCloseAll = false,
            Pages           = [.. HelpTopics.Select(topic => (Text($"Help.{topic}.Title"), Text($"Help.{topic}.Text")))],
        };
        _help.Toggle();
    }

    /// <summary>Opens or closes the AI history.</summary>
    internal void OpenHistory()
    {
        if (_historyAddon is { } window)
        {
            if (window.IsOpen) window.Close(); else window.Open();
        }
    }

    /// <summary>What is written in the AI request box (kept while the window is closed).</summary>
    internal string RequestText { get; set; } = string.Empty;

    /// <summary>Puts an earlier request back in the request box, opening the window if needed.</summary>
    internal void ReuseRequest(string text)
    {
        RequestText = text;
        _addon?.ShowRequest();
        if (_addon is { IsOpen: false } window) window.Open();
    }

    internal void ClearHistory()
    {
        Settings.AiHistory.Clear();
        SaveSettings(notify: false);
    }

    internal void SetGenerateWords(string words, bool save)
    {
        Settings.GenerateWords = words.Trim();
        if (save) SaveSettings(notify: false);
    }

    internal void SetReviewAfterCreate(bool on)
    {
        Settings.ReviewAfterCreate = on;
        SaveSettings(notify: false);
    }

    /// <summary>Opens the settings (leaves them open if they already are).</summary>
    internal void ShowSettings()
    {
        if (_settingsAddon is { IsOpen: false } window) window.Open();
    }

    internal void OpenSettings()
    {
        if (_settingsAddon is { } window)
        {
            if (window.IsOpen) window.Close(); else window.Open();
        }
    }

    public override TweakButton? OpenButton => new(TMain("Open"), TMain("Open.Help"), () => _addon?.Toggle());

    public override TweakButton? HelpButton => new(Loc.Get("Window.Help"), null, ToggleHelp);

    public override IReadOnlyList<TweakButton> Buttons => [new(Loc.Get("Window.Settings"), TMain("Settings.Help"), OpenSettings)];

    protected override IReadOnlyList<string> CommandNames => ["/gglam", "/goblinglamour"];

    protected override void OnCommand() => _addon?.Toggle();
}
