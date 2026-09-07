namespace YAOLlm;

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

    /// <summary>Original PRESET_N number this config was loaded from (0 if not loaded from config).</summary>
    public int SourceIndex { get; set; }

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
