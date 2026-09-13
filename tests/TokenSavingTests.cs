using System;
using System.Collections.Generic;
using System.Linq;
using YAOLlm.Providers;
using Xunit;

namespace YAOLlm.Tests;

/// <summary>
/// Exposes protected body builders of the concrete provider classes.
/// </summary>
internal class TestOpenAICompatibleProvider : OpenAICompatibleProvider
{
    public TestOpenAICompatibleProvider(string profileName) : base("test-model", "http://localhost", null, name: profileName) { }
    public Dictionary<string, object> BuildBody() => BuildStreamingRequestBody(new List<object>(), null);
}

internal class TestOpenRouterBodyProvider : OpenRouterProvider
{
    public TestOpenRouterBodyProvider() : base("test-model", "key", "http://localhost") { }
    public Dictionary<string, object> BuildBody() => BuildStreamingRequestBody(new List<object>(), null);
}

/// <summary>
/// Verifies the token-saving configuration (PRESET_N_MAX_TOKENS /
/// PRESET_N_REASONING, fallback MAX_TOKENS / REASONING) maps to the correct
/// per-protocol request parameters.
/// </summary>
public class TokenSavingTests
{
    // ─── Config parsing ────────────────────────────────────────────────

    [Theory]
    [InlineData("4096", 4096)]
    [InlineData(" 2048 ", 2048)]
    public void ParseMaxTokens_ValidValues(string value, int expected)
    {
        Assert.Equal(expected, ProviderConfig.ParseMaxTokens(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    public void ParseMaxTokens_InvalidValues_ReturnNull(string? value)
    {
        Assert.Null(ProviderConfig.ParseMaxTokens(value));
    }

    [Theory]
    [InlineData("off", ReasoningMode.Off)]
    [InlineData("OFF", ReasoningMode.Off)]
    [InlineData("Disabled", ReasoningMode.Off)]
    [InlineData("none", ReasoningMode.Off)]
    [InlineData("0", ReasoningMode.Off)]
    [InlineData("low", ReasoningMode.Low)]
    [InlineData("MINIMAL", ReasoningMode.Low)]
    [InlineData("min", ReasoningMode.Low)]
    public void ParseReasoning_ValidValues(string value, ReasoningMode expected)
    {
        Assert.Equal(expected, ReasoningModeExtensions.ParseReasoning(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("medium")]
    [InlineData("turbo")]
    public void ParseReasoning_UnknownValues_ReturnNull(string? value)
    {
        Assert.Null(ReasoningModeExtensions.ParseReasoning(value));
    }

    [Fact]
    public void ToConfigValue_RoundTrips()
    {
        Assert.Equal("off", ReasoningMode.Off.ToConfigValue());
        Assert.Equal("low", ReasoningMode.Low.ToConfigValue());
        Assert.Equal("", ReasoningMode.Default.ToConfigValue());
        Assert.Equal(ReasoningMode.Off, ReasoningModeExtensions.ParseReasoning(ReasoningMode.Off.ToConfigValue()));
        Assert.Equal(ReasoningMode.Low, ReasoningModeExtensions.ParseReasoning(ReasoningMode.Low.ToConfigValue()));
    }

    // ─── OpenAI-compatible (generic profile) ───────────────────────────

    [Fact]
    public void OpenAICompatible_Default_SendsNoExtraParams()
    {
        var provider = new TestOpenAICompatibleProvider("openai-compatible");
        var body = provider.BuildBody();

        Assert.False(body.ContainsKey("reasoning_effort"));
        Assert.False(body.ContainsKey("thinking"));
        Assert.False(body.ContainsKey("max_tokens"));
    }

    [Fact]
    public void OpenAICompatible_ReasoningOff_UsesPortableLowEffort()
    {
        var provider = new TestOpenAICompatibleProvider("openai-compatible")
        {
            Reasoning = ReasoningMode.Off,
            MaxTokens = 2048,
        };
        var body = provider.BuildBody();

        Assert.Equal("low", body["reasoning_effort"]);
        Assert.Equal(2048, body["max_tokens"]);
        Assert.False(body.ContainsKey("thinking"));
    }

    [Fact]
    public void OpenAICompatible_ReasoningLow_UsesLowEffort()
    {
        var provider = new TestOpenAICompatibleProvider("openai-compatible") { Reasoning = ReasoningMode.Low };

        Assert.Equal("low", provider.BuildBody()["reasoning_effort"]);
    }

    // ─── DeepSeek profile: thinking toggle ─────────────────────────────

    [Fact]
    public void DeepSeek_ReasoningOff_DisablesThinking()
    {
        var provider = new TestOpenAICompatibleProvider("deepseek") { Reasoning = ReasoningMode.Off };
        var body = provider.BuildBody();

        var thinking = Assert.IsType<Dictionary<string, object>>(body["thinking"]);
        Assert.Equal("disabled", thinking["type"]);
        Assert.False(body.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void DeepSeek_ReasoningLow_EnablesThinkingWithLowEffort()
    {
        var provider = new TestOpenAICompatibleProvider("deepseek") { Reasoning = ReasoningMode.Low };
        var body = provider.BuildBody();

        var thinking = Assert.IsType<Dictionary<string, object>>(body["thinking"]);
        Assert.Equal("enabled", thinking["type"]);
        Assert.Equal("low", body["reasoning_effort"]);
    }

    [Fact]
    public void DeepSeek_Default_SendsNoThinking()
    {
        var provider = new TestOpenAICompatibleProvider("deepseek");

        Assert.False(provider.BuildBody().ContainsKey("thinking"));
    }

    // ─── Z.ai GLM profile: thinking toggle, no effort knob ─────────────

    [Fact]
    public void Zai_ReasoningOff_DisablesThinking()
    {
        var provider = new TestOpenAICompatibleProvider("zai") { Reasoning = ReasoningMode.Off };
        var thinking = Assert.IsType<Dictionary<string, object>>(provider.BuildBody()["thinking"]);

        Assert.Equal("disabled", thinking["type"]);
    }

    [Fact]
    public void Zai_ReasoningLow_EnablesThinkingWithoutEffort()
    {
        var provider = new TestOpenAICompatibleProvider("zai") { Reasoning = ReasoningMode.Low };
        var body = provider.BuildBody();
        var thinking = Assert.IsType<Dictionary<string, object>>(body["thinking"]);

        Assert.Equal("enabled", thinking["type"]);
        // GLM has no reasoning_effort parameter — must not be sent
        Assert.False(body.ContainsKey("reasoning_effort"));
    }

    // ─── OpenRouter: unified reasoning object ──────────────────────────

    [Fact]
    public void OpenRouter_ReasoningOff_SendsEnabledFalse()
    {
        var provider = new TestOpenRouterBodyProvider { Reasoning = ReasoningMode.Off };
        var reasoning = Assert.IsType<Dictionary<string, object>>(provider.BuildBody()["reasoning"]);

        Assert.Equal(false, reasoning["enabled"]);
    }

    [Fact]
    public void OpenRouter_ReasoningLow_SendsLowEffort()
    {
        var provider = new TestOpenRouterBodyProvider { Reasoning = ReasoningMode.Low, MaxTokens = 1024 };
        var body = provider.BuildBody();
        var reasoning = Assert.IsType<Dictionary<string, object>>(body["reasoning"]);

        Assert.Equal("low", reasoning["effort"]);
        Assert.Equal(1024, body["max_tokens"]);
    }

    [Fact]
    public void OpenRouter_Default_SendsNoReasoning()
    {
        var provider = new TestOpenRouterBodyProvider();

        Assert.False(provider.BuildBody().ContainsKey("reasoning"));
    }

    // ─── Gemini: generationConfig ──────────────────────────────────────

    [Fact]
    public void Gemini_ReasoningOff_ZeroThinkingBudget()
    {
        var provider = CreateGeminiProvider();
        provider.Reasoning = ReasoningMode.Off;
        var config = provider.BuildGenerationConfig();
        var thinking = Assert.IsType<Dictionary<string, object>>(config["thinkingConfig"]);

        Assert.Equal(0, thinking["thinkingBudget"]);
    }

    [Fact]
    public void Gemini_ReasoningLow_SmallThinkingBudget()
    {
        var provider = CreateGeminiProvider();
        provider.Reasoning = ReasoningMode.Low;
        var config = provider.BuildGenerationConfig();
        var thinking = Assert.IsType<Dictionary<string, object>>(config["thinkingConfig"]);

        Assert.Equal(1024, thinking["thinkingBudget"]);
    }

    [Fact]
    public void Gemini_MaxTokens_MapsToMaxOutputTokens()
    {
        var provider = CreateGeminiProvider();
        provider.MaxTokens = 4096;
        var config = provider.BuildGenerationConfig();

        Assert.Equal(4096, config["maxOutputTokens"]);
    }

    [Fact]
    public void Gemini_Default_EmptyGenerationConfig()
    {
        var provider = CreateGeminiProvider();

        Assert.Empty(provider.BuildGenerationConfig());
    }

    private static GeminiProvider CreateGeminiProvider()
        => new("gemini-test", "key", "http://localhost");

    // ─── Ollama: think toggle + num_predict ────────────────────────────

    private static OllamaProvider CreateOllamaProvider()
        => new("test-model", "http://localhost");

    [Fact]
    public void Ollama_ReasoningOff_SetsThinkFalse()
    {
        var provider = CreateOllamaProvider();
        provider.Reasoning = ReasoningMode.Off;
        var body = provider.BuildRequestBody(new List<object>(), null);

        Assert.Equal(false, body["think"]);
        Assert.False(body.ContainsKey("options"));
    }

    [Fact]
    public void Ollama_ReasoningLow_AlsoSetsThinkFalse()
    {
        var provider = CreateOllamaProvider();
        provider.Reasoning = ReasoningMode.Low;

        Assert.Equal(false, provider.BuildRequestBody(new List<object>(), null)["think"]);
    }

    [Fact]
    public void Ollama_MaxTokens_MapsToNumPredict()
    {
        var provider = CreateOllamaProvider();
        provider.MaxTokens = 512;
        var body = provider.BuildRequestBody(new List<object>(), null);
        var options = Assert.IsType<Dictionary<string, object>>(body["options"]);

        Assert.Equal(512, options["num_predict"]);
    }

    [Fact]
    public void Ollama_Default_SendsNoThinkOrOptions()
    {
        var provider = CreateOllamaProvider();
        var body = provider.BuildRequestBody(new List<object>(), null);

        Assert.False(body.ContainsKey("think"));
        Assert.False(body.ContainsKey("options"));
    }
}
