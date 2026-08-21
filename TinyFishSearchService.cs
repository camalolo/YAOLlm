using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;

namespace YAOLlm;

public class TinyFishSearchService : SearchServiceBase
{
    private const string ApiUrl = "https://api.search.tinyfish.ai";

    public override string Name => "TinyFish";

    public TinyFishSearchService(string apiKey, Logger logger)
        : base(CreateClient(apiKey), logger)
    {
        _ = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    private static HttpClient CreateClient(string apiKey)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return client;
    }

    protected override HttpRequestMessage CreateRequest(string query, int maxResults, string searchDepth)
    {
        var fetchConfig = Uri.EscapeDataString("{\"format\":\"markdown\"}");
        var url = $"{ApiUrl}?query={Uri.EscapeDataString(query)}&location=US&fetch={fetchConfig}";
        return new HttpRequestMessage(HttpMethod.Get, url);
    }

    protected override IEnumerable<(string title, string url, string content)> ParseResults(string jsonResponse, int maxResults)
    {
        using var doc = JsonDocument.Parse(jsonResponse);
        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            Logger.Log("[TinyFish] No 'results' property found in response");
            yield break;
        }

        // TinyFish results are verbose (fetched page content) — cap at 3
        var cap = Math.Min(maxResults, 3);
        int count = 0;
        foreach (var result in results.EnumerateArray())
        {
            if (count++ >= cap)
                yield break;

            var content = GetStringProperty(result, "snippet");

            // Use fetched full-page content if available, otherwise fall back to snippet
            if (result.TryGetProperty("fetch", out var fetchElement) && fetchElement.ValueKind == JsonValueKind.Object
                && fetchElement.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                var fetched = textElement.GetString() ?? "";
                if (!string.IsNullOrEmpty(fetched))
                    content = fetched;
            }

            yield return (
                GetStringProperty(result, "title"),
                GetStringProperty(result, "url"),
                content);
        }
    }
}
