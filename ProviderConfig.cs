namespace GeminiDotnet;

public class ProviderConfig
{
    public string Provider { get; set; }
    public string Model { get; set; }
    public string? DisplayName { get; set; }

    public ProviderConfig(string provider, string model, string? displayName = null)
    {
        Provider = provider;
        Model = model;
        DisplayName = displayName;
    }

    public override string ToString() => DisplayName ?? $"{Provider}:{Model}";

    public static ProviderConfig? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split(':', 2);
        if (parts.Length != 2)
            return null;

        return new ProviderConfig(parts[0].Trim(), parts[1].Trim());
    }
}
