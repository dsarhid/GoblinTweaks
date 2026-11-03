using System.Text.Json;

namespace GoblinTweaks.Tests;

public class AiProtocolTests
{
    private static readonly CharacterPalette Palette = new(
        Rgb.FromHex("E8C0A0"), Rgb.FromHex("A02828"), Rgb.FromHex("C04040"),
        Rgb.FromHex("3080C0"), Rgb.FromHex("3080C0"), Rgb.FromHex("C06070"), null);

    private static readonly List<AiCandidate> Candidates =
    [
        new(101, "Corsair's Coat", GlamourSlot.Body, 2),
        new(102, "Plain Trousers", GlamourSlot.Legs, 1),
        new(103, "Leather Boots", GlamourSlot.Feet, 0),
    ];

    private static readonly List<Stain> Stains = [new(1, "Snow White", Rgb.FromHex("F0F0F0")), new(5, "Dalamud Red", Rgb.FromHex("C81E1E"))];

    private static AiRequestInput Input(bool judge = true, byte[]? image = null, string? request = null)
        => new(Palette, "Miqo'te", "Bard",
               judge ? new Dictionary<GlamourSlot, OutfitPiece> { [GlamourSlot.Body] = new(101, 1, 5) } : null,
               Candidates, Stains, request, image, "es");

    /// <summary>A chat completions reply whose message content is <paramref name="content"/>.</summary>
    private static string Reply(string content)
        => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    private static JsonElement Body(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public void BuildBody_CandidatesSayTheirColorsSetAndRole()
    {
        var candidates = new List<AiCandidate>
        {
            new(201, "Darkvein Coat", GlamourSlot.Body, 1, Rgb.FromHex("101010"), Rgb.FromHex("6E0F0F"), StyleFamily.Tank, 77),
        };
        var input = Input(judge: false, request: "algo oscuro") with { Candidates = candidates };
        var text = Body(AiProtocol.BuildBody("m", input)).GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("201|Body|Darkvein Coat|black / dark red|set 77|Tank|1", text);
    }

    [Fact]
    public void BuildBody_IncludesShapes()
    {
        var candidates = new List<AiCandidate>
        {
            new(301, "Darkvein Cape", GlamourSlot.Body, 1, Shapes: ShapeTag.Cape | ShapeTag.HeavyArmor),
            new(302, "Plain Trousers", GlamourSlot.Legs, 1),
        };
        var text = Body(AiProtocol.BuildBody("m", Input(judge: false, request: "algo") with { Candidates = candidates }))
            .GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("shape", text);
        Assert.Contains("|1|cape, heavy armor", text);
        Assert.Contains("302|Legs|Plain Trousers|", text);
    }

    [Fact]
    public void BuildBody_ReasoningEffortOnlyWhenGiven()
    {
        Assert.Equal("low", Body(AiProtocol.BuildBody("m", Input(), "low")).GetProperty("reasoning_effort").GetString());
        Assert.False(Body(AiProtocol.BuildBody("m", Input())).TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void BuildBody_WithoutMyColorsSendsNoCharacterColors()
    {
        var root = Body(AiProtocol.BuildBody("m", Input(judge: false, request: "algo") with { Palette = null }));
        var system = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
        var text = root.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("skin", text);
        Assert.DoesNotContain("A02828", text);   // the hair
        Assert.DoesNotContain("skin", system);
        Assert.Contains("Miqo'te", text);
    }

    [Fact]
    public void BuildBody_SaysTheChosenTheme()
    {
        var withTheme = Body(AiProtocol.BuildBody("m", Input(judge: false, request: "algo") with { Theme = "knight" }))
            .GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("Theme chosen by the player: knight", withTheme);

        var without = Body(AiProtocol.BuildBody("m", Input(judge: false, request: "algo")))
            .GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("Theme chosen", without);
    }

    [Fact]
    public void BuildBody_SameRoleOnlyWhenAsked()
    {
        string System(bool matchRole) => Body(AiProtocol.BuildBody("m", Input() with { MatchRole = matchRole }))
            .GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("made for the same role", System(true));
        Assert.Contains("any role may be mixed", System(false));
    }

    [Fact]
    public void BuildBody_NoDyesSendsNoDyeList()
    {
        var root = Body(AiProtocol.BuildBody("m", Input(judge: false, request: "algo") with { Stains = [] }));
        var system = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
        var text = root.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("Dyes (id|name|hex)", text);
        Assert.Contains("No dyes", system);
    }

    [Fact]
    public void BuildBody_SetsAreAllowedToMix()
    {
        var system = Body(AiProtocol.BuildBody("m", Input())).GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.DoesNotContain("always match", system);
        Assert.Contains("mixing sets is fine", system);
    }

    [Fact]
    public void BuildBody_JudgeModeHasImageAndCurrent()
    {
        var body = AiProtocol.BuildBody("gemini-3.6-flash", Input(image: [1, 2, 3]));
        var root = Body(body);
        Assert.Equal("gemini-3.6-flash", root.GetProperty("model").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var parts = messages[1].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        var text = parts.EnumerateArray().First(p => p.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
        Assert.Contains("101|Body|Corsair's Coat", text);
        Assert.Contains("A02828", text);
        Assert.Contains("5|Dalamud Red|C81E1E", text);
        var image = parts.EnumerateArray().Single(p => p.GetProperty("type").GetString() == "image_url");
        Assert.Equal("data:image/jpeg;base64,AQID", image.GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void BuildBody_NoImage_HasNoImageUrl()
    {
        var body = AiProtocol.BuildBody("m", Input(judge: false, request: "a pirate captain"));
        Assert.DoesNotContain("image_url", body);
        Assert.Contains("a pirate captain", body);
    }

    [Fact]
    public void BuildBody_NeverContainsKey()
    {
        var body = AiProtocol.BuildBody("m", Input());
        Assert.DoesNotContain("Authorization", body);
        Assert.DoesNotContain("Bearer", body);
    }

    [Fact]
    public void Parse_PlainJson()
    {
        var result = AiProtocol.Parse(Reply("""{"opinion":"Muy bien","pieces":[{"slot":"Body","id":101,"stain0":5,"stain1":1},{"slot":"Legs","id":102,"stain0":1,"stain1":0}]}"""), Candidates, Stains);
        Assert.True(result.Ok);
        Assert.Equal("Muy bien", result.Opinion);
        Assert.Equal(new OutfitPiece(101, 5, 1), result.Pieces[GlamourSlot.Body]);
        Assert.Equal(new OutfitPiece(102, 1, 0), result.Pieces[GlamourSlot.Legs]);
    }

    [Fact]
    public void Parse_FencedJson()
    {
        var result = AiProtocol.Parse(Reply("```json\n{\"opinion\":\"ok\",\"pieces\":[{\"slot\":\"Feet\",\"id\":103}]}\n```"), Candidates, Stains);
        Assert.True(result.Ok);
        Assert.Equal(103u, result.Pieces[GlamourSlot.Feet].ItemId);
    }

    [Fact]
    public void Parse_ProseAroundJson()
    {
        var result = AiProtocol.Parse(Reply("Here you go! {\"opinion\":\"ok\",\"pieces\":[{\"slot\":\"Body\",\"id\":101,\"stain0\":1}]} Hope you like it."), Candidates, Stains);
        Assert.True(result.Ok);
        Assert.Equal(new OutfitPiece(101, 1, 0), result.Pieces[GlamourSlot.Body]);
    }

    [Fact]
    public void Parse_DropsUnknownId()
    {
        var result = AiProtocol.Parse(Reply("""{"opinion":"x","pieces":[{"slot":"Body","id":999},{"slot":"Legs","id":102}]}"""), Candidates, Stains);
        Assert.True(result.Ok);
        Assert.Single(result.Pieces);
        Assert.True(result.Pieces.ContainsKey(GlamourSlot.Legs));
    }

    [Fact]
    public void Parse_DropsWrongSlot()
    {
        var result = AiProtocol.Parse(Reply("""{"opinion":"x","pieces":[{"slot":"Head","id":101},{"slot":"legs","id":102}]}"""), Candidates, Stains);
        Assert.False(result.Pieces.ContainsKey(GlamourSlot.Head));
        Assert.False(result.Pieces.ContainsKey(GlamourSlot.Body));
        Assert.True(result.Pieces.ContainsKey(GlamourSlot.Legs));
    }

    [Fact]
    public void Parse_UnknownStainBecomesZero()
    {
        var result = AiProtocol.Parse(Reply("""{"opinion":"x","pieces":[{"slot":"Body","id":101,"stain0":77,"stain1":5},{"slot":"Legs","id":102,"stain0":5,"stain1":5},{"slot":"Feet","id":103,"stain0":5}]}"""), Candidates, Stains);
        Assert.Equal(new OutfitPiece(101, 0, 5), result.Pieces[GlamourSlot.Body]);
        Assert.Equal(new OutfitPiece(102, 5, 0), result.Pieces[GlamourSlot.Legs]); // one dye channel only
        Assert.Equal(new OutfitPiece(103, 0, 0), result.Pieces[GlamourSlot.Feet]); // not dyeable
    }

    [Fact]
    public void Parse_Garbage_NotOk()
    {
        Assert.False(AiProtocol.Parse("not json at all", Candidates, Stains).Ok);
        Assert.False(AiProtocol.Parse(Reply("I cannot help with that."), Candidates, Stains).Ok);
        Assert.False(AiProtocol.Parse(Reply("{\"opinion\": broken"), Candidates, Stains).Ok);
        Assert.False(AiProtocol.Parse("""{"choices":[]}""", Candidates, Stains).Ok);
    }

    [Fact]
    public void Classify_401_InvalidKey()
    {
        Assert.Equal(AiFailureKind.InvalidKey, AiProtocol.Classify(401, """{"error":{"message":"Invalid API Key"}}""", null).Kind);
        Assert.Equal(AiFailureKind.InvalidKey, AiProtocol.Classify(403, "", null).Kind);
        // Gemini answers 400 for a bad key.
        Assert.Equal(AiFailureKind.InvalidKey, AiProtocol.Classify(400, """[{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}]""", null).Kind);
    }

    [Fact]
    public void Classify_429Quota_QuotaExhausted()
    {
        const string geminiDaily = """[{"error":{"code":429,"message":"You exceeded your current quota, please check your plan and billing details.","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel-FreeTier"}]}]}}]""";
        const string openRouterDaily = """{"error":{"message":"Rate limit exceeded: free-models-per-day. Add 10 credits to unlock 1000 free model requests per day","code":429}}""";
        const string groqDaily = """{"error":{"message":"Rate limit reached for model `llama-3.3-70b-versatile` on requests per day (RPD): Limit 1000, Used 1000","type":"requests","code":"rate_limit_exceeded"}}""";
        Assert.Equal(AiFailureKind.QuotaExhausted, AiProtocol.Classify(429, geminiDaily, null).Kind);
        Assert.Equal(AiFailureKind.QuotaExhausted, AiProtocol.Classify(429, openRouterDaily, null).Kind);
        Assert.Equal(AiFailureKind.QuotaExhausted, AiProtocol.Classify(429, groqDaily, null).Kind);
        Assert.Equal(AiFailureKind.QuotaExhausted, AiProtocol.Classify(402, """{"error":{"message":"Insufficient credits"}}""", null).Kind);
    }

    [Fact]
    public void Classify_429_RateLimitedWithRetry()
    {
        const string groqMinute = """{"error":{"message":"Rate limit reached for model `llama-3.3-70b-versatile` on tokens per minute (TPM): Limit 6000, Used 5800, Requested 900. Please try again in 7.5s.","type":"tokens","code":"rate_limit_exceeded"}}""";
        const string geminiMinute = """[{"error":{"code":429,"message":"You exceeded your current quota.","status":"RESOURCE_EXHAUSTED","details":[{"violations":[{"quotaId":"GenerateRequestsPerMinutePerProjectPerModel-FreeTier"}]}]}}]""";

        var before = DateTime.UtcNow;
        var withHeader = AiProtocol.Classify(429, geminiMinute, TimeSpan.FromSeconds(20));
        Assert.Equal(AiFailureKind.RateLimited, withHeader.Kind);
        Assert.InRange(withHeader.RetryAt!.Value, before.AddSeconds(19), DateTime.UtcNow.AddSeconds(21));

        var fromBody = AiProtocol.Classify(429, groqMinute, null);
        Assert.Equal(AiFailureKind.RateLimited, fromBody.Kind);
        Assert.InRange(fromBody.RetryAt!.Value, before.AddSeconds(7), DateTime.UtcNow.AddSeconds(9));

        var fallback = AiProtocol.Classify(429, "{}", null);
        Assert.InRange(fallback.RetryAt!.Value, before.AddSeconds(59), DateTime.UtcNow.AddSeconds(61));
    }

    [Fact]
    public void Classify_402WithRetry_RateLimited()
    {
        // Pollinations' anonymous tier: one reply every 45 seconds, and it says when to come back.
        const string freeLimit = """{"status":402,"success":false,"error":{"code":"FREE_LIMIT","message":"Free limit reached: 1 reply every 45 seconds per IP. Retry after 11s, or get unlimited replies with a pollen key"}}""";
        var failure = AiProtocol.Classify(402, freeLimit, null);
        Assert.Equal(AiFailureKind.RateLimited, failure.Kind);
        var wait = failure.RetryAt!.Value - DateTime.UtcNow;
        Assert.InRange(wait.TotalSeconds, 9, 12);
    }

    [Fact]
    public void Classify_400Image_ModelNoImages()
    {
        Assert.Equal(AiFailureKind.ModelNoImages, AiProtocol.Classify(400, """{"error":{"message":"This model does not support image input"}}""", null).Kind);
        Assert.Equal(AiFailureKind.Network, AiProtocol.Classify(0, "", null).Kind);
        Assert.Equal(AiFailureKind.None, AiProtocol.Classify(200, "", null).Kind);
    }

    [Theory]
    [InlineData("https://api.example.com/v1/chat/completions", true)]
    [InlineData("http://localhost:11434/v1/chat/completions", true)]    // a model on this PC
    [InlineData("http://127.0.0.1:1234/v1/chat/completions", true)]
    [InlineData("http://example.com/v1/chat/completions", false)]       // the key would travel in clear text
    [InlineData("ftp://example.com/x", false)]
    [InlineData("not a url", false)]
    public void SafeUrl_HttpsOrThisPc(string url, bool safe)
        => Assert.Equal(safe, AiProtocol.SafeUrl(url));

    [Fact]
    public void Classify_404_ModelNotFound()
    {
        const string gemini = """[{"error":{"code":404,"message":"models/gemini-2.5-flash is not found for API version v1beta","status":"NOT_FOUND"}}]""";
        Assert.Equal(AiFailureKind.ModelNotFound, AiProtocol.Classify(404, gemini, null).Kind);
    }

    [Fact]
    public void ErrorMessage_ReadsTheProvidersMessage()
    {
        Assert.Equal("models/x is not found", AiProtocol.ErrorMessage("""[{"error":{"code":404,"message":"models/x is not found"}}]"""));
        Assert.Equal("Invalid API Key", AiProtocol.ErrorMessage("""{"error":{"message":"Invalid API Key"}}"""));
        Assert.Equal("not json", AiProtocol.ErrorMessage("not json"));
        Assert.Equal(200, AiProtocol.ErrorMessage(new string('a', 500)).Length);
    }

    [Fact]
    public void ChatModels_KeepsChatModelsSuggestionsFirst()
    {
        const string gemini = """
            {"object":"list","data":[
              {"id":"models/gemini-3.8-flash"},{"id":"models/gemini-embedding-2"},{"id":"models/gemini-3.8-flash-tts"},
              {"id":"models/gemini-3.6-flash"},{"id":"models/gemini-3.1-flash-image"},{"id":"models/gemini-3.8-live"},
              {"id":"models/gemini-3.5-flash-lite"},{"id":"models/veo-3.1-generate-preview"},{"id":"models/gemini-3.1-pro-preview"}]}
            """;
        var models = AiProtocol.ChatModels(gemini, ["gemini-3.6-flash", "gemini-9-gone"], freeOnly: false);
        Assert.Equal(["gemini-3.6-flash", "gemini-3.1-pro-preview", "gemini-3.5-flash-lite", "gemini-3.8-flash"], models);
    }

    [Fact]
    public void ChatModels_FreeOnlyKeepsFreeModels()
    {
        const string openRouter = """{"data":[{"id":"google/gemma-4-31b-it:free"},{"id":"google/gemma-4-31b-it"},{"id":"openai/whisper-1:free"}]}""";
        Assert.Equal(["google/gemma-4-31b-it:free"], AiProtocol.ChatModels(openRouter, [], freeOnly: true));
        Assert.Empty(AiProtocol.ChatModels("garbage", [], freeOnly: false));
    }
}
