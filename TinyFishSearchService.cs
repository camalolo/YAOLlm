using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace YAOLlm;

public class TinyFishSearchService : ISearchService, IDisposable
{
    public string Name => "TinyFish";
    private readonly string _apiKey;
    private readonly Logger _logger;
    private readonly HttpClient _client;
    private bool _disposed;

    public TinyFishSearchService(string apiKey, Logger logger)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = new HttpClient();
        _client.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
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
            _logger.Log($"[TinyFish] Performing search: '{query}' (maxResults: {maxResults})");

            var fetchConfig = Uri.EscapeDataString("{\"format\":\"markdown\"}");
            var url = $"https://api.search.tinyfish.ai?query={Uri.EscapeDataString(query)}&location=US&fetch={fetchConfig}";
            var response = await _client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var errorDetail = await response.Content.ReadAsStringAsync();
                _logger.Log($"[TinyFish] API request failed: {(int)response.StatusCode} - {errorDetail}");
                throw new Exception($"Search failed ({(int)response.StatusCode}): {errorDetail}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(jsonResponse))
            {
                _logger.Log("[TinyFish] API returned empty response");
                return "Error: Received empty response from search API";
            }

            _logger.Log($"[TinyFish] Response received: {jsonResponse.Substring(0, Math.Min(200, jsonResponse.Length))}...");

            var formattedResults = FormatSearchResults(jsonResponse, maxResults);
            _logger.Log($"[TinyFish] Search completed, formatted {formattedResults.Split('\n').Length} lines");

            return formattedResults;
        }
        catch (Exception ex)
        {
            _logger.Log($"[TinyFish] Error in SearchAsync: {ex.Message}");
            return $"Error: Failed to perform search. {ex.Message}";
        }
    }

    private string FormatSearchResults(string jsonResponse, int maxResults = 5)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonResponse);
            var root = document.RootElement;

            if (!root.TryGetProperty("results", out var resultsElement))
            {
                _logger.Log("[TinyFish] No 'results' property found in response");
                return "No search results found";
            }

            var results = resultsElement.EnumerateArray();
            var formattedResults = new StringBuilder();

            int resultCount = 0;
            int maxToFormat = Math.Min(maxResults, 3);
            foreach (var result in results)
            {
                if (resultCount >= maxToFormat) break;
                resultCount++;

                var title = result.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
                var url = result.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "";
                var snippet = result.TryGetProperty("snippet", out var snippetElement) ? snippetElement.GetString() ?? "" : "";

                // Use fetched full-page content if available, otherwise fall back to snippet
                var content = snippet;
                if (result.TryGetProperty("fetch", out var fetchElement) && fetchElement.ValueKind == JsonValueKind.Object
                    && fetchElement.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    var fetched = textElement.GetString() ?? "";
                    if (!string.IsNullOrEmpty(fetched))
                        content = fetched;
                }

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
            _logger.Log($"[TinyFish] Error formatting search results: {ex.Message}");
            return $"Error: Formatting search results failed: {ex.Message}";
        }
    }
}
