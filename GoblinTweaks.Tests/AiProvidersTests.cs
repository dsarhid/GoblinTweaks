namespace GoblinTweaks.Tests;

public class AiProvidersTests
{
    [Fact]
    public void Default_IsFreeWithoutKey_AndTextOnly()
    {
        var provider = AiProviders.Get("no such provider");
        Assert.Equal("pollinations", provider.Key);
        Assert.False(provider.NeedsKey);
        Assert.False(provider.Vision);
        Assert.StartsWith("https://", provider.BaseUrl);
        Assert.NotEmpty(provider.FreeModels);
    }

    [Fact]
    public void KeyedProviders_NeedAKey_AndSeeImages()
    {
        foreach (var key in new[] { "gemini", "groq", "openrouter" })
            Assert.True(AiProviders.Get(key).NeedsKey, key);
        Assert.True(AiProviders.Get("gemini").Vision);
        Assert.True(AiProviders.Get("openrouter").Vision);
        Assert.False(AiProviders.Get("groq").Vision);   // its models in 2026-11 read text only
        Assert.True(AiProviders.Get("openrouter").FreeOnly);
    }
}
