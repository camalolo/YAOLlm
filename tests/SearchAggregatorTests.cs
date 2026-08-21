using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace YAOLlm.Tests;

public class FakeSearchService : ISearchService
{
    public string Name { get; }
    private readonly string _result;

    public FakeSearchService(string name, string result)
    {
        Name = name;
        _result = result;
    }

    public Task<string> SearchAsync(string query, int maxResults = 5, string searchDepth = "basic")
        => Task.FromResult(_result);
}

public class SearchServiceAggregatorTests
{
    private static Logger Logger() => new();

    [Fact]
    public async Task SearchAsync_ReturnsFirstSuccessfulService()
    {
        var aggregator = new SearchServiceAggregator(new System.Collections.Generic.List<ISearchService>
        {
            new FakeSearchService("Bad", "Error: quota exceeded"),
            new FakeSearchService("Good", "result body"),
        }, Logger());

        var result = await aggregator.SearchAsync("query");

        Assert.Equal("result body", result);
    }

    [Fact]
    public async Task SearchAsync_AllFailed_ReturnsLastError()
    {
        var aggregator = new SearchServiceAggregator(new System.Collections.Generic.List<ISearchService>
        {
            new FakeSearchService("A", "Error: a failed"),
            new FakeSearchService("B", "Error: b failed"),
        }, Logger());

        var result = await aggregator.SearchAsync("query");

        Assert.Equal("Error: b failed", result);
    }

    [Fact]
    public async Task SearchAsync_EmptyServiceList_ReturnsError()
    {
        var aggregator = new SearchServiceAggregator(new System.Collections.Generic.List<ISearchService>(), Logger());

        var result = await aggregator.SearchAsync("query");

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
    }
}

public class LLMExceptionTests
{
    [Theory]
    [InlineData(401, "Invalid API key")]
    [InlineData(403, "Access denied")]
    [InlineData(404, "API endpoint not found")]
    [InlineData(429, "Rate limited - please wait")]
    [InlineData(500, "Server error - try again later")]
    [InlineData(503, "Server error - try again later")]
    [InlineData(0, "Connection failed")]
    [InlineData(418, "Request failed")]
    public void CreateWithStatusCode_MapsUserMessages(int statusCode, string expected)
    {
        var ex = LLMException.CreateWithStatusCode(statusCode, "body", "gemini");

        Assert.Equal(expected, ex.UserMessage);
        Assert.Equal(statusCode, ex.StatusCode);
        Assert.Equal("body", ex.Details);
        Assert.StartsWith("[gemini]", ex.Message, StringComparison.Ordinal);
    }
}
