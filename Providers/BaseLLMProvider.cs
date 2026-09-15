using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YAOLlm;

namespace YAOLlm.Providers;

/// <summary>
/// Base class for LLM provider implementations with shared utility methods,
/// retrying POST transport, SSE reading, and tool execution.
/// </summary>
public abstract class BaseLLMProvider : ILLMProvider
{
    protected const int MaxRetries = 3;

    protected readonly HttpClient _httpClient;
    protected readonly ISearchService? _searchService;
    protected readonly IWebFetchService? _webFetchService;
    protected readonly IFileReadService? _fileReadService;
    protected readonly IBrowserService? _browserService;
    protected readonly IYouTubeCaptionService? _captionService;
    protected readonly IFileWriteService? _fileWriteService;
    protected readonly Logger _logger;
    private readonly bool _ownsHttpClient;
    protected volatile bool _isDisposed;

    public string? CompletedSearchSummaries { get; protected set; }
    public int CompletedSearchCount { get; protected set; }
    public int CompletedFetchCount { get; protected set; }
    public int CompletedScrapeCount { get; protected set; }
    public int CompletedFileReadCount { get; protected set; }

    /// <summary>
    /// finish_reason from the last streamed round ("stop", "length", "tool_calls", ...).
    /// Null when the server never sent one — a sign of a cut-off stream.
    /// </summary>
    public string? LastFinishReason { get; protected set; }

    /// <summary>
    /// Whether the last stream ended cleanly: [DONE] sentinel received or an
    /// explicit finish_reason seen. False = the connection likely closed
    /// mid-generation and the response may be truncated.
    /// </summary>
    public bool LastStreamEndedCleanly { get; protected set; }

    /// <summary>
    /// Provider name (e.g., "gemini", "openrouter", "ollama")
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Current model identifier
    /// </summary>
    public abstract string Model { get; protected set; }

    /// <summary>
    /// Whether this provider supports web search/fetch tool calling
    /// </summary>
    public abstract bool SupportsWebSearch { get; }

    /// <summary>
    /// Optional completion-token cap (PRESET_N_MAX_TOKENS, fallback MAX_TOKENS).
    /// Mapped per protocol: max_tokens (OpenAI-style), maxOutputTokens (Gemini),
    /// options.num_predict (Ollama). Null = API default.
    /// </summary>
    public int? MaxTokens { get; set; }

    /// <summary>
    /// Reasoning-effort preference (PRESET_N_REASONING, fallback REASONING).
    /// Off disables thinking where the API supports it, Low requests the lowest
    /// portable effort; Default sends nothing.
    /// </summary>
    public ReasoningMode Reasoning { get; set; } = ReasoningMode.Default;

    /// <summary>
    /// Called when the provider status changes (searching, fetching, tts, ...)
    /// </summary>
    public event Action<ProviderStatus>? OnStatusChange;

    /// <summary>
    /// Initializes a new instance of the BaseLLMProvider class.
    /// A shared HttpClient can be supplied; the provider only disposes a client it created itself.
    /// </summary>
    protected BaseLLMProvider(HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null, IFileReadService? fileReadService = null, IBrowserService? browserService = null, IYouTubeCaptionService? captionService = null, IFileWriteService? fileWriteService = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttpClient = httpClient == null;
        _searchService = searchService;
        _webFetchService = webFetchService;
        _fileReadService = fileReadService;
        _browserService = browserService;
        _captionService = captionService;
        _fileWriteService = fileWriteService;
        _logger = logger ?? new Logger();
    }

    /// <summary>
    /// Stream a conversation response from the LLM chunk by chunk
    /// </summary>
    public abstract IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raises the OnStatusChange event.
    /// </summary>
    protected virtual void RaiseOnStatusChange(ProviderStatus status)
    {
        OnStatusChange?.Invoke(status);
    }

    // ─── Shared HTTP transport ────────────────────────────────────────

    /// <summary>
    /// Override to add provider-specific headers (auth, referer, ...) to each request.
    /// </summary>
    protected virtual void CustomizeRequest(HttpRequestMessage request) { }

    /// <summary>
    /// POSTs a JSON payload with exponential-backoff retries on transient errors
    /// (429/503, network failures, timeouts). Returns a successful response
    /// (caller must dispose) or throws <see cref="LLMException"/>.
    /// </summary>
    protected async Task<HttpResponseMessage> PostWithRetryAsync(string url, string jsonPayload, CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                CustomizeRequest(request);

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    response.Dispose();
                    throw LLMException.CreateWithStatusCode(statusCode, errorBody, Name);
                }

                return response;
            }
            catch (OperationCanceledException)
            {
                LogCancelled();
                throw;
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt))
            {
                var delay = GetRetryDelay(attempt);
                LogRetry(attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
                attempt++;
            }
        }
    }

    /// <summary>
    /// Reads an SSE stream ("data: ..." lines) from a response, yielding each data
    /// payload including the final "[DONE]" sentinel — consumers use it to tell
    /// a clean stream end from a silently-closed connection. Caller owns (disposes) the response.
    /// </summary>
    protected static async IAsyncEnumerable<string> ReadSseDataLinesAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            yield return line["data: ".Length..];
        }
    }

    // ─── Shared tool execution ────────────────────────────────────────

    /// <summary>
    /// Executes a web_search tool call and updates the Completed* counters/summaries.
    /// </summary>
    protected async Task<ToolResult> ExecuteWebSearchToolAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = toolCall.Arguments.TryGetValue("query", out var q) ? q?.ToString() : null;
            if (string.IsNullOrEmpty(query))
            {
                LogError("web_search", "Missing query parameter");
                return new ToolResult(toolCall.Id, "Error: Missing query parameter", isError: true);
            }

            var maxResults = GetIntArg(toolCall.Arguments, "max_results", 5);

            LogToolExecution("web_search");
            var searchResult = await _searchService!.SearchAsync(query, maxResults);
            LogToolResult("web_search", searchResult);

            CompletedSearchCount++;
            CompletedSearchSummaries = (CompletedSearchSummaries != null ? CompletedSearchSummaries + "\n\n---\n\n" : "")
                + $"**Search: {query}**\n{searchResult}";

            return new ToolResult(toolCall.Id, searchResult);
        }
        catch (Exception ex)
        {
            LogError("web_search", ex.Message);
            return new ToolResult(toolCall.Id, $"Error executing web search: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Executes a web_scrape tool call and updates the Completed* counters.
    /// </summary>
    protected async Task<ToolResult> ExecuteWebScrapeToolAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = toolCall.Arguments.TryGetValue("url", out var u) ? u?.ToString() : null;
            if (string.IsNullOrEmpty(url))
            {
                LogError("web_scrape", "Missing url parameter");
                return new ToolResult(toolCall.Id, "Error: Missing url parameter", isError: true);
            }

            LogToolExecution("web_scrape");

            // 24h in-process cache: a session re-scrapes the same guide pages
            // across turns, and each fetch otherwise re-hits the scrape API.
            var cacheKey = ScrapeCache.NormalizeKey(url);
            if (ScrapeCache.TryGet(cacheKey, out var cachedResult))
            {
                _logger.Log($"[scrape-cache] hit ({cachedResult.Length} chars): {url}");
                CompletedScrapeCount++;
                return new ToolResult(toolCall.Id, cachedResult);
            }

            var scrapeResult = await _webFetchService!.FetchAsync(url, cancellationToken: cancellationToken);
            // Full scrape results can be tens of KB of page text — truncate in
            // the log (the LLM still gets the full content) to keep yaollm.log readable.
            LogToolResult("web_scrape", scrapeResult, maxLength: 500);

            // Only successes are cached — "Error:" results (dead pages,
            // bot-blocks, proxy failures) must retry live on the next call.
            if (!scrapeResult.StartsWith("Error:"))
                ScrapeCache.Store(cacheKey, scrapeResult);

            CompletedScrapeCount++;

            return new ToolResult(toolCall.Id, scrapeResult);
        }
        catch (Exception ex)
        {
            LogError("web_scrape", ex.Message);
            return new ToolResult(toolCall.Id, $"Error scraping URL: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Returns the TTS text if this tool call is a tts_summary call, otherwise null.
    /// </summary>
    protected static string? ExtractTtsText(ToolCall toolCall)
    {
        if (toolCall.Name != "tts_summary")
            return null;
        return toolCall.Arguments.TryGetValue("text", out var textObj) ? textObj?.ToString() : null;
    }

    /// <summary>
    /// Executes a file_read tool call (read-only local file access).
    /// </summary>
    protected async Task<ToolResult> ExecuteFileReadToolAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = toolCall.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
            if (string.IsNullOrEmpty(path))
            {
                LogError("file_read", "Missing path parameter");
                return new ToolResult(toolCall.Id, "Error: Missing path parameter", isError: true);
            }

            var startLine = GetIntArg(toolCall.Arguments, "start_line", 0);
            var endLine = GetIntArg(toolCall.Arguments, "end_line", 0);

            LogToolExecution("file_read");
            var content = await _fileReadService!.ReadFileAsync(path, startLine, endLine, cancellationToken: cancellationToken);
            // Full file contents can be large — truncate in the log (the LLM
            // still gets the full content) to keep yaollm.log readable.
            LogToolResult("file_read", content, maxLength: 500);

            CompletedFileReadCount++;

            return new ToolResult(toolCall.Id, content);
        }
        catch (Exception ex)
        {
            LogError("file_read", ex.Message);
            return new ToolResult(toolCall.Id, $"Error reading file: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Executes a list_files tool call (directory listing of user-approved paths).
    /// </summary>
    protected async Task<ToolResult> ExecuteFileListToolAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = toolCall.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
            if (string.IsNullOrEmpty(path))
            {
                LogError("list_files", "Missing path parameter");
                return new ToolResult(toolCall.Id, "Error: Missing path parameter", isError: true);
            }

            LogToolExecution("list_files");
            var content = await _fileReadService!.ListFilesAsync(path, cancellationToken);
            // Listings can be long — truncate in the log (the LLM still gets
            // the full content) to keep yaollm.log readable.
            LogToolResult("list_files", content, maxLength: 500);

            return new ToolResult(toolCall.Id, content);
        }
        catch (Exception ex)
        {
            LogError("list_files", ex.Message);
            return new ToolResult(toolCall.Id, $"Error listing directory: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Executes a browse_* tool call via the Playwright MCP bridge. Returns
    /// the page snapshot / action result as text.
    /// </summary>
    protected async Task<ToolResult> ExecuteBrowseToolAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            // Status label: the URL for navigations, otherwise the tool name —
            // MainForm surfaces URLs as an in-chat system line.
            var detail = toolCall.Arguments.TryGetValue("url", out var u) ? u?.ToString() : null;
            if (string.IsNullOrEmpty(detail))
                detail = toolCall.Name["browse_".Length..].Replace('_', ' ');
            RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.Browsing, detail));

            var argumentsJson = JsonSerializer.Serialize(toolCall.Arguments);
            var content = await _browserService!.InvokeAsync(toolCall.Name, argumentsJson, cancellationToken);
            // Snapshots/pages can be large — truncate in the log (the LLM
            // still gets the full content) to keep yaollm.log readable.
            LogToolResult(toolCall.Name, content, maxLength: 500);

            return new ToolResult(toolCall.Id, content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(toolCall.Name, ex.Message);
            return new ToolResult(toolCall.Id, $"Error browsing: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Executes a youtube_captions tool call (transcript extraction via yt-dlp).
    /// </summary>
    protected async Task<ToolResult> ExecuteYouTubeCaptionsToolAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = toolCall.Arguments.TryGetValue("url", out var u) ? u?.ToString() : null;
            if (string.IsNullOrEmpty(url))
            {
                LogError("youtube_captions", "Missing url parameter");
                return new ToolResult(toolCall.Id, "Error: Missing url parameter", isError: true);
            }

            var language = toolCall.Arguments.TryGetValue("language", out var l) ? l?.ToString() : null;
            var timestamps = GetBoolArg(toolCall.Arguments, "timestamps", false);

            LogToolExecution("youtube_captions");
            RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.Captions, url));
            var content = await _captionService!.GetCaptionsAsync(url, language, timestamps, cancellationToken);
            // Transcripts can be tens of KB — truncate in the log (the LLM
            // still gets the full content) to keep yaollm.log readable.
            LogToolResult("youtube_captions", content, maxLength: 500);

            return new ToolResult(toolCall.Id, content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError("youtube_captions", ex.Message);
            return new ToolResult(toolCall.Id, $"Error fetching captions: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Executes a file_write tool call (writes restricted to the temp area).
    /// </summary>
    protected async Task<ToolResult> ExecuteFileWriteToolAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = toolCall.Arguments.TryGetValue("path", out var p) ? p?.ToString() : null;
            var content = toolCall.Arguments.TryGetValue("content", out var c) ? c?.ToString() : null;
            if (string.IsNullOrEmpty(path) || content == null)
            {
                LogError("file_write", "Missing path/content parameter");
                return new ToolResult(toolCall.Id, "Error: Missing path or content parameter", isError: true);
            }

            var append = GetBoolArg(toolCall.Arguments, "append", false);

            LogToolExecution("file_write");
            RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.WritingFile, path));
            var result = await _fileWriteService!.WriteAsync(path, content, append, cancellationToken);
            LogToolResult("file_write", result, maxLength: 300);

            return new ToolResult(toolCall.Id, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogError("file_write", ex.Message);
            return new ToolResult(toolCall.Id, $"Error writing file: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Executes a memory_write tool call (per-game memory file edit).
    /// </summary>
    protected async Task<ToolResult> ExecuteMemoryWriteToolAsync(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        try
        {
            var content = toolCall.Arguments.TryGetValue("content", out var c) ? c?.ToString() : null;
            if (content == null)
            {
                LogError("memory_write", "Missing content parameter");
                return new ToolResult(toolCall.Id, "Error: Missing content parameter", isError: true);
            }

            var op = toolCall.Arguments.TryGetValue("op", out var o) ? o?.ToString() ?? "append" : "append";
            var find = toolCall.Arguments.TryGetValue("find", out var f) ? f?.ToString() : null;
            var game = toolCall.Arguments.TryGetValue("game", out var g) ? g?.ToString() : null;

            LogToolExecution("memory_write");
            RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.WritingFile,
                game != null ? $"memory[{game}]" : _fileWriteService!.CurrentMemoryPath));
            var result = await _fileWriteService!.WriteMemoryAsync(content, op, find, game, cancellationToken);
            LogToolResult("memory_write", result, maxLength: 300);

            return new ToolResult(toolCall.Id, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogError("memory_write", ex.Message);
            return new ToolResult(toolCall.Id, $"Error updating memory: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Reads a boolean tool argument robustly across boxed types
    /// (bool/string/JsonElement).
    /// </summary>
    protected static bool GetBoolArg(Dictionary<string, object?> args, string key, bool defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } s =>
                bool.TryParse(s.GetString(), out var parsed) ? parsed : defaultValue,
            _ => bool.TryParse(value.ToString(), out var coerced) ? coerced : defaultValue,
        };
    }

    /// <summary>
    /// Reads an integer tool argument robustly across boxed numeric types
    /// (int/long/double/JsonElement).
    /// </summary>
    protected static int GetIntArg(Dictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        // JsonValue.TryGetValue boxes JSON numbers as JsonElement
        if (value is JsonElement { ValueKind: JsonValueKind.Number } element && element.TryGetInt32(out var number))
            return number;

        try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return defaultValue; }
    }

    // ─── Shared conversion helpers ────────────────────────────────────

    /// <summary>
    /// Detects the MIME type of image data based on byte patterns.
    /// </summary>
    protected static string DetectImageMimeType(byte[] imageData)
    {
        if (imageData.Length < 4)
            return "image/jpeg";

        if (imageData[0] == 0x89 && imageData[1] == 0x50 && imageData[2] == 0x4E && imageData[3] == 0x47)
            return "image/png";

        if (imageData[0] == 0xFF && imageData[1] == 0xD8)
            return "image/jpeg";

        if (imageData[0] == 0x47 && imageData[1] == 0x49 && imageData[2] == 0x46)
            return "image/gif";

        if (imageData.Length >= 12 && imageData[8] == 0x57 && imageData[9] == 0x45 && imageData[10] == 0x42 && imageData[11] == 0x50)
            return "image/webp";

        return "image/jpeg";
    }

    /// <summary>
    /// Maps role names to OpenAI-compatible format.
    /// </summary>
    protected static string MapRoleToOpenAI(ChatRole role)
    {
        return role switch
        {
            ChatRole.Model => "assistant",
            _ => role.ToApiString()
        };
    }

    /// <summary>
    /// Deserializes JSON arguments string to a dictionary.
    /// </summary>
    protected static Dictionary<string, object?> DeserializeArguments(string argsJson)
    {
        try
        {
            var result = new Dictionary<string, object?>();
            var json = JsonNode.Parse(argsJson);

            if (json is JsonObject obj)
            {
                foreach (var kvp in obj)
                {
                    result[kvp.Key] = ConvertJsonNodeToObject(kvp.Value!);
                }
            }

            return result;
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// Converts a JsonNode to the appropriate C# type.
    /// </summary>
    protected static object ConvertJsonNodeToObject(JsonNode? node)
    {
        return node switch
        {
            JsonObject obj => obj.ToDictionary(kvp => kvp.Key, kvp => ConvertJsonNodeToObject(kvp.Value)),
            JsonArray arr => arr.Select(ConvertJsonNodeToObject).ToList(),
            JsonValue val => val.TryGetValue(out object? v) ? v ?? node.ToString() : node.ToString(),
            _ => node?.ToString() ?? string.Empty
        };
    }

    /// <summary>
    /// Formats a list of tool definitions into the OpenAI-compatible structure
    /// required by OpenAI-style API endpoints.
    /// </summary>
    protected static List<object> FormatToolDefinitions(List<ToolDefinition> tools)
    {
        return tools.Select(t => (object)new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.Parameters
            }
        }).ToList();
    }

    public virtual void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    protected void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(Name);
    }

    #region Protected Logging Methods

    protected void LogRequest(int messageCount, bool hasTools)
    {
        _logger.Log($"[{Name}] Request: model={Model}, messages={messageCount}, tools={hasTools.ToString().ToLower()}");
    }

    protected void LogResponse(string response, int maxLength = 500)
    {
        var trimmed = response.TrimStart();
        var truncated = trimmed.Length > maxLength ? trimmed.Substring(0, maxLength) + "..." : trimmed;
        _logger.Log($"[{Name}] Response: {truncated}");
    }

    protected void LogRetry(int attempt, int maxAttempts, int delayMs)
    {
        _logger.Log($"[{Name}] Retry: attempt {attempt}/{maxAttempts}, waiting {delayMs}ms");
    }

    protected static bool ShouldRetry(Exception ex, int attempt, int maxRetries = MaxRetries)
    {
        if (attempt >= maxRetries) return false;
        return ex switch
        {
            LLMException llm when llm.StatusCode == 429 || llm.StatusCode == 503 => true,
            HttpRequestException => true,
            TaskCanceledException tc when tc.InnerException is TimeoutException => true,
            _ => false
        };
    }

    protected static TimeSpan GetRetryDelay(int attempt, TimeSpan? baseDelay = null)
    {
        var delay = baseDelay ?? TimeSpan.FromSeconds(1);
        return TimeSpan.FromMilliseconds(delay.TotalMilliseconds * Math.Pow(2, attempt));
    }

    protected void LogToolCallReceived(string toolName, Dictionary<string, object?> args)
    {
        var argsJson = JsonSerializer.Serialize(args);
        _logger.Log($"[{Name}] Tool call: {toolName}({argsJson})");
    }

    protected void LogToolExecution(string toolName)
    {
        _logger.Log($"[{Name}] Tool executing: {toolName}");
    }

    protected void LogToolResult(string toolName, string result, int maxLength = 0)
    {
        var truncated = maxLength > 0 && result.Length > maxLength ? result.Substring(0, maxLength) + "..." : result;
        _logger.Log($"[{Name}] Tool result: {toolName} -> \"{truncated}\"");
    }

    protected void LogError(string operation, string error)
    {
        _logger.Log($"[{Name}] Error in {operation}: {error}");
    }

    protected void LogCancelled()
    {
        _logger.Log($"[{Name}] Request cancelled");
    }

    protected void LogJsonParseError(string rawContent, string error, int maxLength = 100)
    {
        var truncated = rawContent.Length > maxLength ? rawContent.Substring(0, maxLength) + "..." : rawContent;
        _logger.Log($"[{Name}] JSON parse error: {error} in content: {truncated}");
    }

    protected void LogStreamChunk(int chunkIndex)
    {
        if (chunkIndex == 1)
            _logger.Log($"[{Name}] Stream started");
        else if (chunkIndex % 50 == 0)
            _logger.Log($"[{Name}] Stream chunk #{chunkIndex}");
    }

    protected void LogStreamComplete(int totalChunks, int toolCallCount)
    {
        _logger.Log($"[{Name}] Stream complete: {totalChunks} chunks, {toolCallCount} tool calls");
    }

    #endregion
}
