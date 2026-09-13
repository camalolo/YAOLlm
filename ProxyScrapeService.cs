using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YAOLlm;

/// <summary>
/// Web fetch backend backed by the user's own proxy scrape endpoint
/// (GET {scrapeUrl}?url=...&amp;format=json with Bearer PROXY_API_KEY — same
/// auth as /api/search). Headless rendering / bot-block handling happens
/// server-side, so this is a thin client. Per the endpoint contract:
/// - retry 429 (honoring Retry-After), 5xx, and network errors (max 3 attempts,
///   backoff 1s → 4s);
/// - never retry 400/401;
/// - the informational `provider` field is never used;
/// - empty content is a failure, not a valid answer (unlike search results).
/// Failures return strings starting with "Error:" — the IWebFetchService
/// convention the web_scrape tool relies on ("report, never guess").
/// </summary>
public class ProxyScrapeService : IWebFetchService, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _endpointUrl;
    private readonly Logger _logger;
    private bool _disposed;

    public ProxyScrapeService(string apiKey, string endpointUrl, Logger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Proxy scrape API key is required", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(endpointUrl))
            throw new ArgumentException("Proxy scrape endpoint URL is required (PROXY_SCRAPE_URL)", nameof(endpointUrl));

        _endpointUrl = endpointUrl;
        _logger = logger ?? new Logger();
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<string> FetchAsync(string url, int maxLength = 15000, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Error: URL is empty.";

            if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
                (target.Scheme != "http" && target.Scheme != "https"))
                return "Error: Invalid URL. Must be an absolute HTTP or HTTPS URL.";

            _logger.Log($"[proxy] Scraping '{url}' via {_endpointUrl}");

            // backoff.Length = 2 → up to 3 total attempts.
            var backoff = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4) };
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = await _client.GetAsync(
                        BuildRequestUrl(_endpointUrl, url), cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var (title, content) = ParseResponse(body);
                        var formatted = FormatResult(url, title, content);
                        _logger.Log($"[proxy] Scraped {url}: {content.Length} chars (title: {Truncate(title, 80)})");
                        return TruncateResult(formatted, maxLength);
                    }

                    var status = (int)response.StatusCode;
                    _logger.Log($"[proxy] Attempt {attempt} failed: {status} - {Truncate(body, 300)}");

                    var retryable = status == 429 || status >= 500;
                    if (!retryable || attempt > backoff.Length)
                        return $"Error: Proxy scrape failed ({status}): {Truncate(body, 300)}";

                    await Task.Delay(GetRetryAfter(response) ?? backoff[attempt - 1], cancellationToken);
                }
                catch (Exception netEx) when (netEx is HttpRequestException or TaskCanceledException)
                {
                    // TaskCanceledException covers both our own 45s timeout and
                    // hard network failures — both are retryable per contract.
                    _logger.Log($"[proxy] Attempt {attempt} network error: {netEx.Message}");
                    if (attempt > backoff.Length)
                        return $"Error: Proxy scrape failed after {attempt} attempts: {netEx.Message}";
                    await Task.Delay(backoff[attempt - 1], cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "Error: Fetch was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Log($"[proxy] Error in FetchAsync: {ex.Message}");
            return $"Error: Failed to scrape URL. {ex.Message}";
        }
    }

    /// <summary>Builds the GET URL with URL-encoded url and format=json.</summary>
    public static string BuildRequestUrl(string endpointUrl, string url)
    {
        var separator = endpointUrl.Contains('?') ? '&' : '?';
        return $"{endpointUrl}{separator}url={Uri.EscapeDataString(url)}&format=json";
    }

    /// <summary>
    /// Derives the scrape endpoint from PROXY_SEARCH_URL by replacing the
    /// trailing "/search" path segment with "/scrape" (case-insensitive,
    /// trailing slash preserved). Returns null when the URL doesn't end in
    /// /search — an explicit PROXY_SCRAPE_URL always wins.
    /// Pure/static for testability.
    /// </summary>
    public static string? DeriveScrapeUrl(string? searchUrl)
    {
        if (string.IsNullOrWhiteSpace(searchUrl))
            return null;

        var trimmed = searchUrl.Trim();
        var hadTrailingSlash = trimmed.EndsWith("/");
        var core = hadTrailingSlash ? trimmed[..^1] : trimmed;

        if (!core.EndsWith("/search", StringComparison.OrdinalIgnoreCase))
            return null;

        var derived = core[..^"/search".Length] + "/scrape";
        return hadTrailingSlash ? derived + "/" : derived;
    }

    /// <summary>
    /// Parses {"url","title","content","provider"} — the stable normalized
    /// schema. Pure/static for testability.
    /// </summary>
    public static (string title, string content) ParseResponse(string jsonResponse)
    {
        using var doc = JsonDocument.Parse(jsonResponse);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return ("", "");

        var title = GetString(root, "title");
        var content = GetString(root, "content");
        return (title, content);
    }

    /// <summary>Formats the page as markdown: bold title heading + full content.</summary>
    private static string FormatResult(string url, string title, string content)
    {
        if (string.IsNullOrEmpty(content))
            return $"Error: Scrape returned no content for {url} (possibly bot-blocked or JS-rendered).";

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine($"**{title}**");
            sb.AppendLine();
        }
        sb.Append(content);
        return sb.ToString().Trim();
    }

    private static string TruncateResult(string result, int maxLength)
    {
        if (result.Length <= maxLength)
            return result;
        return result[..maxLength] + $"\n\n... [truncated at {maxLength} characters; full page was longer]";
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
