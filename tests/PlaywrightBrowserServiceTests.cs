using System.Text.Json;
using Xunit;

namespace YAOLlm.Tests;

public class PlaywrightBrowserServiceTests
{
    // ─── Pure helpers ─────────────────────────────────────────────────

    [Fact]
    public void MapToolName_MapsAllCuratedBrowseTools()
    {
        Assert.Equal("browser_navigate", PlaywrightBrowserService.MapToolName("browse_navigate"));
        Assert.Equal("browser_snapshot", PlaywrightBrowserService.MapToolName("browse_snapshot"));
        Assert.Equal("browser_find", PlaywrightBrowserService.MapToolName("browse_find"));
        Assert.Equal("browser_click", PlaywrightBrowserService.MapToolName("browse_click"));
        Assert.Equal("browser_type", PlaywrightBrowserService.MapToolName("browse_type"));
        Assert.Equal("browser_press_key", PlaywrightBrowserService.MapToolName("browse_press_key"));
        Assert.Equal("browser_tabs", PlaywrightBrowserService.MapToolName("browse_tabs"));
        Assert.Equal("browser_wait_for", PlaywrightBrowserService.MapToolName("browse_wait_for"));
        Assert.Equal("browser_navigate_back", PlaywrightBrowserService.MapToolName("browse_back"));
        Assert.Equal("browser_close", PlaywrightBrowserService.MapToolName("browse_close"));
        Assert.Equal("browser_evaluate", PlaywrightBrowserService.MapToolName("browse_evaluate"));
        Assert.Equal("browser_network_requests", PlaywrightBrowserService.MapToolName("browse_network"));
        Assert.Equal("browser_network_request", PlaywrightBrowserService.MapToolName("browse_network_request"));
    }

    [Fact]
    public void MapToolName_UnknownOrMcpName_ReturnsNull()
    {
        Assert.Null(PlaywrightBrowserService.MapToolName("browse_run_code_unsafe"));
        Assert.Null(PlaywrightBrowserService.MapToolName("browser_click"));
        Assert.Null(PlaywrightBrowserService.MapToolName("file_read"));
    }

    [Fact]
    public void BuildRequest_ProducesJsonRpcLine()
    {
        var line = PlaywrightBrowserService.BuildRequest(7, "tools/call", new { name = "x" });

        using var doc = JsonDocument.Parse(line); // valid single-line JSON
        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.Equal("tools/call", root.GetProperty("method").GetString());
        Assert.Equal("x", root.GetProperty("params").GetProperty("name").GetString());
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void FormatToolResponse_TextContent_IsExtracted()
    {
        var response = JsonDocument.Parse("""
        {
          "result": {
            "content": [
              { "type": "text", "text": "line one" },
              { "type": "image", "data": "..." },
              { "type": "text", "text": "line two" }
            ]
          }
        }
        """).RootElement.Clone();

        Assert.Equal("line one\nline two", PlaywrightBrowserService.FormatToolResponse(response));
    }

    [Fact]
    public void FormatToolResponse_IsError_GetsErrorPrefix()
    {
        var response = JsonDocument.Parse("""
        {
          "result": {
            "isError": true,
            "content": [ { "type": "text", "text": "Page not found" } ]
          }
        }
        """).RootElement.Clone();

        Assert.Equal("Error: Page not found", PlaywrightBrowserService.FormatToolResponse(response));
    }

    [Fact]
    public void FormatToolResponse_ProtocolError_IsReported()
    {
        var response = JsonDocument.Parse("""
        { "error": { "message": "Browser not running" } }
        """).RootElement.Clone();

        var result = PlaywrightBrowserService.FormatToolResponse(response);
        Assert.StartsWith("Error:", result);
        Assert.Contains("Browser not running", result);
    }

    [Fact]
    public void FormatToolResponse_EmptyContent_ReturnsMarker()
    {
        var response = JsonDocument.Parse("""{ "result": { "content": [] } }""").RootElement.Clone();

        Assert.Equal("(no content returned)", PlaywrightBrowserService.FormatToolResponse(response));
    }

    // ─── Disabled-service behavior ────────────────────────────────────

    [Fact]
    public async Task Service_WithMissingCli_IsDisabled_AndRefusesCalls()
    {
        using var service = new PlaywrightBrowserService(cliPath: @"Z:\does\not\exist\cli.js");

        Assert.False(service.IsEnabled);
        var result = await service.InvokeAsync("browse_navigate", """{"url":"https://example.com"}""");
        Assert.StartsWith("Error:", result);
        Assert.Contains("disabled", result);
    }

    // ─── Live round-trip against the installed @playwright/mcp ────────

    [Fact]
    public async Task Live_NavigateAndSnapshot_ReturnsPageContent()
    {
        var cliPath = PlaywrightBrowserService.DefaultCliPath();
        if (!File.Exists(cliPath))
            return; // package not installed — nothing to test here

        using var service = new PlaywrightBrowserService(headless: true);
        Assert.True(service.IsEnabled);

        var opened = await service.InvokeAsync("browse_navigate", """{"url":"about:blank"}""");
        Assert.False(opened.StartsWith("Error:", StringComparison.Ordinal), opened);

        var snapshot = await service.InvokeAsync("browse_snapshot", "{}");
        Assert.False(snapshot.StartsWith("Error:", StringComparison.Ordinal), snapshot);
    }
}
