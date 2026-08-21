using Xunit;

namespace YAOLlm.Tests;

public class ProviderConfigTests
{
    [Fact]
    public void Parse_SimpleProviderModel()
    {
        var config = ProviderConfig.Parse("gemini:gemini-2.0-flash");

        Assert.NotNull(config);
        Assert.Equal("gemini", config.Provider);
        Assert.Equal("gemini-2.0-flash", config.Model);
        Assert.Null(config.DisplayName);
    }

    [Fact]
    public void Parse_WithDisplayName()
    {
        var config = ProviderConfig.Parse("openrouter:openrouter/auto:My Provider");

        Assert.NotNull(config);
        Assert.Equal("openrouter", config.Provider);
        Assert.Equal("openrouter/auto", config.Model);
        Assert.Equal("My Provider", config.DisplayName);
    }

    [Fact]
    public void Parse_ModelWithColons_LastSegmentIsDisplayName()
    {
        // Documented format is provider:model[:display_name] — when three or
        // more segments exist, the last one is always the display name.
        var config = ProviderConfig.Parse("ollama:llama3:8b");

        Assert.NotNull(config);
        Assert.Equal("ollama", config.Provider);
        Assert.Equal("llama3", config.Model);
        Assert.Equal("8b", config.DisplayName);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var config = ProviderConfig.Parse("  gemini : gemini-2.0-flash : Disp  ");

        Assert.NotNull(config);
        Assert.Equal("gemini", config.Provider);
        Assert.Equal("gemini-2.0-flash", config.Model);
        Assert.Equal("Disp", config.DisplayName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gemini")]
    public void Parse_Invalid_ReturnsNull(string? value)
    {
        Assert.Null(ProviderConfig.Parse(value!));
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        var config = new ProviderConfig("gemini", "gemini-2.0-flash", "Test");
        Assert.Equal("gemini:gemini-2.0-flash:Test", config.ToString());

        var plain = new ProviderConfig("ollama", "llama3");
        Assert.Equal("ollama:llama3", plain.ToString());
    }
}
