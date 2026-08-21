using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace YAOLlm;

public class TavilySearchService : SearchServiceBase
{
    private const string ApiUrl = "https://api.tavily.com/search";

    public override string Name => "Tavily";

    private readonly string _apiKey;

    private static readonly HttpClient _ddgClient = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" } }
    };

    public TavilySearchService(string apiKey, Logger logger)
        : base(new HttpClient(), logger)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    protected override HttpRequestMessage CreateRequest(string query, int maxResults, string searchDepth)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            query = query,
            max_results = maxResults,
            search_depth = searchDepth,
            include_answer = false,
            include_raw_content = false
        }), Encoding.UTF8, "application/json");
        return request;
    }

    protected override async Task<string?> OnRequestFailedAsync(string query, int maxResults, int statusCode)
    {
        // 432/433 = Tavily quota exceeded
        if (statusCode == 432 || statusCode == 433)
        {
            Logger.Log($"Tavily quota exceeded ({statusCode}), falling back to DuckDuckGo");
            return await SearchDuckDuckGoAsync(query, maxResults);
        }
        return null;
    }

    protected override IEnumerable<(string title, string url, string content)> ParseResults(string jsonResponse, int maxResults)
    {
        using var doc = JsonDocument.Parse(jsonResponse);
        if (!doc.RootElement.TryGetProperty("results", out var results))
        {
            Logger.Log("No 'results' property found in Tavily response");
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
                GetStringProperty(result, "content"));
        }
    }

    private async Task<string> SearchDuckDuckGoAsync(string query, int maxResults)
    {
        try
        {
            Logger.Log($"[DDG] Starting DuckDuckGo fallback search for: '{query}' (maxResults: {maxResults})");
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";

            var html = await _ddgClient.GetStringAsync(url);
            Logger.Log($"[DDG] Response received, length: {html.Length}");

            var titleMatches = Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""([^""]*)""[^>]*>([\s\S]*?)</a>");
            var snippetMatches = Regex.Matches(html, @"<a[^>]*class=""result__snippet""[^>]*>([\s\S]*?)</a>");
            Logger.Log($"[DDG] Regex matches - titles: {titleMatches.Count}, snippets: {snippetMatches.Count}");

            var results = new StringBuilder();
            int count = Math.Min(titleMatches.Count, maxResults);

            for (int i = 0; i < count; i++)
            {
                var rawUrl = titleMatches[i].Groups[1].Value;
                var title = Regex.Replace(titleMatches[i].Groups[2].Value, "<[^>]*>", "").Trim();

                var uddgMatch = Regex.Match(rawUrl, @"uddg=([^&]+)");
                var actualUrl = uddgMatch.Success ? Uri.UnescapeDataString(uddgMatch.Groups[1].Value) : rawUrl;

                var snippet = "";
                if (i < snippetMatches.Count)
                    snippet = Regex.Replace(snippetMatches[i].Groups[1].Value, "<[^>]*>", "").Trim();

                results.AppendLine($"**{title}**");
                results.AppendLine($"URL: {actualUrl}");
                results.AppendLine($"Content: {snippet}");

                if (i < count - 1)
                {
                    results.AppendLine();
                    results.AppendLine("---");
                    results.AppendLine();
                }
            }

            var result = count > 0 ? results.ToString().Trim() : "No search results found";
            Logger.Log($"[DDG] Search completed, {count} results, output length: {result.Length}");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Log($"[DDG] Fallback search failed: {ex.Message}");
            return "Error: DuckDuckGo fallback search failed.";
        }
    }
}
