using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    /// <summary>
    /// HTML pages extracting to less text than this are treated as unreadable
    /// (bot-blocked / login-walled / JS-rendered) rather than as a successful fetch.
    /// </summary>
    internal const int MinReadableHtmlLength = 250;

    /// <summary>
    /// Chrome-on-Windows user agent. Sites like Reddit/Akamai flag unknown
    /// UAs instantly; overridable via the WEBFETCH_USER_AGENT env var.
    /// </summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";

    public WebFetchService(HttpClient? httpClient = null, Logger? logger = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
        }
        else
        {
            // Automatic decompression makes the handler send and decode
            // Accept-Encoding: gzip/deflate/br itself — never set that
            // header manually or bodies would arrive corrupted.
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
            _httpClient = new HttpClient(handler);
        }
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        ApplyBrowserHeaders(_httpClient.DefaultRequestHeaders,
            Environment.GetEnvironmentVariable("WEBFETCH_USER_AGENT"));
        _logger = logger ?? new Logger();
    }

    /// <summary>
    /// Applies a browser-like header set so fetches look like ordinary
    /// Chrome navigation instead of a bot. Known headers (User-Agent, Accept,
    /// Accept-Language) go through the typed ParseAdd API — values added via
    /// TryAddWithoutValidation can be silently dropped from the wire for
    /// known headers. Note: header-based bot detection is only one layer —
    /// TLS (JA3) fingerprinting can still flag us on heavily protected sites,
    /// which headers cannot fix.
    /// </summary>
    internal static void ApplyBrowserHeaders(HttpRequestHeaders headers, string? userAgentOverride = null)
    {
        var ua = string.IsNullOrWhiteSpace(userAgentOverride) ? DefaultUserAgent : userAgentOverride.Trim();
        try { headers.UserAgent.ParseAdd(ua); }
        catch (FormatException) { headers.TryAddWithoutValidation("User-Agent", ua); }

        try
        {
            headers.Accept.ParseAdd(
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        }
        catch (FormatException) { }

        try { headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9"); }
        catch (FormatException) { }

        // Chrome client hints + fetch metadata, matching a fresh address-bar
        // navigation (sec-fetch-site: none, no Referer). Unknown headers are
        // always sent verbatim, so TryAddWithoutValidation is safe here.
        headers.TryAddWithoutValidation("sec-ch-ua",
            "\"Chromium\";v=\"143\", \"Google Chrome\";v=\"143\", \"Not=A?Brand\";v=\"24\"");
        headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        headers.TryAddWithoutValidation("sec-fetch-dest", "document");
        headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
        headers.TryAddWithoutValidation("sec-fetch-site", "none");
        headers.TryAddWithoutValidation("sec-fetch-user", "?1");
        headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");
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

            var result = ExtractContent(rawText, isHtml, maxLength);

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

    /// <summary>
    /// Converts raw response text into the tool result handed to the LLM:
    /// HTML-to-text extraction, a readability gate for bot-blocked pages,
    /// and length truncation. Internal + static so tests can exercise the
    /// exact pipeline without network access.
    /// </summary>
    internal static string ExtractContent(string rawText, bool isHtml, int maxLength)
    {
        var result = isHtml ? HtmlToText(rawText) : rawText;

        // Junk gate: bot-blocked, login-walled, or JS-rendered pages extract
        // to almost nothing (e.g. Reddit → "Reddit"). Returning that as a
        // successful fetch invites the model to invent the page's contents —
        // fail loudly instead.
        if (isHtml && result.Length < MinReadableHtmlLength)
        {
            var snippet = Regex.Replace(result, @"\s+", " ").Trim();
            if (snippet.Length > 80)
                snippet = snippet[..80] + "…";
            return $"Error: page returned only {result.Length} chars of readable text (\"{snippet}\"). " +
                   "It is likely bot-blocked, login-walled, or requires JavaScript. Do not guess its contents.";
        }

        if (result.Length > maxLength)
            result = result[..maxLength] + $"\n\n... [truncated at {maxLength} characters; full page was longer]";

        return result;
    }

    internal static string HtmlToText(string html)
    {
        // Comments and script/style/noscript carry no page content
        var text = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<(script|style|noscript)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Drop page chrome (nav menus, headers, footers, asides, forms) before
        // stripping tags — otherwise thousands of chars of menu boilerplate
        // bury (and get conflated with) the actual content.
        text = Regex.Replace(text, @"<(nav|header|footer|aside|svg|form|button)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Convert block elements to newlines
        text = Regex.Replace(text, @"<(br|/p|/div|/h[1-6]|/li|/tr|/td|/th|/blockquote|/section|/article|/table|/ul|/ol|/figure|/caption)[^>]*>", "\n", RegexOptions.IgnoreCase);

        // Strip remaining tags
        text = _htmlTagRegex.Replace(text, "");

        // Decode HTML entities
        text = System.Net.WebUtility.HtmlDecode(text);

        // Normalize whitespace
        text = _whitespaceRegex.Replace(text, "\n");

        return text.Trim();
    }
}
