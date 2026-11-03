using GoblinTweaks.Core;
using GoblinTweaks.Glamour;
using GoblinTweaks.Glamour.Game;

namespace GoblinTweaks.Tweaks;

/// <summary>
/// The optional AI of Goblin Glamour: "judge and improve" (the AI looks at an outfit in the Try On window and proposes
/// changes) and "create from scratch" (the AI puts an outfit together from what the user asks). It uses the user's
/// own key for a free model; it chooses only among real candidate pieces sent to it. Requests run off the framework
/// thread and are dropped if the window closes or the tweak is disabled before they answer.
/// </summary>
public sealed partial class GoblinGlamour
{
    private const int JudgeCandidatesPerSlot = 15;
    private const int CreateCandidatesPerSlot = 40;
    private const int MaxAiCards = 5;
    private const int MaxHistory = 30;

    internal const string HistoryCreate = "Create";
    internal const string HistoryJudge = "Judge";

    // When the request names colors: the shortlist the closest pieces are taken from, and how close a dyeable piece counts.
    private const int WideShortlist = 150;
    private const float DyeableDistance = 0.15f;

    // With colors named, a piece of a shape asked for ("una capa negra") counts as this much closer to them.
    private const float ShapeEdge = 0.15f;

    // The free provider without a key answers once every 45 seconds per user: fewer candidates per request keep its
    // small model quick, and running out only means waiting a moment.
    private const int FreeCandidatesPerSlot = 20;
    private static readonly TimeSpan FreeBudgetWait = TimeSpan.FromMinutes(1);

    /// <summary>Frames to let the Try On draw the outfit before its picture is taken.</summary>
    private const int PreviewSettleTicks = 45;

    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(8);

    private readonly AiClient _ai = new();
    private readonly List<OutfitCard> _aiCards = [];
    private CancellationTokenSource? _aiCancel;
    private int _aiGeneration;

    // A rate limit (until a time) or a key / quota problem (until the provider, model or key change).
    private DateTime _aiWaitUntil = DateTime.MinValue;
    private AiFailureKind _aiBlocked = AiFailureKind.None;
    private string _aiBlockedFor = string.Empty;

    // The models each provider has now for the key in use ("provider|key hash"), read from the provider, and the
    // lists already asked for (asked again only after a failure).
    private readonly Dictionary<string, IReadOnlyList<string>> _liveModels = [];
    private readonly HashSet<string> _modelsAsked = [];

    // "Create from scratch" asks once to confirm (it uses more of the free quota).
    private DateTime _confirmCreateUntil = DateTime.MinValue;

    internal bool AiBusy { get; private set; }

    /// <summary>The user has entered what the AI needs: a key, and a URL for the custom provider.</summary>
    internal bool AiConfigured
    {
        get
        {
            var provider = AiProviders.Get(Settings.Provider);
            return (!provider.NeedsKey || ApiKey.Length > 0) && (!provider.Custom || Settings.CustomBaseUrl.Length > 0);
        }
    }

    /// <summary>The user's key for the chosen provider ("" when none): a key only ever goes to its own provider.</summary>
    internal string ApiKey => Settings.ApiKeys.GetValueOrDefault(Settings.Provider) ?? string.Empty;

    /// <summary>Saves (or, with "", forgets) the key of the chosen provider.</summary>
    internal void SetApiKey(string key)
    {
        if (key.Length > 0) Settings.ApiKeys[Settings.Provider] = key;
        else Settings.ApiKeys.Remove(Settings.Provider);
        SaveSettings(notify: false);
    }

    private string ModelsKey => $"{Settings.Provider}|{ApiKey.GetHashCode()}";

    /// <summary>The models to choose from: what the provider has now for this key when known, else the suggestions.</summary>
    internal IReadOnlyList<string> Models => _liveModels.GetValueOrDefault(ModelsKey) ?? AiProviders.Get(Settings.Provider).FreeModels;

    /// <summary>
    /// The model in use: the one chosen while the provider still has it, else the first one. A model saved by an
    /// older version may be gone (gemini-2.5-flash answered 404 to new keys in 2026-11). The custom provider uses
    /// whatever was typed.
    /// </summary>
    internal string AiModel
    {
        get
        {
            if (AiProviders.Get(Settings.Provider).Custom) return Settings.Model;
            var models = Models;
            return models.Contains(Settings.Model) ? Settings.Model : models.FirstOrDefault() ?? string.Empty;
        }
    }

    /// <summary>
    /// Asks the provider, once per key, which models it has now; <paramref name="done"/> runs on the game thread
    /// when a new list arrives. Nothing for the free provider (no key) or the custom one.
    /// </summary>
    internal void RefreshModels(Action done)
    {
        var provider = AiProviders.Get(Settings.Provider);
        if (provider.Custom || !provider.NeedsKey || ApiKey.Length == 0 || _lifetime is null) return;
        var cacheKey = ModelsKey;
        if (!_modelsAsked.Add(cacheKey)) return;

        var apiKey = ApiKey;
        var token = _lifetime.Token;
        _ = Task.Run(() => _ai.ListModelsAsync(provider.BaseUrl, apiKey, provider.FreeModels, provider.FreeOnly, token), token)
            .ContinueWith(task => Svc.Framework.RunOnFrameworkThread(() =>
            {
                if (State != TweakState.Enabled) return;
                if (!task.IsCompletedSuccessfully || task.Result is not { } models)
                {
                    _modelsAsked.Remove(cacheKey);   // try again next time the settings open
                    return;
                }
                _liveModels[cacheKey] = models;
                done();
            }), TaskScheduler.Default);
    }

    /// <summary>Changes when the provider, model, URL or key change: a key or quota problem belongs to that choice.</summary>
    private string AiChoice => $"{Settings.Provider}|{AiModel}|{Settings.CustomBaseUrl}|{ApiKey.GetHashCode()}";

    /// <summary>Seconds left before the provider accepts another request (0 = none).</summary>
    internal int AiWaitSeconds => Math.Max(0, (int)Math.Ceiling((_aiWaitUntil - DateTime.UtcNow).TotalSeconds));

    /// <summary>Why the AI cannot be asked now, or null when it can.</summary>
    internal string? AiUnavailableReason()
    {
        if (!AiConfigured) return Text("Ai.NoKey");
        if (!Settings.PrivacyAccepted) return Text("Ai.NeedPrivacy");
        if (_aiBlocked != AiFailureKind.None && _aiBlockedFor == AiChoice) return Text($"Ai.Failure.{_aiBlocked}");
        if (AiWaitSeconds > 0) return string.Format(Text("Ai.Failure.RateLimited"), AiWaitSeconds);
        if (AiBusy) return Text("Ai.Busy");
        if (!CatalogReady) return Text("Status.Waiting");
        return null;
    }

    /// <summary>"Gemini · gemini-3.6-flash": the provider and model in use.</summary>
    internal string AiLabel => $"{Text($"Provider.{AiProviders.Get(Settings.Provider).Key}")} · {AiModel}";

    internal bool CreateNeedsConfirm => DateTime.UtcNow > _confirmCreateUntil;

    internal void SetPrivacyAccepted(bool accepted)
    {
        Settings.PrivacyAccepted = accepted;
        SaveSettings(notify: false);
    }

    /// <summary>The AI looks at the outfit (a picture of the Try On window if it can) and proposes a better one.</summary>
    internal void Judge(OutfitCard card) => Guard(() => JudgeCore(card));

    private void JudgeCore(OutfitCard card)
    {
        if (AiUnavailableReason() is { } why) { Say(why); return; }
        if (Prepare() is not { } prepared) return;

        var language = UiLanguage == "es" ? "es" : "en";
        Ask(prepared, JudgeCandidatesPerSlot, words: [], current: card.Pieces, request: null, language,
            title: string.Format(Text("Card.AiJudged"), card.Title), withPicture: true);
    }

    /// <summary>
    /// The AI puts an outfit together from <paramref name="request"/>. The first press only warns that it uses more
    /// of the free quota; pressing again within a few seconds sends it.
    /// </summary>
    internal void Create(string request) => Guard(() => CreateCore(request));

    private void CreateCore(string request)
    {
        request = request.Trim();
        if (request.Length == 0) { Say(Text("Ai.EmptyRequest")); return; }
        if (AiUnavailableReason() is { } why) { Say(why); return; }
        if (CreateNeedsConfirm)
        {
            _confirmCreateUntil = DateTime.UtcNow + ConfirmWindow;
            Say($"{Understood(request)} {Text("Ai.ConfirmCreate")}");
            return;
        }
        _confirmCreateUntil = DateTime.MinValue;
        if (Prepare() is not { } prepared) return;

        var words = request.Split([' ', ',', '.', ';', ':', '/', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3).ToList();
        var language = UiLanguage == "es" ? "es" : "en";
        Ask(prepared, CreateCandidatesPerSlot, words, current: null, request, language,
            title: string.Format(Text("Card.AiCreated"), request.Length > 40 ? request[..40] + "..." : request), withPicture: false,
            review: Settings.ReviewAfterCreate && AiProviders.Get(Settings.Provider).Vision);
    }

    /// <summary>"Understood: shapes: cape, boots · colors: negro": what the request names, before it is sent.</summary>
    private string Understood(string request)
    {
        var parts = new List<string>();
        var shapes = ShapeTags.FromRequest(request, AllShapes);
        if (shapes != ShapeTag.None)
            parts.Add(string.Format(Text("Ai.Understood.Shapes"), string.Join(", ",
                Enum.GetValues<ShapeTag>().Where(tag => tag != ShapeTag.None && (shapes & tag) != 0).Select(tag => Text($"Shape.{tag}")))));
        var colors = ColorWords.WordsInRequest(request);
        if (colors.Count > 0)
            parts.Add(string.Format(Text("Ai.Understood.Colors"), string.Join(", ", colors)));
        return parts.Count == 0 ? Text("Ai.Understood.Nothing") : string.Format(Text("Ai.Understood"), string.Join(" · ", parts));
    }

    private void Ask(Prepared prepared, int perSlot, IReadOnlyList<string> words,
        IReadOnlyDictionary<GlamourSlot, OutfitPiece>? current, string? request, string language, string title, bool withPicture,
        bool review = false)
    {
        var (catalog, who, owned, options, _) = prepared;
        var provider = AiProviders.Get(Settings.Provider);
        var url = provider.Custom ? Settings.CustomBaseUrl : provider.BaseUrl;
        var model = AiModel;
        var key = provider.NeedsKey ? ApiKey : string.Empty;
        var effort = provider.ReasoningEffort;
        if (!provider.NeedsKey) perSlot = Math.Min(perSlot, FreeCandidatesPerSlot);
        withPicture &= provider.Vision;
        var jobOnly = Settings.JobOnly;
        var choice = AiChoice;

        _aiCancel?.Cancel();
        _aiCancel?.Dispose();
        _aiCancel = new CancellationTokenSource();
        var token = _aiCancel.Token;
        var generation = Interlocked.Increment(ref _aiGeneration);
        AiBusy = true;
        Say(Text("Ai.Asking"));

        var entry = new AiHistoryEntry
        {
            When = DateTime.UtcNow, Kind = request is null ? HistoryJudge : HistoryCreate, Text = request ?? title,
            Provider = provider.Key, Model = model, Result = "Pending",
        };
        Settings.AiHistory.Insert(0, entry);
        if (Settings.AiHistory.Count > MaxHistory) Settings.AiHistory.RemoveRange(MaxHistory, Settings.AiHistory.Count - MaxHistory);
        SaveHistory();

        _ = Task.Run(async () =>
        {
            // The candidates: the best pieces of every slot for this request, plus the pieces being judged.
            var pool = CatalogFilter.For(catalog, who, owned, jobOnly);
            // Shapes the request asks for ("con capa", "vestido largo"): those pieces are preferred in the shortlist.
            var shapes = request is null ? ShapeTag.None : ShapeTags.FromRequest(request, AllShapes);
            var shortlistOptions = options with { Words = words, WantedShapes = shapes, Locked = new Dictionary<GlamourSlot, OutfitPiece>() };

            // Colors the request names ("algo oscuro con detalles rojos"): the pieces closest to them are sent, out
            // of a wider shortlist, so the AI has pieces of those colors to choose from.
            var wanted = request is null ? [] : ColorWords.InRequest(request);
            var candidates = new Dictionary<uint, AiCandidate>();
            foreach (var slot in SlotOrder.Where(slot => options.Slots.Has(slot)))
            {
                var items = OutfitEngine.Shortlist(pool, slot, who.Palette, shortlistOptions, wanted.Count == 0 ? perSlot : WideShortlist);
                if (wanted.Count > 0)
                    items = [.. items.OrderBy(item => ColorDistance(item, wanted, options.UseDyes) - ((item.Shapes & shapes) != 0 ? ShapeEdge : 0f))
                                     .Take(perSlot)];
                foreach (var item in items)
                    candidates.TryAdd(item.Id, Candidate(item));
            }
            if (current is not null)
                foreach (var piece in current.Values)
                    if (_byId?.GetValueOrDefault(piece.ItemId) is { } item)
                        candidates.TryAdd(item.Id, Candidate(item));

            byte[]? picture = null;
            if (withPicture && current is not null && await TryOnPreview.ShowAsync(current).ConfigureAwait(false))
            {
                picture = await Svc.Framework.RunOnTick(() => TryOnPreview.CaptureJpegAsync(),
                    delayTicks: PreviewSettleTicks, cancellationToken: token).ConfigureAwait(false);
            }

            // "Use my colors" off: the character's colors are not sent either (race and job still are).
            var input = new AiRequestInput(options.UseCharacterColors ? who.Palette : null, who.RaceName, who.JobName, current, [.. candidates.Values],
                options.UseDyes ? catalog.Stains : [], request, picture, language, options.Theme?.Key, options.MatchRole);
            var (result, failure) = await _ai.AskAsync(url, model, key, effort, input, token).ConfigureAwait(false);
            var noImages = false;
            if (failure.Kind == AiFailureKind.ModelNoImages && picture is not null)
            {
                noImages = true;
                (result, failure) = await _ai.AskAsync(url, model, key, effort, input with { ImageJpeg = null }, token).ConfigureAwait(false);
            }
            return (result, failure, noImages);
        }, token).ContinueWith(task => Svc.Framework.RunOnFrameworkThread(() =>
        {
            if (generation != Volatile.Read(ref _aiGeneration) || State != TweakState.Enabled)
            {
                if (entry.Result == "Pending") entry.Result = "Cancelled";
                return;
            }
            AiBusy = false;
            entry.Result = task.IsCanceled ? "Cancelled" : "Failed";   // "Ok" below, once its outfit is in the AI tab
            SaveHistory();

            if (!task.IsCompletedSuccessfully)
            {
                if (task.Exception is { } ex)
                {
                    Svc.Log.Warning(ex.InnerException ?? ex, "GoblinGlamour: AI request failed.");
                    Say(Text("Ai.Failure.Network"));
                }
                return;
            }

            var (result, failure, noImages) = task.Result;
            if (failure.Kind != AiFailureKind.None || result is null)
            {
                OnAiFailure(failure.Kind == AiFailureKind.None ? new AiFailure(AiFailureKind.BadResponse, null) : failure, choice);
                return;
            }

            // Judging keeps the pieces the AI did not change.
            var pieces = new Dictionary<GlamourSlot, OutfitPiece>(current ?? new Dictionary<GlamourSlot, OutfitPiece>());
            foreach (var (slot, piece) in result.Pieces)
                pieces[slot] = piece;
            var opinion = result.Opinion.Length > 0 ? result.Opinion : Text("Ai.NoOpinion");

            // Newest first, in its own tab, which opens: the answer is right there, not mixed with the proposals.
            var card = new OutfitCard(title, pieces, opinion, -1);
            _aiCards.Insert(0, card);
            if (_aiCards.Count > MaxAiCards) _aiCards.RemoveAt(_aiCards.Count - 1);
            Settings.AiOutfits = [.. _aiCards.Select(each => new SavedAiOutfit { Name = each.Title, Pieces = new(each.Pieces), Opinion = each.Reason })];
            entry.Result = "Ok";
            SaveHistory();
            View = CardView.Ai;
            CardsVersion++;
            ListVersion++;
            Say(noImages ? Text("Ai.Failure.ModelNoImages") : Text("Ai.Done"));

            // "Review with picture": the AI looks at what it made, worn, and improves it.
            if (review)
            {
                Judge(card);
                if (AiBusy) Say(Text("Ai.Reviewing"));
            }
        }), TaskScheduler.Default);
    }

    private void SaveHistory()
    {
        SaveSettings(notify: false);
        _historyAddon?.Refresh();
    }

    private static AiCandidate Candidate(CatalogItem item)
        => new(item.Id, item.NameEn, item.Slot, item.DyeSlots, item.Primary, item.Secondary, item.Family, item.SetKey, item.Shapes);

    /// <summary>
    /// How far a piece is from the nearest wanted color (0 = that color). A piece that can be dyed takes any color,
    /// so with dyes on it counts as close, though a piece already of that color is still closer.
    /// </summary>
    private static float ColorDistance(CatalogItem item, IReadOnlyList<Rgb> wanted, bool dyes)
    {
        var distance = wanted.Min(color => ColorMath.Distance(item.Primary, color));
        return dyes && item.DyeSlots > 0 ? Math.Min(distance, DyeableDistance) : distance;
    }

    private void OnAiFailure(AiFailure failure, string choice)
    {
        // The free provider answers 402 when its budget for this user ran out for now: it comes back on its own.
        // (When it says how long, that wait is already in the failure.)
        if (failure.Kind == AiFailureKind.QuotaExhausted && !AiProviders.Get(Settings.Provider).NeedsKey)
            failure = new AiFailure(AiFailureKind.RateLimited, DateTime.UtcNow + FreeBudgetWait);

        switch (failure.Kind)
        {
            case AiFailureKind.RateLimited:
                _aiWaitUntil = failure.RetryAt ?? DateTime.UtcNow.AddSeconds(60);
                Say(string.Format(Text("Ai.Failure.RateLimited"), AiWaitSeconds));
                return;
            case AiFailureKind.InvalidKey or AiFailureKind.ModelNotFound when !AiProviders.Get(Settings.Provider).NeedsKey:
                // Nothing the user could change would lift a block on the free provider (no key, one model): a
                // passing server error only gets a short wait.
                _aiWaitUntil = DateTime.UtcNow + FreeBudgetWait;
                Say(Text("Ai.Failure.Network"));
                return;
            case AiFailureKind.QuotaExhausted or AiFailureKind.InvalidKey or AiFailureKind.ModelNotFound:
                _aiBlocked = failure.Kind;
                _aiBlockedFor = choice;
                if (failure.Kind == AiFailureKind.ModelNotFound)
                {
                    // Read the provider's models again: once the list is in, a model it has is used, and since that
                    // changes the choice, this block lifts by itself.
                    _modelsAsked.Remove(ModelsKey);
                    RefreshModels(() => { });
                }
                break;
        }
        Say(Text($"Ai.Failure.{failure.Kind}"));
    }

    /// <summary>Drops an AI answer still on its way (window closed, tweak disabled).</summary>
    private void CancelAi()
    {
        Interlocked.Increment(ref _aiGeneration);
        _aiCancel?.Cancel();
        _aiCancel?.Dispose();
        _aiCancel = null;
        AiBusy = false;
    }
}
