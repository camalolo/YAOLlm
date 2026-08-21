using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace YAOLlm;

public class ExaSearchService : SearchServiceBase
{
    private const string ApiUrl = "https://api.exa.ai/search";

    public override string Name => "Exa";

    private readonly string _apiKey;

    public ExaSearchService(string apiKey, Logger logger)
        : base(CreateClient(apiKey), logger)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    private static HttpClient CreateClient(string apiKey)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-api-key", apiKey);
        return client;
    }

    protected override HttpRequestMessage CreateRequest(string query, int maxResults, string searchDepth)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            query = query,
            numResults = maxResults,
            contents = new { text = true }
        }), Encoding.UTF8, "application/json");
        return request;
    }

    protected override IEnumerable<(string title, string url, string content)> ParseResults(string jsonResponse, int maxResults)
    {
        using var doc = JsonDocument.Parse(jsonResponse);
        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            Logger.Log("[Exa] No 'results' property found in response");
            yield break;
        }

        int count = 0;
        foreach (var result in results.EnumerateArray())
        {
            if (count++ >= maxResults)
                yield break;
            yield return (
                GetStringProperty(result, "title"),
                GetStringProperty(result, "url"),
                GetStringProperty(result, "text"));
        }
    }
}
