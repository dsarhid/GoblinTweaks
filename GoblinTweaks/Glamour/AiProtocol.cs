using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GoblinTweaks.Glamour;

/// <summary>
/// An item the AI may choose. The AI answers with ids from this list only. Its colors, set and role tell a model that
/// only reads text what the piece looks like and what it goes with (<c>SetKey</c> 0 = no set). <c>Shapes</c> =
/// what kind of piece it is, from its name.
/// </summary>
public sealed record AiCandidate(
    uint Id, string NameEn, GlamourSlot Slot, byte DyeSlots,
    Rgb Primary = default, Rgb Secondary = default, StyleFamily Family = StyleFamily.Any, uint SetKey = 0,
    ShapeTag Shapes = ShapeTag.None);

/// <summary>
/// What is sent to the AI. Judge mode when <c>Current</c> is set, create mode when <c>UserRequest</c> is set
/// (both may be). <c>ImageJpeg</c> = the Try On preview, optional. <c>Language</c> = "en" or "es", for the opinion.
/// <c>Palette</c> = the character's colors, or null when the player chose not to use them ("Use my colors" off).
/// <c>Theme</c> = key of the theme chosen in the window (English, "knight"), null for none or random.
/// <c>MatchRole</c> = "Same role": pieces should be made for the same role.
/// </summary>
public sealed record AiRequestInput(
    CharacterPalette? Palette, string RaceName, string JobName, IReadOnlyDictionary<GlamourSlot, OutfitPiece>? Current,
    IReadOnlyList<AiCandidate> Candidates, IReadOnlyList<Stain> Stains, string? UserRequest, byte[]? ImageJpeg, string Language,
    string? Theme = null, bool MatchRole = false);

public sealed record AiParseResult(bool Ok, string Opinion, IReadOnlyDictionary<GlamourSlot, OutfitPiece> Pieces);

public enum AiFailureKind { None, InvalidKey, QuotaExhausted, RateLimited, ModelNoImages, ModelNotFound, Network, BadResponse }

/// <summary>Why a request failed. <c>RetryAt</c> (UTC) only for <see cref="AiFailureKind.RateLimited"/>.</summary>
public sealed record AiFailure(AiFailureKind Kind, DateTime? RetryAt)
{
    public static readonly AiFailure None = new(AiFailureKind.None, null);
}

/// <summary>
/// Builds the chat completions request for an OpenAI-compatible provider and reads its reply. Pure: no HTTP here,
/// and the API key never goes through this class (it travels only in the Authorization header).
/// </summary>
public static partial class AiProtocol
{
    private static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(60);

    private const string Schema = """{"opinion": string, "pieces": [{"slot": string, "id": number, "stain0": number, "stain1": number}]}""";

    /// <summary>
    /// The request body. <paramref name="reasoningEffort"/> ("low"...) is sent only when given: a model that thinks
    /// too long runs out of room before it answers.
    /// </summary>
    public static string BuildBody(string model, AiRequestInput input, string? reasoningEffort = null)
    {
        var language = input.Language == "es" ? "Spanish (Rioplatense, using 'vos')" : "English";
        // Without dyes the dye list is not sent at all: it is a good part of the request.
        var dyeRule = input.Stains.Count > 0
            ? "Dyes: use ONLY dye ids from the dye list; 0 = no dye. stain1 only for items with 2 dye channels."
            : "No dyes: stain0 and stain1 are always 0.";
        var harmony = input.Palette is null
            ? "Think about color harmony between the pieces"
            : "Think about color harmony with the character's hair, eyes and skin";
        var role = input.MatchRole
            ? "Use pieces made for the same role (the \"made for\" column), as the player asked."
            : "\"made for\" is only a hint: pieces made for any role may be mixed when they look good together.";
        var system = $"""
            You are a fashion stylist for Final Fantasy XIV glamours. You build outfits for the character described,
            mixing pieces from different sets when that looks better.
            Rules:
            - Use ONLY item ids from the candidate list, each in its own slot. Never invent items or names.
            - At most one piece per slot. Leave a slot out rather than force a bad piece.
            - {dyeRule}
            - Every candidate says its main colors. When the player asks for colors ("dark" = black, charcoal and deep
              tones), pick pieces of those colors, or dye pieces that have dye channels to those colors.
            - Keep ONE coherent look: armor goes with armor, robes with robes. Never mix heavy armor with swimwear,
              shorts or casual clothes. Pieces with the same "set" number were designed together, so they are a safe
              choice, but mixing sets is fine when colors and style agree: fill every slot you can.
            - {role}
            - The "shape" column says what kind of piece it is (cape, robe, heavy armor...): use it for requests such as
              "with a cape" and to keep the silhouette coherent.
            - {harmony}, about silhouette and about a coherent style.
            - "opinion": two or three short sentences in {language} explaining the look (and, when judging, what you changed and why).
            Answer ONLY with JSON, no markdown, no other text, with this shape:
            {Schema}
            """;

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Character: {input.RaceName}, job {input.JobName}.\n");
        if (input.Palette is { } p)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Colors: skin #{p.Skin.ToHex()}, hair #{p.Hair.ToHex()}, highlights #{p.Highlights.ToHex()}, eyes #{p.LeftEye.ToHex()} / #{p.RightEye.ToHex()}, lips #{p.Lips.ToHex()}");
            if (p.FacePaint is { } paint)
                text.Append(CultureInfo.InvariantCulture, $", face paint #{paint.ToHex()}");
            text.Append(".\n");
        }
        if (input.Theme is { Length: > 0 } theme)
            text.Append(CultureInfo.InvariantCulture, $"Theme chosen by the player: {theme}. Follow it unless the request says otherwise.\n");
        text.Append('\n');

        var names = input.Candidates.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().NameEn);
        if (input.Current is { } current)
        {
            text.Append("Current outfit (slot|id|name|stain0|stain1):\n");
            foreach (var (slot, piece) in current.OrderBy(pair => pair.Key))
                text.Append(CultureInfo.InvariantCulture,
                    $"{slot}|{piece.ItemId}|{names.GetValueOrDefault(piece.ItemId, "?")}|{piece.Stain0}|{piece.Stain1}\n");
            text.Append(input.ImageJpeg is null ? "" : "The image shows this outfit on the character.\n");
            text.Append("Task: judge this outfit and improve it. Keep what works, replace what does not.\n");
        }
        if (input.UserRequest is { } request)
            text.Append(input.Current is null ? "Task: build one outfit from scratch for this request: " : "The player asks: ")
                .Append(request).Append('\n');

        text.Append("\nCandidates (id|slot|name|colors|set|made for|dye channels|shape):\n");
        foreach (var c in input.Candidates)
        {
            var colors = c.Primary == c.Secondary ? ColorWords.Name(c.Primary) : $"{ColorWords.Name(c.Primary)} / {ColorWords.Name(c.Secondary)}";
            var set = c.SetKey == 0 ? "-" : $"set {c.SetKey}";
            text.Append(CultureInfo.InvariantCulture, $"{c.Id}|{c.Slot}|{c.NameEn}|{colors}|{set}|{c.Family}|{c.DyeSlots}|{ShapeTags.Describe(c.Shapes)}\n");
        }

        if (input.Stains.Count > 0)
        {
            text.Append("\nDyes (id|name|hex):\n");
            foreach (var s in input.Stains)
                text.Append(CultureInfo.InvariantCulture, $"{s.Id}|{s.Name}|{s.Color.ToHex()}\n");
        }

        JsonNode userContent = input.ImageJpeg is { } jpeg
            ? new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = text.ToString() },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) },
                })
            : JsonValue.Create(text.ToString());

        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.7,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = userContent }),
        };
        if (reasoningEffort is not null)
            body["reasoning_effort"] = reasoningEffort;
        return body.ToJsonString();
    }

    /// <summary>
    /// Reads a chat completions reply: takes <c>choices[0].message.content</c>, keeps the text from the first '{'
    /// to the last '}' (drops markdown fences and prose), and keeps only pieces whose id is a candidate of that
    /// same slot. Unknown dyes, and dyes on channels the item does not have, become 0.
    /// </summary>
    public static AiParseResult Parse(string responseBody, IReadOnlyList<AiCandidate> candidates, IReadOnlyList<Stain> stains)
    {
        var failed = new AiParseResult(false, "", new Dictionary<GlamourSlot, OutfitPiece>());
        var content = MessageContent(responseBody);
        if (content is null)
            return failed;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return failed;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(content[start..(end + 1)]);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return failed;
        }
        if (root.ValueKind != JsonValueKind.Object)
            return failed;

        var opinion = root.TryGetProperty("opinion", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
        var byId = candidates.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var stainIds = stains.Select(s => s.Id).ToHashSet();
        var pieces = new Dictionary<GlamourSlot, OutfitPiece>();

        if (root.TryGetProperty("pieces", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !TryGetUInt(entry, "id", out var id)
                    || !byId.TryGetValue(id, out var candidate)
                    || !entry.TryGetProperty("slot", out var slotElement) || slotElement.ValueKind != JsonValueKind.String
                    || !Enum.TryParse<GlamourSlot>(slotElement.GetString(), ignoreCase: true, out var slot)
                    || slot != candidate.Slot
                    || pieces.ContainsKey(slot))
                    continue;

                var stain0 = candidate.DyeSlots >= 1 ? ValidStain(entry, "stain0", stainIds) : (byte)0;
                var stain1 = candidate.DyeSlots >= 2 ? ValidStain(entry, "stain1", stainIds) : (byte)0;
                pieces[slot] = new OutfitPiece(id, stain0, stain1);
            }
        }

        var ok = pieces.Count > 0 || opinion.Length > 0;
        return new AiParseResult(ok, opinion, pieces);
    }

    /// <summary>
    /// Whether a provider URL may carry the key: https, or plain http only to this PC (a local model server such as
    /// Ollama). Anywhere else, plain http would send the key and the request in clear text.
    /// </summary>
    public static bool SafeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>Turns an HTTP status and error body into a failure kind. Status 0 = no answer (network).</summary>
    public static AiFailure Classify(int status, string body, TimeSpan? retryAfter)
    {
        if (status is >= 200 and < 300)
            return AiFailure.None;
        if (status == 0 || status >= 500)
            return new AiFailure(AiFailureKind.Network, null);
        if (status is 401 or 403 || (status == 400 && Has(body, "api key") && (Has(body, "invalid") || Has(body, "not valid"))))
            return new AiFailure(AiFailureKind.InvalidKey, null);
        if (status == 404)   // providers retire models; the model chosen is gone, or this key cannot use it
            return new AiFailure(AiFailureKind.ModelNotFound, null);
        if (status == 402)
        {
            // A free tier that says when to come back is only asking to wait.
            return RetryFromBody(body) is { } retry
                ? new AiFailure(AiFailureKind.RateLimited, DateTime.UtcNow + retry)
                : new AiFailure(AiFailureKind.QuotaExhausted, null);
        }
        if (status == 429)
        {
            // Daily / credit limits end the free quota for today; per-minute limits only ask to wait.
            if (DailyLimit().IsMatch(body) || Has(body, "insufficient_quota") || Has(body, "credits"))
                return new AiFailure(AiFailureKind.QuotaExhausted, null);
            var wait = retryAfter ?? RetryFromBody(body) ?? DefaultRetry;
            return new AiFailure(AiFailureKind.RateLimited, DateTime.UtcNow + wait);
        }
        if (status == 400 && Has(body, "image"))
            return new AiFailure(AiFailureKind.ModelNoImages, null);
        return new AiFailure(AiFailureKind.BadResponse, null);
    }

    /// <summary>
    /// The provider's own words in an error body (<c>error.message</c>, also inside Gemini's one-element array), or
    /// the start of the body; at most 200 characters, for the log. Error bodies never carry the key.
    /// </summary>
    public static string ErrorMessage(string body)
    {
        const int max = 200;
        var message = body;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String)
                message = text.GetString() ?? body;
        }
        catch (JsonException)
        {
        }
        return message.Length > max ? message[..max] : message;
    }

    // Models a chat request cannot use: embeddings, speech, images, video, music, live audio, moderation...
    private static readonly string[] NotChat =
    [
        "embed", "tts", "image", "live", "audio", "transcribe", "whisper", "guard", "robotics", "omni", "veo",
        "imagen", "lyria", "aqa", "banana", "computer-use", "deep-research", "antigravity", "moderation",
    ];

    /// <summary>
    /// The chat models in a provider's model list (<c>data[].id</c>, without Gemini's "models/" prefix): the
    /// <paramref name="suggested"/> ones it still has first, in that order, then the rest by name.
    /// <paramref name="freeOnly"/> keeps only OpenRouter's free models (":free"). Empty when the list cannot be read.
    /// </summary>
    public static IReadOnlyList<string> ChatModels(string body, IReadOnlyList<string> suggested, bool freeOnly)
    {
        var ids = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];
            foreach (var model in data.EnumerateArray())
                if (model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } name)
                    ids.Add(name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name);
        }
        catch (JsonException)
        {
            return [];
        }

        var chat = ids
            .Where(id => !freeOnly || id.EndsWith(":free", StringComparison.Ordinal))
            .Where(id => !NotChat.Any(word => id.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet();
        var first = suggested.Where(chat.Contains).ToList();
        return [.. first, .. chat.Except(first).Order(StringComparer.Ordinal)];
    }

    private static string? MessageContent(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
                return null;

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString();
            if (content.ValueKind == JsonValueKind.Array)   // some providers answer with content parts
                return string.Concat(content.EnumerateArray()
                    .Where(part => part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    .Select(part => part.GetProperty("text").GetString()));
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetUInt(JsonElement entry, string name, out uint value)
    {
        value = 0;
        if (!entry.TryGetProperty(name, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetUInt32(out value);
        return element.ValueKind == JsonValueKind.String && uint.TryParse(element.GetString(), CultureInfo.InvariantCulture, out value);
    }

    private static byte ValidStain(JsonElement entry, string name, HashSet<byte> stainIds)
        => TryGetUInt(entry, name, out var value) && value <= byte.MaxValue && stainIds.Contains((byte)value) ? (byte)value : (byte)0;

    private static TimeSpan? RetryFromBody(string body)
    {
        var match = RetryIn().Match(body);
        return match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Ceiling(seconds))
            : null;
    }

    private static bool Has(string body, string text) => body.Contains(text, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"per[\s_-]?day|perday|daily|\bRPD\b", RegexOptions.IgnoreCase)]
    private static partial Regex DailyLimit();

    [GeneratedRegex(@"(?:try again|retry) (?:in|after) (\d+(?:\.\d+)?)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex RetryIn();
}
