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

            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = entry.Key?.ToString();
                if (key == null || !key.StartsWith("PRESET_", StringComparison.OrdinalIgnoreCase))
                    continue;

                var numPart = key["PRESET_".Length..];
                presetEntries.Add((int.TryParse(numPart, out var num) ? num : int.MaxValue, entry.Value?.ToString() ?? ""));
            }

            foreach (var entry in presetEntries.OrderBy(e => e.order))
            {
                var config = ProviderConfig.Parse(entry.value);
                if (config != null)
                {
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

                        if (key.StartsWith("PRESET_", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("ACTIVE_PRESET", StringComparison.OrdinalIgnoreCase))
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
                lines.Add($"PRESET_{i + 1}={_presets[i]}");
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

    public ILLMProvider CreateProvider()
    {
        var preset = ActivePreset;
        var model = preset.Model;
        var providerName = preset.Provider.ToLowerInvariant();

        return providerName switch
        {
            "gemini" => CreateGeminiProvider(model),
            "openrouter" => CreateOpenRouterProvider(model),
            "ollama" => CreateOllamaProvider(model),
            "openai-compatible" => CreateOpenAICompatibleProvider(model),
            "deepseek" => CreateDeepSeekProvider(model),
            "zai" => CreateZaiProvider(model),
            _ => throw new NotSupportedException($"Unknown provider: {providerName}")
        };
    }

    private ILLMProvider CreateGeminiProvider(string model)
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("GEMINI_API_KEY not set");
        }
        return new GeminiProvider(model, apiKey, _httpClient, _searchService, _webFetchService, _logger);
    }

    private ILLMProvider CreateOpenRouterProvider(string model)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("OPENROUTER_API_KEY not set");
        }
        return new OpenRouterProvider(model, apiKey, _httpClient, _searchService, _webFetchService, _logger);
    }

    private ILLMProvider CreateOllamaProvider(string model)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        return new OllamaProvider(model, baseUrl, _httpClient, _logger);
    }

    private ILLMProvider CreateOpenAICompatibleProvider(string model)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_COMPATIBLE_BASE_URL") ?? "http://localhost:11434";
        return new OpenAICompatibleProvider(model, baseUrl, _httpClient, _searchService, _webFetchService, _logger);
    }

    private ILLMProvider CreateDeepSeekProvider(string model)
    {
        var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("DEEPSEEK_API_KEY not set");
        }
        return new DeepSeekProvider(model, apiKey, _httpClient, _searchService, _webFetchService, _logger);
    }

    private ILLMProvider CreateZaiProvider(string model)
    {
        var apiKey = Environment.GetEnvironmentVariable("ZAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("ZAI_API_KEY not set");
        }
        return new ZaiProvider(model, apiKey, _httpClient, _searchService, _webFetchService, _logger);
    }

    public void Dispose()
    {
        (_searchService as IDisposable)?.Dispose();
        _httpClient.Dispose();
    }
}
