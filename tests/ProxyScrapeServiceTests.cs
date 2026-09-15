using System;
using Xunit;

namespace YAOLlm.Tests;

public class ProxyScrapeServiceTests
{
    [Fact]
    public void BuildRequestUrl_EncodesUrlAndRequestsJson()
    {
        var url = ProxyScrapeService.BuildRequestUrl(
            "https://inference.camalolo.com/api/scrape", "https://example.com/page?a=1&b=2");

        // url must be URL-encoded; format=json per contract.
        Assert.Equal(
            "https://inference.camalolo.com/api/scrape?url=https%3A%2F%2Fexample.com%2Fpage%3Fa%3D1%26b%3D2&format=json",
            url);
    }

    [Fact]
    public void BuildRequestUrl_AppendsWithAmpersand_WhenEndpointHasQuery()
    {
        var url = ProxyScrapeService.BuildRequestUrl(
            "https://host/api/scrape?region=eu", "https://example.com");

        Assert.Equal("https://host/api/scrape?region=eu&url=https%3A%2F%2Fexample.com&format=json", url);
    }

    [Fact]
    public void ParseResponse_NormalizedSchema()
    {
        const string json = """
            {
              "url": "https://example.com/page",
              "title": "Example Page",
              "content": "# Heading\n\nFull page markdown.",
              "provider": "firecrawl"
            }
            """;

        var (title, content) = ProxyScrapeService.ParseResponse(json);

        Assert.Equal("Example Page", title);
        Assert.Equal("# Heading\n\nFull page markdown.", content);
    }

    [Fact]
    public void ParseResponse_ToleratesMissingAndNullFields()
    {
        const string json = """{ "title": null }""";

        var (title, content) = ProxyScrapeService.ParseResponse(json);

        Assert.Equal("", title);
        Assert.Equal("", content);
    }

    [Fact]
    public void ParseResponse_NonObjectRoot_ReturnsEmpty()
    {
        var (title, content) = ProxyScrapeService.ParseResponse("""["unexpected"]""");

        Assert.Equal("", title);
        Assert.Equal("", content);
    }

    [Fact]
    public void DeriveScrapeUrl_ReplacesTrailingSearch()
    {
        var derived = ProxyScrapeService.DeriveScrapeUrl("https://inference.camalolo.com/api/search");

        Assert.Equal("https://inference.camalolo.com/api/scrape", derived);
    }

    [Fact]
    public void DeriveScrapeUrl_PreservesTrailingSlashAndCase()
    {
        var derived = ProxyScrapeService.DeriveScrapeUrl("https://host/api/Search/");

        Assert.Equal("https://host/api/scrape/", derived);
    }

    [Fact]
    public void DeriveScrapeUrl_ReturnsNull_WhenNotASearchEndpoint()
    {
        Assert.Null(ProxyScrapeService.DeriveScrapeUrl("https://host/api/other"));
        Assert.Null(ProxyScrapeService.DeriveScrapeUrl(""));
        Assert.Null(ProxyScrapeService.DeriveScrapeUrl(null));
    }

    [Fact]
    public void DetectErrorPage_NotFoundTitle_ReturnsError()
    {
        // GameSpot-style: real HTTP 200 from the proxy, but the page is the
        // site's own 404 page (title + a few KB of nav chrome).
        var navJunk = string.Concat(Enumerable.Repeat("Home Guides News Forums ", 170)); // ~4k chars

        var error = ProxyScrapeService.DetectErrorPage("404: Not Found - GameSpot", navJunk);

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error);
        Assert.Contains("404: Not Found - GameSpot", error);
        Assert.Contains("Do not guess", error);
    }

    [Fact]
    public void DetectErrorPage_NotFoundPhraseInTitle_ReturnsError()
    {
        var error = ProxyScrapeService.DetectErrorPage(
            "Page not found | PowerPyx", "Sorry, we could not find that page.");

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error);
    }

    [Fact]
    public void DetectErrorPage_SteamStyleErrorTitle_ReturnsError()
    {
        var error = ProxyScrapeService.DetectErrorPage(
            "Steam Community :: Error", "### Error 404\n### Error 404\nThe page you requested was not found.");

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error);
    }

    [Fact]
    public void DetectErrorPage_TinyContent_ReturnsError()
    {
        // MapGenie-style JS app: the scraper gets only a ~177-char shell.
        var shell = new string('x', 177);

        var error = ProxyScrapeService.DetectErrorPage("Dying Light: The Beast Map | MapGenie", shell);

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error);
        Assert.Contains("177 chars", error);
    }

    [Fact]
    public void DetectErrorPage_RealPage_ReturnsNull()
    {
        var content = string.Concat(Enumerable.Repeat("Treasure map location. ", 400)); // ~9k chars

        Assert.Null(ProxyScrapeService.DetectErrorPage(
            "Castor Woods Recipes - Dying Light: The Beast Guide - IGN", content));
    }

    [Fact]
    public void DetectErrorPage_ErrorWordInTitle_LongRealContent_ReturnsNull()
    {
        // A legit "how to fix error ..." guide — long enough to be real content.
        var guide = string.Concat(Enumerable.Repeat("Step: verify the game files. ", 500)); // ~14k chars

        Assert.Null(ProxyScrapeService.DetectErrorPage(
            "How to fix error 404 in Dying Light: The Beast", guide));
    }

    [Fact]
    public void DetectErrorPage_ErrorWordInTitle_JustAboveMinLength_IsRejected()
    {
        // Same legit-looking title, but a stub body → error page wins.
        var stub = string.Concat(Enumerable.Repeat("word ", 60)); // 300 chars

        var error = ProxyScrapeService.DetectErrorPage(
            "How to fix error 404 in Dying Light: The Beast", stub);

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error);
    }
}
