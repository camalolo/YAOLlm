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
}
