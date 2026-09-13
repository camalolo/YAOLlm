using System.Linq;
using Xunit;

namespace YAOLlm.Tests;

public class ToolDefinitionsTests
{
    [Fact]
    public void BuildTools_TtsOffAndNoWebSearch_SendsNoTools()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: false);

        Assert.Empty(tools);
    }

    [Fact]
    public void BuildTools_TtsOnWithoutWebSearch_AdvertisesOnlyTts()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: true);

        Assert.Equal(new[] { "tts_summary" }, tools.Select(t => t.Name));
    }

    [Fact]
    public void BuildTools_WebSearchProvider_IncludesWebToolsAndTtsConditionally()
    {
        var withTts = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: true)
            .Select(t => t.Name).ToList();
        Assert.Equal(new[] { "web_search", "web_fetch", "tts_summary" }, withTts);

        var withoutTts = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: false)
            .Select(t => t.Name).ToList();
        Assert.Equal(new[] { "web_search", "web_fetch" }, withoutTts);
    }
}
