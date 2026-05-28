using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YAOLlm;

/// <summary>
/// Interface for fetching the text content of a URL.
/// </summary>
public interface IWebFetchService
{
    Task<string> FetchAsync(string url, int maxLength = 15000, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches a URL and extracts its text content. For HTML responses, strips tags.
/// For non-HTML (JSON, plain text), returns the raw content. Truncates to maxLength characters.
/// </summary>
public class WebFetchService : IWebFetchService
{
    private readonly HttpClient _httpClient;
    private readonly Logger _logger;

    private static readonly Regex _htmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex _whitespaceRegex = new(@"\s{3,}", RegexOptions.Compiled);
    private static readonly int MaxDownloadBytes = 2 * 1024 * 1024; // 2 MB

    public WebFetchService(HttpClient? httpClient = null, Logger? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "YAOLlm/1.0 (compatible; bot)");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain,application/json;q=0.8,*/*;q=0.7");
        _logger = logger ?? new Logger();
    }

    public async Task<string> FetchAsync(string url, int maxLength = 15000, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
                return "Error: URL is empty.";

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                return "Error: Invalid URL. Must be an absolute HTTP or HTTPS URL.";

            _logger.Log($"[WebFetch] Fetching: {url}");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                return $"Error: HTTP {statusCode} — the server returned an error.";
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/plain";
            var isHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

            // Read up to MaxDownloadBytes to avoid memory issues
            var contentBytes = await ReadLimitedAsync(response, MaxDownloadBytes, cancellationToken);
            var encoding = GetEncoding(response, contentBytes);
            var rawText = encoding.GetString(contentBytes);

            string result;
            if (isHtml)
            {
                result = HtmlToText(rawText);
            }
            else
            {
                result = rawText;
            }

            if (result.Length > maxLength)
                result = result[..maxLength] + $"\n\n... [truncated at {maxLength} characters; full page was longer]";

            _logger.Log($"[WebFetch] Fetched {url}: {result.Length} chars (type: {contentType})");
            return result;
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return "Error: Fetch was cancelled.";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out.";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Could not fetch URL — {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var totalRead = 0;
        int bytesRead;

        while (totalRead < maxBytes &&
               (bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, maxBytes - totalRead), ct)) > 0)
        {
            totalRead += bytesRead;
        }

        var result = new byte[totalRead];
        Buffer.BlockCopy(buffer, 0, result, 0, totalRead);
        return result;
    }

    private static Encoding GetEncoding(HttpResponseMessage response, byte[] contentBytes)
    {
        // Try charset from Content-Type header
        var charset = response.Content.Headers.ContentType?.CharSet;
        if (!string.IsNullOrEmpty(charset))
        {
            try { return Encoding.GetEncoding(charset); } catch { }
        }

        // Try BOM detection
        if (contentBytes.Length >= 3 && contentBytes[0] == 0xEF && contentBytes[1] == 0xBB && contentBytes[2] == 0xBF)
            return Encoding.UTF8;

        // Check for <meta charset> in first 1024 bytes
        if (contentBytes.Length > 0)
        {
            var head = Encoding.ASCII.GetString(contentBytes, 0, Math.Min(contentBytes.Length, 1024));
            var match = Regex.Match(head, @"charset=[""']?([^""';\s>]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                try { return Encoding.GetEncoding(match.Groups[1].Value); } catch { }
            }
        }

        return Encoding.UTF8;
    }

    private static string HtmlToText(string html)
    {
        // Remove script and style blocks entirely
        var text = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Convert block elements to newlines
        text = Regex.Replace(text, @"<(br|/p|/div|/h[1-6]|/li|/tr|/blockquote)[^>]*>", "\n", RegexOptions.IgnoreCase);

        // Strip remaining tags
        text = _htmlTagRegex.Replace(text, "");

        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);

        // Normalize whitespace
        text = _whitespaceRegex.Replace(text, "\n");

        return text.Trim();
    }
}
