using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace YAOLlm;

/// <summary>
/// Search backend backed by the user's own proxy search endpoint
/// (GET {PROXY_SEARCH_URL}?q=...&amp;limit=N with Bearer PROXY_API_KEY).
/// Provider failover happens server-side (TinyFish/Tavily/Exa chain), so this
/// is a thin client. Per the endpoint contract:
/// - retry 429 (honoring Retry-After), 5xx, and network errors (max 3 attempts,
///   jittered-ish backoff 1s → 4s);
/// - never retry 400/401 or empty result sets;
/// - empty results with HTTP 200 are a valid answer, not an error;
/// - the informational `provider` field is logged at most, never used.
/// </summary>
public class ProxySearchService : ISearchService, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _endpointUrl;
    private readonly Logger _logger;
    private bool _disposed;

    public string Name => "proxy";

    public ProxySearchService(string apiKey, string endpointUrl, Logger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Proxy search API key is required", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new ArgumentException("Proxy search endpoint URL is required (PROXY_SEARCH_URL)", nameof(endpointUrl));

        _endpointUrl = endpointUrl;
        _logger = logger ?? new Logger();
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> SearchAsync(string query, int maxResults = 5, string searchDepth = "basic")
    {
        try
        {
            _logger.Log($"[proxy] Searching '{query}' (limit {maxResults}) via {_endpointUrl}");

            // backoff.Length = 2 → up to 3 total attempts.
            var backoff = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4) };
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = await _client.GetAsync(BuildRequestUrl(_endpointUrl, query, maxResults));
                    var body = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                        return FormatResults(ParseResponse(body, maxResults));

                    var status = (int)response.StatusCode;
                    _logger.Log($"[proxy] Attempt {attempt} failed: {status} - {Truncate(body, 300)}");

                    var retryable = status == 429 || status >= 500;
                    if (!retryable || attempt > backoff.Length)
                        return $"Error: Proxy search failed ({status}): {Truncate(body, 300)}";

                    await Task.Delay(GetRetryAfter(response) ?? backoff[attempt - 1]);
                }
                catch (Exception netEx) when (netEx is HttpRequestException or TaskCanceledException)
                {
                    // TaskCanceledException covers both our own 45s timeout and
                    // hard network failures — both are retryable per contract.
                    _logger.Log($"[proxy] Attempt {attempt} network error: {netEx.Message}");
                    if (attempt > backoff.Length)
                        return $"Error: Proxy search failed after {attempt} attempts: {netEx.Message}";
                    await Task.Delay(backoff[attempt - 1]);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"[proxy] Error in SearchAsync: {ex.Message}");
            return $"Error: Failed to perform search. {ex.Message}";
        }
    }

    /// <summary>Builds the GET URL with URL-encoded q and limit (per contract rule 1).</summary>
    public static string BuildRequestUrl(string endpointUrl, string query, int maxResults)
    {
        var separator = endpointUrl.Contains('?') ? '&' : '?';
        return $"{endpointUrl}{separator}q={Uri.EscapeDataString(query)}&limit={maxResults}";
    }

    /// <summary>
    /// Parses {"results":[{"title","url","snippet"},...]} — the stable
    /// normalized schema. Pure/static for testability.
    /// </summary>
    public static IReadOnlyList<(string title, string url, string content)> ParseResponse(string jsonResponse, int maxResults)
    {
        var results = new List<(string, string, string)>();
        using var doc = JsonDocument.Parse(jsonResponse);

        if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var title = GetString(item, "title");
                var url = GetString(item, "url");
                var snippet = GetString(item, "snippet");

                if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(url))
                    continue;

                results.Add((title, url, snippet));
                if (results.Count >= maxResults)
                    break;
            }
        }

        return results;
    }

    /// <summary>Formats results as markdown (title/URL/content blocks), consistent with what the model expects from a search tool.</summary>
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

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
            return delta;
        return null;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null
            ? prop.GetString() ?? ""
            : "";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
