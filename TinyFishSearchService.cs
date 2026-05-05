using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace YAOLlm;

public class TinyFishSearchService : ISearchService, IDisposable
{
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

            var url = $"https://api.search.tinyfish.ai?query={Uri.EscapeDataString(query)}&location=US";
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

            var formattedResults = FormatSearchResults(jsonResponse);
            _logger.Log($"[TinyFish] Search completed, formatted {formattedResults.Split('\n').Length} lines");

            return formattedResults;
        }
        catch (Exception ex)
        {
            _logger.Log($"[TinyFish] Error in SearchAsync: {ex.Message}");
            return $"Error: Failed to perform search. {ex.Message}";
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
                _logger.Log("[TinyFish] No 'results' property found in response");
                return "No search results found";
            }

            var results = resultsElement.EnumerateArray();
            var formattedResults = new StringBuilder();

            int resultCount = 0;
            foreach (var result in results)
            {
                resultCount++;

                var title = result.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
                var url = result.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "";
                var snippet = result.TryGetProperty("snippet", out var snippetElement) ? snippetElement.GetString() ?? "" : "";

                formattedResults.AppendLine($"**{title}**");
                formattedResults.AppendLine($"URL: {url}");
                formattedResults.AppendLine($"Content: {snippet}");

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
            return $"Error formatting search results: {ex.Message}";
        }
    }
}
