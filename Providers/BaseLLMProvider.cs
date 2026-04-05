using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace YAOLlm.Providers;

/// <summary>
/// Base class for LLM provider implementations with shared utility methods.
/// </summary>
public abstract class BaseLLMProvider : ILLMProvider
{
    protected readonly HttpClient _httpClient;
    protected readonly TavilySearchService? _searchService;
    protected readonly Logger _logger;
    protected volatile bool _isDisposed;

    /// <summary>
    /// Provider name (e.g., "gemini", "openrouter", "ollama")
    /// </summary>
    public abstract string Name { get; }

    /// <summary>
    /// Current model identifier
    /// </summary>
    public abstract string Model { get; protected set; }

    /// <summary>
    /// Whether this provider supports custom web search tool
    /// </summary>
    public abstract bool SupportsWebSearch { get; }

    /// <summary>
    /// Called when the provider status changes (e.g., "searching", "processing")
    /// </summary>
    public event Action<string?>? OnStatusChange;

    /// <summary>
    /// Initializes a new instance of the BaseLLMProvider class.
    /// </summary>
    /// <param name="httpClient">HTTP client for making requests</param>
    /// <param name="searchService">Optional Tavily search service for web search functionality</param>
    /// <param name="logger">Optional logger for provider operations</param>
    protected BaseLLMProvider(HttpClient httpClient, TavilySearchService? searchService = null, Logger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _searchService = searchService;
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
    /// <param name="status">The new status, or null to clear</param>
    protected virtual void RaiseOnStatusChange(string? status)
    {
        OnStatusChange?.Invoke(status);
    }

    /// <summary>
    /// Detects the MIME type of image data based on byte patterns.
    /// </summary>
    /// <param name="imageData">The image byte data</param>
    /// <returns>The detected MIME type string</returns>
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
    /// <param name="role">The role to map</param>
    /// <returns>The OpenAI-compatible role name</returns>
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
    /// <param name="argsJson">JSON string containing arguments</param>
    /// <returns>Dictionary of argument names to values</returns>
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
    /// <param name="node">The JSON node to convert</param>
    /// <returns>The converted object</returns>
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
    /// <param name="tools">The tool definitions to format</param>
    /// <returns>List of formatted tool objects</returns>
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

    protected static bool ShouldRetry(Exception ex, int attempt, int maxRetries = 3)
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

    protected void LogToolResult(string toolName, string result, int maxLength = 200)
    {
        var truncated = result.Length > maxLength ? result.Substring(0, maxLength) + "..." : result;
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

    protected void LogStreamChunk(int chunkIndex, string content, int maxLength = 50)
    {
        var truncated = content.Length > maxLength ? content.Substring(0, maxLength) + "..." : content;
        _logger.Log($"[{Name}] Stream chunk #{chunkIndex}: \"{truncated}\"");
    }

    protected void LogStreamComplete(int totalChunks, int toolCallCount)
    {
        _logger.Log($"[{Name}] Stream complete: {totalChunks} chunks, {toolCallCount} tool calls");
    }

    #endregion
}
