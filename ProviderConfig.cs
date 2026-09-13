namespace YAOLlm;

/// <summary>
/// Reasoning-effort preference for a preset. Off/Low map to per-provider
/// request parameters (thinking toggles, reasoning_effort, thinking budgets);
/// Default sends nothing and keeps the API's own behavior.
/// </summary>
public enum ReasoningMode
{
    /// <summary>Send no reasoning-related parameter (API default behavior).</summary>
    Default,

    /// <summary>Disable thinking/reasoning entirely where the API allows it.</summary>
    Off,

    /// <summary>Request the lowest portable reasoning effort.</summary>
    Low,
}

public static class ReasoningModeExtensions
{
    /// <summary>Parses PRESET_N_REASONING / REASONING values (case-insensitive).</summary>
    public static ReasoningMode? ParseReasoning(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "off" or "disabled" or "false" or "none" or "no" or "0" => ReasoningMode.Off,
            "low" or "minimal" or "min" => ReasoningMode.Low,
            _ => null,
        };
    }

    public static string ToConfigValue(this ReasoningMode mode) => mode switch
    {
        ReasoningMode.Off => "off",
        ReasoningMode.Low => "low",
        _ => "",
    };
}

public class ProviderConfig
{
    public string Provider { get; set; }
    public string Model { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// API endpoint root taken from PRESET_N_BASE_URL (no hardcoded URLs in code).
    /// Providers append their protocol-specific resource path to this.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// API key taken from PRESET_N_API_KEY. Null when the key comes from a
    /// legacy per-vendor env var (GEMINI_API_KEY etc.) — only explicit
    /// per-preset keys are written back by SaveConfig.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Completion-token cap from PRESET_N_MAX_TOKENS (fallback: MAX_TOKENS).
    /// Sent as max_tokens / maxOutputTokens / num_predict depending on the
    /// provider protocol. Null = API default.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Reasoning-effort preference from PRESET_N_REASONING (fallback: REASONING).
    /// Default = send nothing; Off = disable thinking where the API supports it
    /// (DeepSeek/GLM thinking toggle, OpenRouter reasoning.enabled=false, Gemini
    /// thinkingBudget=0, Ollama think:false); Low = lowest portable effort.
    /// </summary>
    public ReasoningMode Reasoning { get; set; } = ReasoningMode.Default;

    /// <summary>Original PRESET_N number this config was loaded from (0 if not loaded from config).</summary>
    public int SourceIndex { get; set; }

    /// <summary>Parses PRESET_N_MAX_TOKENS / MAX_TOKENS. Invalid or non-positive values return null.</summary>
    public static int? ParseMaxTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value.Trim(), out var parsed)) return null;
        return parsed > 0 ? parsed : null;
    }

    public ProviderConfig(string provider, string model, string? displayName = null)
    {
        Provider = provider;
        Model = model;
        DisplayName = displayName;
    }

    public override string ToString()
    {
        if (!string.IsNullOrEmpty(DisplayName))
            return $"{Provider}:{Model}:{DisplayName}";
        return $"{Provider}:{Model}";
    }

    public static ProviderConfig? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split(':');
        if (parts.Length < 2)
            return null;

        var provider = parts[0].Trim();

        string model;
        string? displayName = null;

        if (parts.Length >= 3)
        {
            displayName = parts[^1].Trim();
            model = string.Join(':', parts[1..^1]).Trim();
        }
        else
        {
            model = parts[1].Trim();
        }

        return new ProviderConfig(provider, model, displayName);
    }
}
