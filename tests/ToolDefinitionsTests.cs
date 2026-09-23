using System.Linq;
using System.Text.Json;
using Xunit;

namespace YAOLlm.Tests;

public class ToolDefinitionsTests
{
    [Fact]
    public void WebSearch_OffersOptionalSpoilerFreeInfoParam()
    {
        var json = JsonSerializer.Serialize(ToolDefinitions.WebSearch.Parameters);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");

        Assert.True(props.TryGetProperty("info", out var info));
        Assert.Contains("spoiler", info.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        // info stays optional — only query is required
        Assert.Equal(new[] { "query" },
            doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void WebScrape_OffersOptionalSpoilerFreeInfoParam()
    {
        var json = JsonSerializer.Serialize(ToolDefinitions.WebScrape.Parameters);
        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");

        Assert.True(props.TryGetProperty("info", out var info));
        Assert.Contains("spoiler", info.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "url" },
            doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void BuildTools_TtsOffAndNoWebSearch_SendsOnlyFileTools()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: false);

        // file tools are client-executed — Ollama-style providers without a web
        // tool loop still can't process their results, but the definitions
        // themselves are independent of web search support; gating happens via
        // FILE_READ + a non-empty allowlist.
        Assert.Equal(new[] { "file_read", "list_files" }, tools.Select(t => t.Name));
    }

    [Fact]
    public void BuildTools_FileReadDisabled_RemovesFileToolsOnly()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: true, fileReadEnabled: false);

        Assert.Equal(new[] { "web_search", "web_scrape", "tts_summary" }, tools.Select(t => t.Name));
    }

    [Fact]
    public void BuildTools_TtsOnWithoutWebSearch_AdvertisesFileToolsAndTts()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: true);

        Assert.Equal(new[] { "file_read", "list_files", "tts_summary" }, tools.Select(t => t.Name));
    }

    [Fact]
    public void BuildTools_WebSearchProvider_IncludesWebToolsAndTtsConditionally()
    {
        var withTts = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: true)
            .Select(t => t.Name).ToList();
        Assert.Equal(new[] { "web_search", "web_scrape", "file_read", "list_files", "tts_summary" }, withTts);

        var withoutTts = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: false)
            .Select(t => t.Name).ToList();
        Assert.Equal(new[] { "web_search", "web_scrape", "file_read", "list_files" }, withoutTts);
    }

    [Fact]
    public void FileRead_Definition_RequiresOnlyPath()
    {
        Assert.Equal("file_read", ToolDefinitions.FileRead.Name);
        Assert.Contains("read-only", ToolDefinitions.FileRead.Description);
        Assert.Contains("approved", ToolDefinitions.FileRead.Description);
    }

    [Fact]
    public void FileList_Definition_RequiresOnlyPath()
    {
        Assert.Equal("list_files", ToolDefinitions.FileList.Name);
        Assert.Contains("approved", ToolDefinitions.FileList.Description);
    }

    [Fact]
    public void BuildTools_BrowseEnabled_AddsCuratedBrowseTools()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: false,
            fileReadEnabled: false, browseEnabled: true)
            .Select(t => t.Name).ToList();

        Assert.Equal(new[]
        {
            "browse_navigate", "browse_snapshot", "browse_find", "browse_click", "browse_type",
            "browse_press_key", "browse_tabs", "browse_wait_for", "browse_back", "browse_close",
            "browse_evaluate", "browse_network", "browse_network_request",
        }, tools);
    }

    [Fact]
    public void BuildTools_BrowseDisabled_OmitsBrowseTools()
    {
        var names = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: false, browseEnabled: false)
            .Select(t => t.Name).ToList();

        Assert.DoesNotContain(names, n => n.StartsWith("browse_"));
    }

    [Fact]
    public void BrowseToolDefinitions_NeverExposeUnsafeCode()
    {
        var names = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: false,
            fileReadEnabled: false, browseEnabled: true)
            .Select(t => t.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildTools_CaptionsEnabled_AddsYouTubeCaptions()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: false,
            fileReadEnabled: false, captionsEnabled: true)
            .Select(t => t.Name).ToList();

        Assert.Equal(new[] { "web_search", "web_scrape", "youtube_captions" }, tools);
        Assert.Equal("youtube_captions", ToolDefinitions.YouTubeCaptions.Name);
        Assert.Contains("transcript", ToolDefinitions.YouTubeCaptions.Description);
    }

    [Fact]
    public void BuildTools_CaptionsDisabled_OmitsYouTubeCaptions()
    {
        var names = ToolDefinitions.BuildTools(supportsWebSearch: true, ttsEnabled: false,
            fileReadEnabled: false, captionsEnabled: false)
            .Select(t => t.Name).ToList();

        Assert.DoesNotContain("youtube_captions", names);
    }

    [Fact]
    public void BuildTools_WriteEnabled_AddsFileWriteAndMemoryWrite()
    {
        var tools = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: false,
            fileReadEnabled: true, fileWriteEnabled: true, memoryEnabled: true)
            .Select(t => t.Name).ToList();

        // write tools sit right after the read tools
        Assert.Equal(
            new[] { "file_read", "list_files", "file_write", "memory_write" }, tools);
        Assert.Contains("never search", ToolDefinitions.MemoryWrite.Description);
        Assert.Contains("writable area", ToolDefinitions.FileWrite.Description);
    }

    [Fact]
    public void BuildTools_WriteDisabled_OmitsWriteTools()
    {
        var names = ToolDefinitions.BuildTools(supportsWebSearch: false, ttsEnabled: false,
            fileReadEnabled: true, fileWriteEnabled: false, memoryEnabled: false)
            .Select(t => t.Name).ToList();

        Assert.DoesNotContain("file_write", names);
        Assert.DoesNotContain("memory_write", names);
    }
}
