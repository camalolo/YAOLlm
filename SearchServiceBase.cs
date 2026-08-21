using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace YAOLlm;

/// <summary>
/// Template base for search services: handles request execution, error mapping,
/// quota fallbacks, and result formatting. Subclasses define the endpoint
/// request and the JSON result shape.
/// </summary>
public abstract class SearchServiceBase : ISearchService, IDisposable
{
    protected readonly HttpClient Client;
    protected readonly Logger Logger;
    private bool _disposed;

    public abstract string Name { get; }

    protected SearchServiceBase(HttpClient client, Logger logger)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> SearchAsync(string query, int maxResults = 5, string searchDepth = "basic")
    {
        try
        {
            Logger.Log($"[{Name}] Performing search: '{query}' (maxResults: {maxResults}, depth: {searchDepth})");

            using var request = CreateRequest(query, maxResults, searchDepth);
            using var response = await Client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorDetail = await response.Content.ReadAsStringAsync();
                var statusCode = (int)response.StatusCode;
                Logger.Log($"[{Name}] API request failed: {statusCode} - {errorDetail}");

                var fallback = await OnRequestFailedAsync(query, maxResults, statusCode);
                if (fallback != null)
                    return fallback;

                return $"Error: Search failed ({statusCode}): {errorDetail}";
            }

            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (string.IsNullOrEmpty(jsonResponse))
            {
                Logger.Log($"[{Name}] API returned empty response");
                return "Error: Received empty response from search API";
            }

            Logger.Log($"[{Name}] Response received: {Truncate(jsonResponse, 200)}...");

            var formatted = FormatResults(ParseResults(jsonResponse, maxResults).ToList());
            Logger.Log($"[{Name}] Search completed, {formatted.Split('\n').Length} lines");
            return formatted;
        }
        catch (Exception ex)
        {
            Logger.Log($"[{Name}] Error in SearchAsync: {ex.Message}");
            return $"Error: Failed to perform search. {ex.Message}";
        }
    }

    /// <summary>
    /// Builds the HTTP request for the provider's search endpoint.
    /// </summary>
    protected abstract HttpRequestMessage CreateRequest(string query, int maxResults, string searchDepth);

    /// <summary>
    /// Extracts (title, url, content) triples from a successful JSON response.
    /// </summary>
    protected abstract IEnumerable<(string title, string url, string content)> ParseResults(string jsonResponse, int maxResults);

    /// <summary>
    /// Optional fallback (e.g. DuckDuckGo scrape) for specific HTTP failures.
    /// Return null to propagate the error instead.
    /// </summary>
    protected virtual Task<string?> OnRequestFailedAsync(string query, int maxResults, int statusCode)
        => Task.FromResult<string?>(null);

    private static string FormatResults(IReadOnlyList<(string title, string url, string content)> items)
    {
        if (items.Count == 0)
            return "No search results found for the query";

        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            sb.AppendLine($"**{items[i].title}**");
            sb.AppendLine($"URL: {items[i].url}");
            sb.AppendLine($"Content: {items[i].content}");
            if (i < items.Count - 1)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }
        return sb.ToString().Trim();
    }

    protected static string GetStringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null ? prop.GetString() ?? "" : "";

    protected static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Client.Dispose();
    }
}
