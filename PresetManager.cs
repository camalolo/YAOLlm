using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dotenv.net;
using dotenv.net.Utilities;
using YAOLlm.Providers;

namespace YAOLlm;

public class PresetManager
{
    private readonly string _configPath;
    private readonly TavilySearchService _searchService;
    private readonly Logger _logger;
    private List<ProviderConfig> _presets;
    private int _activeIndex;

    public bool HasProvider => _presets.Count > 0;

    public IReadOnlyList<ProviderConfig> Presets => _presets;
    public int ActiveIndex => _activeIndex;
    public ProviderConfig ActivePreset => _presets[_activeIndex];

    public event Action<ProviderConfig>? PresetChanged;

    public PresetManager(TavilySearchService searchService, Logger? logger = null)
    {
        _searchService = searchService;
        _logger = logger ?? new Logger();
        _presets = new List<ProviderConfig>();
        _activeIndex = 0;

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _configPath = Path.Combine(homeDir, ".yaollm.conf");
    }

    public void LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            _logger.Log("Config file not found, no presets configured");
            _presets = new List<ProviderConfig>();
            _activeIndex = 0;
            return;
        }

        try
        {
            var envVars = DotEnv.Read(options: new DotEnvOptions(envFilePaths: new[] { _configPath }, probeForEnv: false, probeLevelsToSearch: 0));
            _presets = new List<ProviderConfig>();

            var presetEntries = envVars
                .Where(kv => kv.Key.StartsWith("PRESET_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv =>
                {
                    var numPart = kv.Key.Substring(7);
                    return int.TryParse(numPart, out var num) ? num : int.MaxValue;
                })
                .ToList();

            foreach (var entry in presetEntries)
            {
                var config = ProviderConfig.Parse(entry.Value);
                if (config != null)
                {
                    _presets.Add(config);
                }
            }

            if (_presets.Count == 0)
            {
                _logger.Log("No valid presets found, no provider configured");
                _presets = new List<ProviderConfig>();
            }

            if (envVars.TryGetValue("ACTIVE_PRESET", out var activeStr) && int.TryParse(activeStr, out var activeNum))
            {
                _activeIndex = Math.Clamp(activeNum - 1, 0, _presets.Count - 1);
            }
            else
            {
                _activeIndex = 0;
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
            var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                        existingKeys.Add(key);

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
        return new GeminiProvider(model, apiKey, httpClient: null, _searchService, _logger);
    }

    private ILLMProvider CreateOpenRouterProvider(string model)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("OPENROUTER_API_KEY not set");
        }
        return new OpenRouterProvider(model, apiKey, _searchService, _logger);
    }

    private ILLMProvider CreateOllamaProvider(string model)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        return new OllamaProvider(model, baseUrl, httpClient: null, _logger);
    }

    private ILLMProvider CreateOpenAICompatibleProvider(string model)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_COMPATIBLE_BASE_URL") ?? "http://localhost:11434";
        return new OpenAICompatibleProvider(model, baseUrl, httpClient: null, _searchService, _logger);
    }
}
