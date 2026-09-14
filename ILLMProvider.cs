using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace YAOLlm;

/// <summary>
/// Interface for LLM provider implementations
/// </summary>
public interface ILLMProvider : IDisposable
{
    /// <summary>
    /// Protocol profile name (e.g., "gemini", "openai-compatible", "deepseek",
    /// "ollama") — used in log lines and exception context, not for UI display.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Current model identifier
    /// </summary>
    string Model { get; }

    /// <summary>
    /// Whether this provider supports web search / fetch tool calling.
    /// When false, tool definitions are omitted from requests (e.g. Ollama).
    /// </summary>
    bool SupportsWebSearch { get; }

    /// <summary>
    /// Stream a conversation response from the LLM chunk by chunk
    /// </summary>
    /// <param name="history">Conversation history</param>
    /// <param name="image">Optional image data (PNG/JPEG bytes)</param>
    /// <param name="tools">Optional tool definitions for function calling</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of response text chunks</returns>
    IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when the provider status changes (e.g. searching, fetching, tts)
    /// </summary>
    event Action<ProviderStatus>? OnStatusChange;

    string? CompletedSearchSummaries { get; }
    int CompletedSearchCount { get; }
    int CompletedScrapeCount { get; }

    /// <summary>
    /// finish_reason from the last streamed round ("stop", "length", ...);
    /// null when the server never sent one (possible sign of a cut-off stream).
    /// </summary>
    string? LastFinishReason { get; }

    /// <summary>
    /// Whether the last stream ended cleanly ([DONE]/finish_reason/done flag
    /// received). False = the connection likely closed mid-generation and the
    /// response may be truncated.
    /// </summary>
    bool LastStreamEndedCleanly { get; }
}

/// <summary>
/// Definition of a tool/function that the LLM can call
/// </summary>
public class ToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object? Parameters { get; set; }

    public ToolDefinition(string name, string description, object? parameters = null)
    {
        Name = name;
        Description = description;
        Parameters = parameters;
    }
}

/// <summary>
/// Represents a tool call request from the LLM
/// </summary>
public class ToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; set; } = new();
}

/// <summary>
/// Result of executing a tool
/// </summary>
public class ToolResult
{
    public string ToolCallId { get; set; }
    public string Content { get; set; }
    public bool IsError { get; set; }

    public ToolResult(string toolCallId, string content, bool isError = false)
    {
        ToolCallId = toolCallId;
        Content = content;
        IsError = isError;
    }
}
