using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using YAOLlm.Providers;

namespace YAOLlm;

public class PresetManager : IDisposable
{
    private readonly string _configPath;
    private readonly ISearchService _searchService;
    private readonly IWebFetchService _webFetchService;
    private readonly Logger _logger;
    private readonly HttpClient _httpClient;
    private List<ProviderConfig> _presets;
    private int _activeIndex;

    public bool HasProvider => _presets.Count > 0;

    public ProviderConfig ActivePreset => _presets[_activeIndex];

    public event Action<ProviderConfig>? PresetChanged;

    public PresetManager(ISearchService searchService, IWebFetchService webFetchService, Logger? logger = null)
    {
        _searchService = searchService;
        _webFetchService = webFetchService;
        _logger = logger ?? new Logger();
        _presets = new List<ProviderConfig>();
        _activeIndex = 0;

        // Shared across all providers (auth is per-request, so it can be reused
        // safely when switching presets). Long timeout for streaming responses.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _configPath = Path.Combine(homeDir, ".yaollm.conf");
    }

    /// <summary>
    /// Loads presets from PRESET_* / ACTIVE_PRESET environment variables.
    /// Program.cs loads ~/.yaollm.conf into the process environment at startup
    /// (DotEnv.Load), so the config is parsed exactly once.
    /// </summary>
    public void LoadConfig()
    {
        _presets = new List<ProviderConfig>();
        _activeIndex = 0;

        try
        {
            var presetEntries = new List<(int order, string value)>();
            var baseUrls = new Dictionary<int, string>();
            var apiKeys = new Dictionary<int, string>();
            var maxTokens = new Dictionary<int, int?>();
            var reasoningModes = new Dictionary<int, ReasoningMode?>();

            // Global fallbacks: MAX_TOKENS / REASONING apply to every preset
            // that doesn't override them with PRESET_N_MAX_TOKENS / PRESET_N_REASONING.
            int? globalMaxTokens = ProviderConfig.ParseMaxTokens(Environment.GetEnvironmentVariable("MAX_TOKENS"));
            ReasoningMode? globalReasoning = ReasoningModeExtensions.ParseReasoning(Environment.GetEnvironmentVariable("REASONING"));

            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = entry.Key?.ToString();
                if (key == null || !key.StartsWith("PRESET_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var suffix = key["PRESET_".Length..];

                // Exact PRESET_<n> — the preset line itself.
                if (int.TryParse(suffix, out var num))
                {
                    presetEntries.Add((num, entry.Value?.ToString() ?? ""));
                    continue;
                }

                // PRESET_<n>_BASE_URL / _API_KEY / _MAX_TOKENS / _REASONING extras.
                const string baseUrlSuffix = "_BASE_URL";
                const string apiKeySuffix = "_API_KEY";
                const string maxTokensSuffix = "_MAX_TOKENS";
                const string reasoningSuffix = "_REASONING";
                if (suffix.EndsWith(baseUrlSuffix, StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(suffix[..^baseUrlSuffix.Length], out var urlNum))
                {
                    baseUrls[urlNum] = entry.Value?.ToString() ?? "";
                }
                else if (suffix.EndsWith(apiKeySuffix, StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(suffix[..^apiKeySuffix.Length], out var keyNum))
                {
                    apiKeys[keyNum] = entry.Value?.ToString() ?? "";
                }
                else if (suffix.EndsWith(maxTokensSuffix, StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(suffix[..^maxTokensSuffix.Length], out var maxNum))
                {
                    maxTokens[maxNum] = ProviderConfig.ParseMaxTokens(entry.Value?.ToString());
                }
                else if (suffix.EndsWith(reasoningSuffix, StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(suffix[..^reasoningSuffix.Length], out var reasoningNum))
                {
                    reasoningModes[reasoningNum] = ReasoningModeExtensions.ParseReasoning(entry.Value?.ToString());
                }
            }

            foreach (var entry in presetEntries.OrderBy(e => e.order))
            {
                var config = ProviderConfig.Parse(entry.value);
                if (config != null)
                {
                    config.SourceIndex = entry.order;
                    if (baseUrls.TryGetValue(entry.order, out var baseUrl))
                        config.BaseUrl = baseUrl;
                    if (apiKeys.TryGetValue(entry.order, out var apiKey))
                        config.ApiKey = apiKey;
                    config.MaxTokens = maxTokens.TryGetValue(entry.order, out var presetMax)
                        ? (presetMax ?? globalMaxTokens)
                        : globalMaxTokens;
                    config.Reasoning = reasoningModes.TryGetValue(entry.order, out var presetReasoning)
                        ? (presetReasoning ?? globalReasoning) ?? ReasoningMode.Default
                        : globalReasoning ?? ReasoningMode.Default;
                    _presets.Add(config);
                }
            }

            if (_presets.Count == 0)
            {
                _logger.Log("No valid presets found, no provider configured");
            }

            var activeStr = Environment.GetEnvironmentVariable("ACTIVE_PRESET");
            if (int.TryParse(activeStr, out var activeNum))
            {
                _activeIndex = _presets.Count > 0 ? Math.Clamp(activeNum - 1, 0, _presets.Count - 1) : 0;
            }

            _logger.Log($"Loaded {_presets.Count} presets, active: {_activeIndex + 1}");
        }
        catch (Exception ex)
        {
            _logger.Log($"Error loading config: {ex.Message}");
            _presets = new List<ProviderConfig>();
            _activeIndex = 0;
        }
    }

    public void SaveConfig()
    {
        try
        {
            var lines = new List<string>();

            if (File.Exists(_configPath))
            {
                var existingLines = File.ReadAllLines(_configPath);
                foreach (var line in existingLines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#"))
                    {
                        lines.Add(line);
                        continue;
                    }

                    var eqIndex = trimmed.IndexOf('=');
                    if (eqIndex > 0)
                    {
                    var key = trimmed.Substring(0, eqIndex).Trim();

                    if (IsPresetKey(key))
                    {
                        continue;
                    }
                    }
                    lines.Add(line);
                }
            }

            if (lines.Count > 0 && lines[lines.Count - 1].Trim() != "")
            {
                lines.Add("");
            }

            for (int i = 0; i < _presets.Count; i++)
            {
                var n = i + 1;
                lines.Add($"PRESET_{n}={_presets[i]}");
                if (!string.IsNullOrWhiteSpace(_presets[i].BaseUrl))
                    lines.Add($"PRESET_{n}_BASE_URL={_presets[i].BaseUrl}");
                if (!string.IsNullOrWhiteSpace(_presets[i].ApiKey))
                    lines.Add($"PRESET_{n}_API_KEY={_presets[i].ApiKey}");
                if (_presets[i].MaxTokens is int maxTokens)
                    lines.Add($"PRESET_{n}_MAX_TOKENS={maxTokens}");
                if (_presets[i].Reasoning != ReasoningMode.Default)
                    lines.Add($"PRESET_{n}_REASONING={_presets[i].Reasoning.ToConfigValue()}");
            }
            lines.Add($"ACTIVE_PRESET={_activeIndex + 1}");

            var directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(_configPath, lines);
            _logger.Log($"Saved config to {_configPath}");
        }
        catch (Exception ex)
        {
            _logger.Log($"Error saving config: {ex.Message}");
        }
    }

    public void CycleNext()
    {
        if (_presets.Count == 0) return;

        _activeIndex = (_activeIndex + 1) % _presets.Count;
        PresetChanged?.Invoke(ActivePreset);
        _logger.Log($"Switched to preset {_activeIndex + 1}: {ActivePreset}");
    }

    private static bool IsPresetKey(string key)
    {
        return key.StartsWith("PRESET_", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("ACTIVE_PRESET", StringComparison.OrdinalIgnoreCase);
    }

    public ILLMProvider CreateProvider()
    {
        var preset = ActivePreset;
        var providerName = preset.Provider.ToLowerInvariant();

        var provider = providerName switch
        {
            "gemini" => CreateGeminiProvider(preset),
            "openrouter" => CreateOpenRouterProvider(preset),
            "ollama" => CreateOllamaProvider(preset),

            // All remaining OpenAI-style profiles share one implementation;
            // only legacy env fallbacks and key requirements differ.
            "openai" or "openai-compatible"
                => CreateOpenAIStyleProvider(preset, "openai-compatible", legacyUrlEnv: "OPENAI_COMPATIBLE_BASE_URL"),
            "deepseek" => CreateOpenAIStyleProvider(preset, "deepseek", legacyKeyEnv: "DEEPSEEK_API_KEY", keyRequired: true),
            "zai" => CreateOpenAIStyleProvider(preset, "zai", legacyKeyEnv: "ZAI_API_KEY", keyRequired: true),
            _ => throw new NotSupportedException($"Unknown provider: {providerName}")
        };

        ApplyTokenOptions(provider, preset);
        return provider;
    }

    /// <summary>
    /// Pushes the preset's token-saving options (PRESET_N_MAX_TOKENS /
    /// PRESET_N_REASONING, with global fallbacks) onto the provider. All
    /// provider implementations derive from BaseLLMProvider, which carries
    /// both knobs and maps them to their protocol's parameters.
    /// </summary>
    private static void ApplyTokenOptions(ILLMProvider provider, ProviderConfig preset)
    {
        if (provider is not BaseLLMProvider baseProvider)
            return;
        baseProvider.MaxTokens = preset.MaxTokens;
        baseProvider.Reasoning = preset.Reasoning;
    }

    /// <summary>
    /// Resolves the endpoint root for a preset: PRESET_N_BASE_URL first, then a
    /// legacy per-provider env var (OLLAMA_BASE_URL / OPENAI_COMPATIBLE_BASE_URL).
    /// There are no hardcoded API endpoints in code — missing config throws.
    /// </summary>
    private string ResolveBaseUrl(ProviderConfig preset, string? legacyEnvVar)
    {
        var url = FirstNonEmpty(preset.BaseUrl,
            legacyEnvVar == null ? null : Environment.GetEnvironmentVariable(legacyEnvVar));

        if (string.IsNullOrWhiteSpace(url))
        {
            var hint = legacyEnvVar == null ? $"PRESET_{preset.SourceIndex}_BASE_URL"
                                            : $"PRESET_{preset.SourceIndex}_BASE_URL or {legacyEnvVar}";
            throw new InvalidOperationException(
                $"No base URL configured for preset {preset.SourceIndex} ({preset.Provider}). Set {hint}.");
        }

        return url.TrimEnd('/');
    }

    /// <summary>
    /// Resolves the API key for a preset: PRESET_N_API_KEY first, then the
    /// legacy per-vendor env vars in order.
    /// </summary>
    private string? ResolveApiKey(ProviderConfig preset, params string[] legacyEnvVars)
    {
        var candidates = new List<string?> { preset.ApiKey };
        candidates.AddRange(legacyEnvVars.Select(Environment.GetEnvironmentVariable));
        return FirstNonEmpty(candidates.ToArray());
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private ILLMProvider CreateGeminiProvider(ProviderConfig preset)
    {
        var baseUrl = ResolveBaseUrl(preset, legacyEnvVar: null);
        var apiKey = ResolveApiKey(preset, "GEMINI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for preset {preset.SourceIndex} (gemini). Set PRESET_{preset.SourceIndex}_API_KEY or GEMINI_API_KEY.");
        }
        return new GeminiProvider(preset.Model, apiKey, baseUrl, _httpClient, _searchService, _webFetchService, _logger);
    }

    private ILLMProvider CreateOpenRouterProvider(ProviderConfig preset)
    {
        var baseUrl = ResolveBaseUrl(preset, legacyEnvVar: null);
        var apiKey = ResolveApiKey(preset, "OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                $"No API key for preset {preset.SourceIndex} (openrouter). Set PRESET_{preset.SourceIndex}_API_KEY or OPENROUTER_API_KEY.");
        }
        return new OpenRouterProvider(preset.Model, apiKey, baseUrl, _httpClient, _searchService, _webFetchService, _logger);
    }

    private ILLMProvider CreateOllamaProvider(ProviderConfig preset)
    {
        var baseUrl = ResolveBaseUrl(preset, "OLLAMA_BASE_URL");
        return new OllamaProvider(preset.Model, baseUrl, _httpClient, _logger);
    }

    /// <summary>
    /// Creates the shared OpenAI-style provider for any non-gemini/non-ollama
    /// profile. The endpoint always comes from config; legacy per-vendor env
    /// vars are honored as fallbacks, and key requirements vary per profile.
    /// </summary>
    private ILLMProvider CreateOpenAIStyleProvider(ProviderConfig preset, string name,
        string? legacyUrlEnv = null, string? legacyKeyEnv = null, bool keyRequired = false)
    {
        var baseUrl = ResolveBaseUrl(preset, legacyUrlEnv);
        var apiKey = ResolveApiKey(preset, legacyKeyEnv ?? "");

        if (keyRequired && string.IsNullOrEmpty(apiKey))
        {
            var fallback = legacyKeyEnv == null ? "" : $" or {legacyKeyEnv}";
            throw new InvalidOperationException(
                $"No API key for preset {preset.SourceIndex} ({name}). Set PRESET_{preset.SourceIndex}_API_KEY{fallback}.");
        }

        return new OpenAICompatibleProvider(preset.Model, baseUrl, apiKey,
            _httpClient, _searchService, _webFetchService, _logger, name);
    }

    public void Dispose()
    {
        (_searchService as IDisposable)?.Dispose();
        _httpClient.Dispose();
    }
}
