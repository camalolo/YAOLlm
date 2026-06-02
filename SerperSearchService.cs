using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace YAOLlm;

public class SerperSearchService : ISearchService, IDisposable
{
    private readonly string _apiKey;
    private readonly Logger _logger;
    private readonly HttpClient _client;
    private bool _disposed;

    public SerperSearchService(string apiKey, Logger logger)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = new HttpClient();
        _client.DefaultRequestHeaders.Add("X-API-KEY", _apiKey);
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
            _logger.Log($"[Serper] Performing search: '{query}' (maxResults: {maxResults})");

            var requestBody = new
            {
                q = query,
                num = maxResults,
                gl = "us",
                hl = "en"
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _client.PostAsync("https://google.serper.dev/search", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorDetail = await response.Content.ReadAsStringAsync();
                _logger.Log($"[Serper] API request failed: {(int)response.StatusCode} - {errorDetail}");
                throw new Exception($"Search failed ({(int)response.StatusCode}): {errorDetail}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(jsonResponse))
            {
                _logger.Log("[Serper] API returned empty response");
                return "Error: Received empty response from search API";
            }

            _logger.Log($"[Serper] Response received: {jsonResponse.Substring(0, Math.Min(200, jsonResponse.Length))}...");

            var formattedResults = FormatSearchResults(jsonResponse, maxResults);
            _logger.Log($"[Serper] Search completed, formatted {formattedResults.Split('\n').Length} lines");

            return formattedResults;
        }
        catch (Exception ex)
        {
            _logger.Log($"[Serper] Error in SearchAsync: {ex.Message}");
            return $"Error: Failed to perform search. {ex.Message}";
        }
    }

    private string FormatSearchResults(string jsonResponse, int maxResults = 5)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonResponse);
            var root = document.RootElement;

            if (!root.TryGetProperty("organic", out var resultsElement))
            {
                _logger.Log("[Serper] No 'organic' property found in response");
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
                var link = result.TryGetProperty("link", out var linkElement) ? linkElement.GetString() ?? "" : "";
                var snippet = result.TryGetProperty("snippet", out var snippetElement) ? snippetElement.GetString() ?? "" : "";

                formattedResults.AppendLine($"**{title}**");
                formattedResults.AppendLine($"URL: {link}");
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
            _logger.Log($"[Serper] Error formatting search results: {ex.Message}");
            return $"Error: Formatting search results failed: {ex.Message}";
        }
    }
}
