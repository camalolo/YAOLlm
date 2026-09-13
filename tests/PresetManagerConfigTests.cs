using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;
using YAOLlm.Providers;

namespace YAOLlm.Tests;

/// <summary>
/// Verifies that LLM endpoints are fully config-driven: PRESET_N_BASE_URL /
/// PRESET_N_API_KEY (with legacy per-vendor env fallbacks) and that no
/// hardcoded default URLs remain behind provider creation.
/// </summary>
public class PresetManagerConfigTests : IDisposable
{
    private readonly List<string> _setKeys = new();

    private PresetManager CreateManager()
        => new(null!, null!, new Logger());

    private void SetEnv(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        _setKeys.Add(key);
    }

    public void Dispose()
    {
        foreach (var key in _setKeys)
            Environment.SetEnvironmentVariable(key, null);
    }

    private static string GetStreamUrl(ILLMProvider provider)
    {
        var prop = provider.GetType().GetProperty("StreamUrl",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(prop);
        return (string)prop!.GetValue(provider)!;
    }

    [Fact]
    public void CreateProvider_UsesPresetBaseUrlAndKey()
    {
        SetEnv("PRESET_1", "openai:glm-5.3-flash:Local Proxy");
        SetEnv("PRESET_1_BASE_URL", "http://127.0.0.1:3003/api/v1");
        SetEnv("PRESET_1_API_KEY", "ik-test-123");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        using var provider = manager.CreateProvider();
        Assert.IsType<OpenAICompatibleProvider>(provider);
        Assert.Equal("openai-compatible", provider.Name);
        Assert.Equal("http://127.0.0.1:3003/api/v1/chat/completions", GetStreamUrl(provider));
        Assert.Equal("glm-5.3-flash", provider.Model);
    }

    [Fact]
    public void CreateProvider_GeminiBuildsUrlFromConfig()
    {
        SetEnv("PRESET_1", "gemini:gemini-2.5-flash");
        SetEnv("PRESET_1_BASE_URL", "https://example.invalid/v1beta");
        SetEnv("PRESET_1_API_KEY", "g-key");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        using var provider = manager.CreateProvider();
        Assert.Equal("https://example.invalid/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse",
            GetStreamUrl(provider));
    }

    [Fact]
    public void CreateProvider_MissingBaseUrl_Throws()
    {
        SetEnv("PRESET_1", "openai:m1");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        var ex = Assert.Throws<InvalidOperationException>(() => manager.CreateProvider());
        Assert.Contains("PRESET_1_BASE_URL", ex.Message);
    }

    [Fact]
    public void CreateProvider_LegacyBaseUrlEnvVar_StillSupported()
    {
        SetEnv("PRESET_1", "openai-compatible:m1");
        SetEnv("OPENAI_COMPATIBLE_BASE_URL", "http://localhost:11434");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        using var provider = manager.CreateProvider();
        Assert.Equal("http://localhost:11434/chat/completions", GetStreamUrl(provider));
    }

    [Fact]
    public void CreateProvider_LegacyVendorKeyEnvVar_StillSupported()
    {
        SetEnv("PRESET_1", "zai:glm-5-turbo");
        SetEnv("PRESET_1_BASE_URL", "https://z.example.invalid/v4");
        SetEnv("ZAI_API_KEY", "z-key");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        // The zai profile is served by the shared OpenAI-style provider now —
        // only the profile name (logs/errors) distinguishes it.
        using var provider = manager.CreateProvider();
        Assert.IsType<OpenAICompatibleProvider>(provider);
        Assert.Equal("zai", provider.Name);
        Assert.Equal("https://z.example.invalid/v4/chat/completions", GetStreamUrl(provider));
    }

    [Fact]
    public void CreateProvider_OllamaLegacyFallback()
    {
        SetEnv("PRESET_1", "ollama:llama3");
        SetEnv("OLLAMA_BASE_URL", "http://localhost:11434");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        using var provider = manager.CreateProvider();
        Assert.Equal("http://localhost:11434/api/chat", GetStreamUrl(provider));
    }

    [Fact]
    public void LoadConfig_OrphanBaseUrl_IsIgnored()
    {
        SetEnv("PRESET_1", "openai:m1");
        SetEnv("PRESET_1_BASE_URL", "http://127.0.0.1:3003/api/v1");
        SetEnv("PRESET_7_BASE_URL", "http://orphan.invalid");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        // Exactly one preset survived; the orphan key produced no phantom preset
        // (if it had, ACTIVE_PRESET=1 would clamp to the garbage entry instead).
        Assert.True(manager.HasProvider);
        Assert.Equal("openai", manager.ActivePreset.Provider);
        Assert.Equal("http://127.0.0.1:3003/api/v1", manager.ActivePreset.BaseUrl);
    }

    [Fact]
    public void LoadConfig_TrailingBaseUrlsDoNotBecomePresets()
    {
        // Regression: the old scanner treated any PRESET_* env var as a preset
        // line, so PRESET_1_BASE_URL=... would be mis-parsed as one.
        SetEnv("PRESET_1", "openai:m1");
        SetEnv("PRESET_1_BASE_URL", "http://127.0.0.1:3003/api/v1");
        SetEnv("PRESET_1_API_KEY", "k");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        Assert.Equal("openai", manager.ActivePreset.Provider);
        Assert.Equal("m1", manager.ActivePreset.Model);
        Assert.Equal("http://127.0.0.1:3003/api/v1", manager.ActivePreset.BaseUrl);
        Assert.Equal("k", manager.ActivePreset.ApiKey);
    }

    [Fact]
    public void LoadConfig_TokenOptions_PerPresetOverrideGlobalFallback()
    {
        SetEnv("PRESET_1", "openai:m1");
        SetEnv("PRESET_1_BASE_URL", "http://127.0.0.1:3003/api/v1");
        SetEnv("PRESET_2", "openai:m2");
        SetEnv("PRESET_2_BASE_URL", "http://127.0.0.1:3003/api/v1");
        SetEnv("MAX_TOKENS", "1024");
        SetEnv("REASONING", "low");
        SetEnv("PRESET_2_MAX_TOKENS", "256");
        SetEnv("PRESET_2_REASONING", "off");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        // Preset 1: globals only
        Assert.Equal(1024, manager.ActivePreset.MaxTokens);
        Assert.Equal(ReasoningMode.Low, manager.ActivePreset.Reasoning);
        // Preset 2: per-preset values win
        manager.CycleNext();
        Assert.Equal(256, manager.ActivePreset.MaxTokens);
        Assert.Equal(ReasoningMode.Off, manager.ActivePreset.Reasoning);
    }

    [Fact]
    public void LoadConfig_InvalidTokenOptions_AreIgnored()
    {
        SetEnv("PRESET_1", "openai:m1");
        SetEnv("PRESET_1_BASE_URL", "http://127.0.0.1:3003/api/v1");
        SetEnv("PRESET_1_MAX_TOKENS", "not-a-number");
        SetEnv("PRESET_1_REASONING", "turbo");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        Assert.Null(manager.ActivePreset.MaxTokens);
        Assert.Equal(ReasoningMode.Default, manager.ActivePreset.Reasoning);
    }

    [Fact]
    public void CreateProvider_AppliesTokenOptionsToProvider()
    {
        SetEnv("PRESET_1", "deepseek:deepseek-chat");
        SetEnv("PRESET_1_BASE_URL", "https://api.deepseek.example/v1");
        SetEnv("PRESET_1_API_KEY", "sk-test");
        SetEnv("PRESET_1_MAX_TOKENS", "2048");
        SetEnv("PRESET_1_REASONING", "off");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        using var provider = manager.CreateProvider();
        var baseProvider = Assert.IsAssignableFrom<BaseLLMProvider>(provider);
        Assert.Equal(2048, baseProvider.MaxTokens);
        Assert.Equal(ReasoningMode.Off, baseProvider.Reasoning);
    }

    [Fact]
    public void CreateProvider_NoTokenOptions_DefaultsApplied()
    {
        SetEnv("PRESET_1", "openai:m1");
        SetEnv("PRESET_1_BASE_URL", "http://127.0.0.1:3003/api/v1");
        SetEnv("ACTIVE_PRESET", "1");

        using var manager = CreateManager();
        manager.LoadConfig();

        using var provider = manager.CreateProvider();
        var baseProvider = Assert.IsAssignableFrom<BaseLLMProvider>(provider);
        Assert.Null(baseProvider.MaxTokens);
        Assert.Equal(ReasoningMode.Default, baseProvider.Reasoning);
    }
}
