namespace YAOLlm;

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
