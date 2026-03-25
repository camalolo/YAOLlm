using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
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
    /// Called when a tool/function should be executed
    /// </summary>
    public event Func<ToolCall, Task<ToolResult>>? OnToolCall;

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
    /// Send a conversation to the LLM and get a response
    /// </summary>
    public abstract Task<string> SendAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream a conversation response from the LLM chunk by chunk
    /// </summary>
    public abstract IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Raises the OnToolCall event.
    /// </summary>
    /// <param name="toolCall">The tool call to process</param>
    /// <returns>The tool result, or null if no handler is attached</returns>
    protected virtual async Task<ToolResult?> RaiseOnToolCallAsync(ToolCall toolCall)
    {
        if (OnToolCall != null)
        {
            return await OnToolCall.Invoke(toolCall);
        }
        return null;
    }

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
    protected static string MapRoleToOpenAI(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "model" => "assistant",
            _ => role
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

    /// <summary>
    /// Executes a tool call using provided handlers or web search fallback.
    /// </summary>
    /// <param name="toolCall">The tool call to execute</param>
    /// <param name="toolHandlers">Optional dictionary of tool name to handler functions</param>
    /// <param name="searchService">Optional search service for web_search fallback</param>
    /// <returns>The tool result</returns>
    protected virtual async Task<ToolResult> ExecuteToolCallAsync(
        ToolCall toolCall,
        Dictionary<string, Func<string, Task<string>>>? toolHandlers,
        TavilySearchService? searchService)
    {
        if (toolHandlers != null && toolHandlers.TryGetValue(toolCall.Name, out var handler))
        {
            try
            {
                var argsJson = JsonSerializer.Serialize(toolCall.Arguments);
                var result = await handler(argsJson);
                return new ToolResult(toolCall.Id, result);
            }
            catch (Exception ex)
            {
                return new ToolResult(toolCall.Id, $"Tool execution error: {ex.Message}", isError: true);
            }
        }

        if (toolCall.Name == "web_search" && searchService != null)
        {
            var query = toolCall.Arguments.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : null;
            int maxResults;
            if (toolCall.Arguments.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is long l && l >= 0)
                maxResults = (int)l;
            else
                maxResults = 5;

            if (string.IsNullOrEmpty(query))
            {
                return new ToolResult(toolCall.Id, "Error: Missing query parameter", isError: true);
            }

            try
            {
                RaiseOnStatusChange("searching");
                var result = await searchService.SearchAsync(query, maxResults);
                RaiseOnStatusChange(null);
                return new ToolResult(toolCall.Id, result);
            }
            catch (Exception ex)
            {
                RaiseOnStatusChange(null);
                return new ToolResult(toolCall.Id, $"Error executing web search: {ex.Message}", isError: true);
            }
        }

        return new ToolResult(toolCall.Id, $"No handler for tool: {toolCall.Name}", isError: true);
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
        ProviderLogger.LogRequest(_logger, Name, Model, messageCount, hasTools);
    }

    protected void LogResponse(string response, int maxLength = 500)
    {
        ProviderLogger.LogResponse(_logger, Name, response, maxLength);
    }

    protected void LogRetry(int attempt, int maxAttempts, int delayMs)
    {
        ProviderLogger.LogRetry(_logger, Name, attempt, maxAttempts, delayMs);
    }

    protected void LogToolCallReceived(string toolName, Dictionary<string, object?> args)
    {
        ProviderLogger.LogToolCallReceived(_logger, Name, toolName, args);
    }

    protected void LogToolExecution(string toolName)
    {
        ProviderLogger.LogToolExecution(_logger, Name, toolName);
    }

    protected void LogToolResult(string toolName, string result, int maxLength = 200)
    {
        ProviderLogger.LogToolResult(_logger, Name, toolName, result, maxLength);
    }

    protected void LogError(string operation, string error)
    {
        ProviderLogger.LogError(_logger, Name, operation, error);
    }

    protected void LogCancelled()
    {
        ProviderLogger.LogCancelled(_logger, Name);
    }

    protected void LogJsonParseError(string rawContent, string error, int maxLength = 100)
    {
        ProviderLogger.LogJsonParseError(_logger, Name, rawContent, error, maxLength);
    }

    protected void LogStreamChunk(int chunkIndex, string content, int maxLength = 50)
    {
        ProviderLogger.LogStreamChunk(_logger, Name, chunkIndex, content, maxLength);
    }

    protected void LogStreamComplete(int totalChunks, int toolCallCount)
    {
        ProviderLogger.LogStreamComplete(_logger, Name, totalChunks, toolCallCount);
    }

    #endregion
}
