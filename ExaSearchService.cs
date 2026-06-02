using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace YAOLlm;

public class ExaSearchService : ISearchService, IDisposable
{
    public string Name => "Exa";
    private readonly string _apiKey;
    private readonly Logger _logger;
    private readonly HttpClient _client;
    private bool _disposed;

    public ExaSearchService(string apiKey, Logger logger)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = new HttpClient();
        _client.DefaultRequestHeaders.Add("x-api-key", _apiKey);
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
            _logger.Log($"[Exa] Performing search: '{query}' (maxResults: {maxResults})");

            var requestBody = new
            {
                query = query,
                numResults = maxResults,
                contents = new { text = true }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("https://api.exa.ai/search", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorDetail = await response.Content.ReadAsStringAsync();
                _logger.Log($"[Exa] API request failed: {(int)response.StatusCode} - {errorDetail}");
                throw new Exception($"Search failed ({(int)response.StatusCode}): {errorDetail}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(jsonResponse))
            {
                _logger.Log("[Exa] API returned empty response");
                return "Error: Received empty response from search API";
            }

            _logger.Log($"[Exa] Response received: {jsonResponse.Substring(0, Math.Min(200, jsonResponse.Length))}...");

            var formattedResults = FormatSearchResults(jsonResponse, maxResults);
            _logger.Log($"[Exa] Search completed, formatted {formattedResults.Split('\n').Length} lines");

            return formattedResults;
        }
        catch (Exception ex)
        {
            _logger.Log($"[Exa] Error in SearchAsync: {ex.Message}");
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
                _logger.Log("[Exa] No 'results' property found in response");
                return "No search results found";
            }

            var results = resultsElement.EnumerateArray();
            var formattedResults = new StringBuilder();

            int resultCount = 0;
            foreach (var result in results)
            {
                if (resultCount >= maxResults) break;
                resultCount++;

                var title = result.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
                var url = result.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "";
                var text = result.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? "" : "";

                formattedResults.AppendLine($"**{title}**");
                formattedResults.AppendLine($"URL: {url}");
                formattedResults.AppendLine($"Content: {text}");

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
            _logger.Log($"[Exa] Error formatting search results: {ex.Message}");
            return $"Error: Formatting search results failed: {ex.Message}";
        }
    }
}
