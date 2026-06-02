using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RestSharp;

namespace YAOLlm;

public class TavilySearchService : ISearchService, IDisposable
{
    private readonly string _apiKey;
    private readonly Logger _logger;
    private readonly RestClient _client;
    private const string ApiBaseUrl = "https://api.tavily.com";
    private bool _disposed;

    private static readonly HttpClient _ddgClient = new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" } }
    };

    public TavilySearchService(string apiKey, Logger logger)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = new RestClient(ApiBaseUrl);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }

    public async Task<string> SearchAsync(string query, int maxResults = 5, string searchDepth = "basic")
    {
        try
        {
            _logger.Log($"Performing Tavily search: '{query}' (maxResults: {maxResults}, depth: {searchDepth})");

            var request = new RestRequest("/search", Method.Post);
            
            request.AddHeader("Authorization", $"Bearer {_apiKey}");
            request.AddHeader("Content-Type", "application/json");
            
            var requestBody = new
            {
                query = query,
                max_results = maxResults,
                search_depth = searchDepth,
                include_answer = false,
                include_raw_content = false
            };

            request.AddJsonBody(requestBody);

            var response = await _client.ExecuteAsync(request);

            if (!response.IsSuccessful)
            {
                var errorDetail = response.Content ?? response.ErrorMessage ?? "No details available";
                _logger.Log($"Tavily API request failed: {(int)response.StatusCode} - {errorDetail}");

                var statusCode = (int)response.StatusCode;
                if (statusCode == 432 || statusCode == 433)
                {
                    _logger.Log($"Tavily quota exceeded ({statusCode}), falling back to DuckDuckGo");
                    return await SearchDuckDuckGoAsync(query, maxResults);
                }

                throw new Exception($"Search failed ({statusCode}): {errorDetail}");
            }

            if (string.IsNullOrEmpty(response.Content))
            {
                _logger.Log("Tavily API returned empty response");
                return "Error: Received empty response from search API";
            }

            _logger.Log($"Tavily response received: {response.Content.Substring(0, Math.Min(200, response.Content.Length))}...");

            var formattedResults = FormatSearchResults(response.Content);
            _logger.Log($"Search completed, formatted {formattedResults.Split('\n').Length} lines");
            
            return formattedResults;
        }
        catch (Exception ex)
        {
            _logger.Log($"Error in TavilySearchService.SearchAsync: {ex.Message}");
            return $"Error: Failed to perform search. {ex.Message}";
        }
    }

    private async Task<string> SearchDuckDuckGoAsync(string query, int maxResults)
    {
        try
        {
            _logger.Log($"[DDG] Starting DuckDuckGo fallback search for: '{query}' (maxResults: {maxResults})");
            var url = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
            _logger.Log($"[DDG] Requesting: {url}");

            var html = await _ddgClient.GetStringAsync(url);
            _logger.Log($"[DDG] Response received, length: {html.Length}");

            var titleMatches = Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""([^""]*)""[^>]*>([\s\S]*?)</a>");
            var snippetMatches = Regex.Matches(html, @"<a[^>]*class=""result__snippet""[^>]*>([\s\S]*?)</a>");
            _logger.Log($"[DDG] Regex matches - titles: {titleMatches.Count}, snippets: {snippetMatches.Count}");

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

                _logger.Log($"[DDG] Result {i + 1}: title='{title}', url='{actualUrl}', snippet_len={snippet.Length}");

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
            _logger.Log($"[DDG] Search completed, {count} results, output length: {result.Length}");
            return result;
        }
        catch (Exception ex)
        {
            _logger.Log($"[DDG] Fallback search failed: {ex.Message}");
            return $"Error: DuckDuckGo fallback search failed. {ex.Message}";
        }
    }

    private string FormatSearchResults(string jsonResponse)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonResponse);
            var root = document.RootElement;

            if (!root.TryGetProperty("results", out var resultsElement))
            {
                _logger.Log("No 'results' property found in Tavily response");
                return "No search results found";
            }

            var results = resultsElement.EnumerateArray();
            var formattedResults = new System.Text.StringBuilder();

            int resultCount = 0;
            foreach (var result in results)
            {
                resultCount++;

                var title = result.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
                var url = result.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "";
                var content = result.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? "" : "";

                formattedResults.AppendLine($"**{title}**");
                formattedResults.AppendLine($"URL: {url}");
                formattedResults.AppendLine($"Content: {content}");
                
                if (resultCount < resultsElement.GetArrayLength())
                {
                    formattedResults.AppendLine();
                    formattedResults.AppendLine("---");
                    formattedResults.AppendLine();
                }
            }

            if (resultCount == 0)
            {
                return "No search results found for the query";
            }

            return formattedResults.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.Log($"Error formatting search results: {ex.Message}");
            return $"Error: Formatting search results failed: {ex.Message}";
        }
    }
}
