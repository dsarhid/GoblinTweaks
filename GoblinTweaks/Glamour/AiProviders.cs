namespace GoblinTweaks.Glamour;

/// <summary>
/// A provider with an OpenAI-compatible chat completions endpoint and a free tier. <c>FreeModels</c> are
/// suggestions (the user can type any model); the first one is the default. <c>Custom</c> = the user enters the URL.
/// <c>NeedsKey</c> = the user must enter their own key. <c>Vision</c> = its models read the Try On picture.
/// <c>ReasoningEffort</c> = sent with each request when set. <c>FreeOnly</c> = of its model list, only the ":free" ones.
/// </summary>
public sealed record AiProvider(
    string Key, string BaseUrl, string[] FreeModels, bool Custom, bool NeedsKey = true, bool Vision = true,
    string? ReasoningEffort = null, bool FreeOnly = false);

public static class AiProviders
{
    public static readonly IReadOnlyList<AiProvider> All =
    [
        // Free for everyone without a key (anonymous tier: one reply every 45 seconds, text only). "openai-fast" is
        // its only anonymous model (gpt-oss-20b; "openai" is an alias). It thinks until it runs out of room unless
        // asked to think less.
        new("pollinations", "https://text.pollinations.ai/openai", ["openai-fast"], Custom: false, NeedsKey: false, Vision: false,
            ReasoningEffort: "low"),
        // Providers retire models every few months: these are only the first suggestions. With a key, the settings
        // ask the provider for the models it has now. Checked 2026-11-02 against each provider's model and
        // deprecation pages: Groq's llama-3.3-70b-versatile shut down 2026-08-16; qwen3.8-27b is a preview.
        new("gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
            ["gemini-3.6-flash", "gemini-3.8-flash", "gemini-3.5-flash-lite"], Custom: false),
        new("groq", "https://api.groq.com/openai/v1/chat/completions",
            ["openai/gpt-oss-120b", "openai/gpt-oss-20b", "qwen/qwen3.8-27b"], Custom: false, Vision: false),
        new("openrouter", "https://openrouter.ai/api/v1/chat/completions",
            ["google/gemma-4-31b-it:free", "google/gemma-4-26b-a4b-it:free", "nvidia/nemotron-3.5-super-vl:free"], Custom: false,
            FreeOnly: true),
        new("custom", "", [], Custom: true),
    ];

    /// <summary>The provider with this key, or the first one (free, no key).</summary>
    public static AiProvider Get(string key) => All.FirstOrDefault(p => p.Key == key) ?? All[0];
}
