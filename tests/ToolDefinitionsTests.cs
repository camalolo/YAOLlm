using System.Linq;
using Xunit;

namespace YAOLlm.Tests;

public class ToolDefinitionsTests
{
    [Fact]
    public void BuildTools_TtsOffAndNoWebSearch_SendsOnlyFileRead()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: false);

        // file_read is client-executed — Ollama-style providers without a web
        // tool loop still can't process its results, but the definition itself
        // is independent of web search support; gating happens via FILE_READ.
        Assert.Equal(new[] { "file_read" }, tools.Select(t => t.Name));
    }

    [Fact]
    public void BuildTools_FileReadDisabled_RemovesFileToolOnly()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: true, fileReadEnabled: false);

        Assert.Equal(new[] { "web_search", "web_scrape", "tts_summary" }, tools.Select(t => t.Name));
    }

    [Fact]
    public void BuildTools_TtsOnWithoutWebSearch_AdvertisesTtsAndFileRead()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: true);

        Assert.Equal(new[] { "file_read", "tts_summary" }, tools.Select(t => t.Name));
    }

    [Fact]
    public void BuildTools_WebSearchProvider_IncludesWebToolsAndTtsConditionally()
    {
        var withTts = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: true)
            .Select(t => t.Name).ToList();
        Assert.Equal(new[] { "web_search", "web_scrape", "file_read", "tts_summary" }, withTts);

        var withoutTts = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: false)
            .Select(t => t.Name).ToList();
        Assert.Equal(new[] { "web_search", "web_scrape", "file_read" }, withoutTts);
    }

    [Fact]
    public void FileRead_Definition_RequiresOnlyPath()
    {
        Assert.Equal("file_read", ToolDefinitions.FileRead.Name);
        Assert.Contains("read-only", ToolDefinitions.FileRead.Description);
    }
}
