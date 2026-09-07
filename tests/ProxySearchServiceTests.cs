using System;
using System.Linq;
using Xunit;

namespace YAOLlm.Tests;

public class ProxySearchServiceTests
{
    [Fact]
    public void BuildRequestUrl_EncodesQuery()
    {
        var url = ProxySearchService.BuildRequestUrl(
            "https://inference.camalolo.com/api/search", "best rdf database 2026 & more", 5);

        // q must be URL-encoded (contract rule 1); limit passed through.
        Assert.Equal("https://inference.camalolo.com/api/search?q=best%20rdf%20database%202026%20%26%20more&limit=5", url);
    }

    [Fact]
    public void BuildRequestUrl_AppendsWithAmpersand_WhenEndpointHasQuery()
    {
        var url = ProxySearchService.BuildRequestUrl(
            "https://host/api/search?lang=fr", "test", 8);

        Assert.Equal("https://host/api/search?lang=fr&q=test&limit=8", url);
    }

    [Fact]
    public void ParseResponse_NormalizedSchema()
    {
        const string json = """
            {
              "results": [
                { "title": "Example", "url": "https://example.com/a", "snippet": "Text excerpt" },
                { "title": "Second", "url": "https://example.com/b", "snippet": "More text" }
              ],
              "query": "q",
              "provider": "tavily"
            }
            """;

        var results = ProxySearchService.ParseResponse(json, 5);

        Assert.Equal(2, results.Count);
        Assert.Equal(("Example", "https://example.com/a", "Text excerpt"), results[0]);
    }

    [Fact]
    public void ParseResponse_EmptyResultsArray_IsValidAndEmpty()
    {
        // HTTP 200 with results: [] means "no hits", not an error.
        var results = ProxySearchService.ParseResponse("""{"results": [], "provider": "exa"}""", 5);

        Assert.Empty(results);
    }

    [Fact]
    public void ParseResponse_RespectsMaxResults()
    {
        const string json = """
            { "results": [
                { "title": "1", "url": "u1", "snippet": "s1" },
                { "title": "2", "url": "u2", "snippet": "s2" },
                { "title": "3", "url": "u3", "snippet": "s3" }
            ] }
            """;

        var results = ProxySearchService.ParseResponse(json, 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("1", results[0].title);
    }

    [Fact]
    public void ParseResponse_ToleratesMissingAndNullFields()
    {
        const string json = """
            { "results": [
                { "title": null, "url": "https://example.com/x" },
                { "url": "https://example.com/y" },
                { "title": "", "url": "" },
                "not-an-object"
            ] }
            """;

        var results = ProxySearchService.ParseResponse(json, 10);

        // Third item dropped (no title AND no url); fourth item skipped (not an object).
        Assert.Equal(2, results.Count);
        Assert.Equal("", results[0].content);
        Assert.Equal("https://example.com/y", results[1].url);
    }
}
