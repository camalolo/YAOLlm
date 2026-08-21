using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace YAOLlm;

public class SerperSearchService : SearchServiceBase
{
    private const string ApiUrl = "https://google.serper.dev/search";

    public override string Name => "Serper";

    public SerperSearchService(string apiKey, Logger logger)
        : base(CreateClient(apiKey), logger)
    {
        _ = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    private static HttpClient CreateClient(string apiKey)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-API-KEY", apiKey);
        return client;
    }

    protected override HttpRequestMessage CreateRequest(string query, int maxResults, string searchDepth)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            q = query,
            num = maxResults,
            gl = "us",
            hl = "en"
        }), Encoding.UTF8, "application/json");
        return request;
    }

    protected override IEnumerable<(string title, string url, string content)> ParseResults(string jsonResponse, int maxResults)
    {
        using var doc = JsonDocument.Parse(jsonResponse);
        if (!doc.RootElement.TryGetProperty("organic", out var results))
        {
            Logger.Log("[Serper] No 'organic' property found in response");
            yield break;
        }

        int count = 0;
        foreach (var result in results.EnumerateArray())
        {
            if (count++ >= maxResults)
                yield break;
            yield return (
                GetStringProperty(result, "title"),
                GetStringProperty(result, "link"),
                GetStringProperty(result, "snippet"));
        }
    }
}
