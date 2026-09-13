using Xunit;

namespace YAOLlm.Tests;

public class WebFetchServiceTests
{
    [Fact]
    public void HtmlToText_DropsChromeAndKeepsContent()
    {
        var html = """
            <html>
              <head><title>Guide</title><style>body { color: red }</style></head>
              <body>
                <!-- template blob -->
                <nav><a href="/">Home</a><a href="/maps">Maps</a> Skip to content</nav>
                <script>var tracking = 1;</script>
                <header>Site Banner</header>
                <article><h1>Treasure Map C4</h1><p>Behind the counter.</p></article>
                <footer>Copyright</footer>
              </body>
            </html>
            """;

        var text = WebFetchService.HtmlToText(html);

        Assert.Contains("Treasure Map C4", text);
        Assert.Contains("Behind the counter.", text);
        Assert.DoesNotContain("Skip to content", text);
        Assert.DoesNotContain("Site Banner", text);
        Assert.DoesNotContain("Copyright", text);
        Assert.DoesNotContain("var tracking", text);
        Assert.DoesNotContain("body { color: red }", text);
        Assert.DoesNotContain("template blob", text);
        // Title survives as the first readable line
        Assert.StartsWith("Guide", text);
    }

    [Fact]
    public void HtmlToText_ConvertsBlocksAndDecodesEntities()
    {
        var html = "<ul><li>First</li><li>Second &amp; third</li></ul><table><tr><td>Cell&#160;A</td><td>Cell B</td></tr></table>";

        var text = WebFetchService.HtmlToText(html);

        Assert.Contains("First\nSecond & third", text);
        Assert.Contains("Cell\u00a0A", text);
        Assert.Contains("Cell B", text);
    }

    [Fact]
    public void ExtractContent_BotBlockedPage_ReturnsErrorNotPseudoContent()
    {
        // What Reddit actually returns to a bot: a shell whose only text is "Reddit"
        var html = "<html><head><title>Reddit</title></head><body><div>Reddit</div></body></html>";

        var result = WebFetchService.ExtractContent(html, isHtml: true, maxLength: 15000);

        Assert.StartsWith("Error:", result);
        Assert.Contains("chars of readable text", result);
        Assert.Contains("Reddit", result);
        Assert.Contains("Do not guess its contents", result);
    }

    [Fact]
    public void ExtractContent_ShortNonHtmlPayload_IsKeptAsIs()
    {
        // JSON APIs legitimately return short payloads — the gate is HTML-only
        const string json = """{"results": []}""";

        var result = WebFetchService.ExtractContent(json, isHtml: false, maxLength: 15000);

        Assert.Equal(json, result);
    }

    [Fact]
    public void ExtractContent_LongPage_TruncatesWithMarker()
    {
        var html = "<p>" + new string('x', 20000) + "</p>";

        var result = WebFetchService.ExtractContent(html, isHtml: true, maxLength: 15000);

        Assert.True(result.Length < 20000);
        Assert.Contains("[truncated at 15000 characters", result);
    }

    [Fact]
    public void ExtractContent_ReadablePage_PassesGate()
    {
        var article = string.Concat(Enumerable.Repeat("word ", 100)); // 500 chars — above the gate
        var html = $"<article><p>{article}</p></article>";

        var result = WebFetchService.ExtractContent(html, isHtml: true, maxLength: 15000);

        Assert.False(result.StartsWith("Error:"), result);
        Assert.Contains("word", result);
    }

    [Fact]
    public void ApplyBrowserHeaders_SetsRealisticBrowserHeaders()
    {
        using var msg = new HttpRequestMessage();

        WebFetchService.ApplyBrowserHeaders(msg.Headers);

        // Known headers round-trip through the typed collections
        Assert.True(msg.Headers.UserAgent.Count > 0);
        var firstProduct = msg.Headers.UserAgent.First().Product;
        Assert.Equal("Mozilla", firstProduct?.Name);
        Assert.Equal("5.0", firstProduct?.Version);
        Assert.Contains(msg.Headers.UserAgent, p => p.Product?.Name == "Chrome");
        Assert.DoesNotContain(msg.Headers.UserAgent,
            p => (p.Product?.Name ?? "").Contains("bot", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(msg.Headers.Accept, a => a.MediaType == "text/html");
        Assert.Contains(msg.Headers.Accept, a => a.MediaType == "*/*");
        Assert.Contains(msg.Headers.AcceptLanguage, l => l.Value == "en-US");

        // Unknown headers (client hints / fetch metadata) via raw values
        Assert.True(msg.Headers.TryGetValues("sec-fetch-dest", out var dest));
        Assert.Equal("document", dest.First());
        Assert.True(msg.Headers.TryGetValues("sec-fetch-mode", out var mode));
        Assert.Equal("navigate", mode.First());
        Assert.True(msg.Headers.TryGetValues("sec-fetch-site", out var site));
        Assert.Equal("none", site.First());
        Assert.True(msg.Headers.TryGetValues("sec-ch-ua-platform", out var platform));
        Assert.Equal("\"Windows\"", platform.First());
    }

    [Fact]
    public void ApplyBrowserHeaders_OverrideReplacesDefaultUserAgent()
    {
        using var msg = new HttpRequestMessage();

        WebFetchService.ApplyBrowserHeaders(msg.Headers, "MyCustomAgent/2.0");

        var ua = Assert.Single(msg.Headers.UserAgent);
        Assert.Equal("MyCustomAgent", ua.Product?.Name);
        Assert.Equal("2.0", ua.Product?.Version);
        // Non-UA browser headers are still applied
        Assert.True(msg.Headers.TryGetValues("sec-fetch-mode", out var mode));
        Assert.Equal("navigate", mode.First());
    }

    [Fact]
    public void ApplyBrowserHeaders_BlankOverrideFallsBackToDefault()
    {
        using var msg = new HttpRequestMessage();

        WebFetchService.ApplyBrowserHeaders(msg.Headers, "   ");

        Assert.Contains(msg.Headers.UserAgent, p => p.Product?.Name == "Mozilla" && p.Product?.Version == "5.0");
        Assert.Contains(msg.Headers.UserAgent, p => p.Product?.Name == "Chrome");
    }
}
