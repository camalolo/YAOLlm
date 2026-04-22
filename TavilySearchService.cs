using System;
using System.Text.Json;
using System.Threading.Tasks;
using RestSharp;

namespace YAOLlm;

public class TavilySearchService : IDisposable
{
    private readonly string _apiKey;
    private readonly Logger _logger;
    private readonly RestClient _client;
    private const string ApiBaseUrl = "https://api.tavily.com";
    private bool _disposed;

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
                throw new Exception($"Search failed ({(int)response.StatusCode}): {errorDetail}");
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
            return $"Error formatting search results: {ex.Message}";
        }
    }
}
