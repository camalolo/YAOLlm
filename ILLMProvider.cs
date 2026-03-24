using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace YAOLlm;

/// <summary>
/// Interface for LLM provider implementations
/// </summary>
public interface ILLMProvider
{
    /// <summary>
    /// Provider name (e.g., "gemini", "openrouter", "ollama")
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Current model identifier
    /// </summary>
    string Model { get; }

    /// <summary>
    /// Whether this provider supports custom web search tool
    /// (Some providers like Gemini have built-in grounding and don't need our tool)
    /// </summary>
    bool SupportsWebSearch { get; }

    /// <summary>
    /// Send a conversation to the LLM and get a response
    /// </summary>
    /// <param name="history">Conversation history with role and content keys</param>
    /// <param name="image">Optional image data (PNG/JPEG bytes)</param>
    /// <param name="tools">Optional tool definitions for function calling</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>LLM response text</returns>
    Task<string> SendAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream a conversation response from the LLM chunk by chunk
    /// </summary>
    /// <param name="history">Conversation history with role and content keys</param>
    /// <param name="image">Optional image data (PNG/JPEG bytes)</param>
    /// <param name="tools">Optional tool definitions for function calling</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of response text chunks</returns>
    IAsyncEnumerable<string> StreamAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Called when a tool/function should be executed
    /// </summary>
    event Func<ToolCall, Task<ToolResult>> OnToolCall;

    /// <summary>
    /// Called when the provider status changes (e.g., "searching", "processing")
    /// </summary>
    event Action<string?>? OnStatusChange;
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
