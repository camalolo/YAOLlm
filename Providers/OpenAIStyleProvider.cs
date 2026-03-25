using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace YAOLlm.Providers;

/// <summary>
/// Abstract base class for OpenAI-compatible API providers.
/// Encapsulates shared message building, request body construction, response processing,
/// and streaming chunk parsing logic used by providers with OpenAI-style chat completions APIs.
/// </summary>
public abstract class OpenAIStyleProvider : BaseLLMProvider
{
    protected OpenAIStyleProvider(HttpClient httpClient, TavilySearchService? searchService = null, Logger? logger = null)
        : base(httpClient, searchService, logger)
    {
    }

    // ─── Template: SendAsync ───────────────────────────────────────────
    public override async Task<string> SendAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        LogRequest(history.Count, tools != null && tools.Count > 0);

        var messages = BuildMessages(history, image);
        var requestBody = BuildRequestBody(messages, tools);

        return await ExecuteSendAsync(requestBody, cancellationToken);
    }

    // ─── Template: StreamAsync ─────────────────────────────────────────
    public override async IAsyncEnumerable<string> StreamAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        LogRequest(history.Count, tools != null && tools.Count > 0);

        var messages = BuildMessages(history, image);
        var requestBody = BuildStreamingRequestBody(messages, tools);

        await foreach (var chunk in ExecuteStreamAsync(requestBody, cancellationToken))
        {
            yield return chunk;
        }
    }

    // ─── Abstract: Subclass implements actual HTTP send ────────────────
    protected abstract Task<string> ExecuteSendAsync(
        Dictionary<string, object> requestBody,
        CancellationToken cancellationToken);

    protected abstract IAsyncEnumerable<string> ExecuteStreamAsync(
        Dictionary<string, object> requestBody,
        CancellationToken cancellationToken);

    // ─── Shared: Message Building ──────────────────────────────────────
    protected List<object> BuildMessages(List<Dictionary<string, object>> history, byte[]? image)
    {
        var messages = new List<object>();

        for (int i = 0; i < history.Count; i++)
        {
            var entry = history[i];
            var role = entry.TryGetValue("role", out var roleObj) ? roleObj?.ToString() ?? "user" : "user";
            role = MapRoleToOpenAI(role);
            var content = entry.TryGetValue("content", out var contentObj) ? contentObj?.ToString() ?? "" : "";

            var isLastUserMessage = i == history.Count - 1 && role == "user" && image != null && image.Length > 0;

            if (isLastUserMessage)
            {
                var contentArray = new List<object>
                {
                    new { type = "text", text = content }
                };

                var base64Image = Convert.ToBase64String(image!);
                var mimeType = DetectImageMimeType(image!);
                contentArray.Add(new
                {
                    type = "image_url",
                    image_url = new { url = $"data:{mimeType};base64,{base64Image}" }
                });

                messages.Add(new { role, content = contentArray });
            }
            else
            {
                messages.Add(new { role, content });
            }
        }

        return messages;
    }

    // ─── Shared: Request Body Construction ─────────────────────────────
    protected Dictionary<string, object> BuildRequestBody(List<object> messages, List<ToolDefinition>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["messages"] = messages
        };

        if (tools != null && tools.Count > 0)
        {
            body["tools"] = FormatToolDefinitions(tools);
        }

        return body;
    }

    protected Dictionary<string, object> BuildStreamingRequestBody(List<object> messages, List<ToolDefinition>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["stream"] = true
        };

        if (tools != null && tools.Count > 0)
        {
            body["tools"] = FormatToolDefinitions(tools);
        }

        return body;
    }

    // ─── Shared: Non-Streaming Response Processing ─────────────────────
    protected async Task<string> ProcessResponseAsync(string responseContent, object requestBody, CancellationToken cancellationToken)
    {
        LogResponse(responseContent);

        var json = JsonNode.Parse(responseContent);

        if (json?["error"] is JsonNode errorNode)
        {
            var errorMsg = errorNode["message"]?.ToString()
                ?? errorNode.ToString();
            throw LLMException.CreateWithMessage(errorMsg, Name);
        }

        var choices = json?["choices"] as JsonArray;

        if (choices == null || choices.Count == 0)
            throw LLMException.CreateWithMessage("Invalid API response: no choices", Name);

        var choice = choices[0];
        var finishReason = choice?["finish_reason"]?.ToString();
        if (finishReason == "content_filter")
        {
            throw LLMException.CreateWithMessage("Content blocked by filter", Name);
        }
        if (finishReason == "length")
        {
            throw LLMException.CreateWithMessage("Response truncated (max tokens)", Name);
        }

        var message = choice?["message"];
        if (message == null)
            throw LLMException.CreateWithMessage("Invalid API response: no message", Name);

        var toolCalls = message["tool_calls"] as JsonArray;

        if (toolCalls != null && toolCalls.Count > 0)
        {
            return await HandleToolCallsAsync(toolCalls, requestBody, cancellationToken);
        }

        return message["content"]?.ToString() ?? string.Empty;
    }

    // ─── Shared: Tool Call Handling (non-streaming) ────────────────────
    protected async Task<string> HandleToolCallsAsync(JsonArray toolCalls, object originalRequestBody, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var requestBodyDict = originalRequestBody as Dictionary<string, object>;
        var messages = requestBodyDict?["messages"] as List<object>;

        if (messages == null)
            throw LLMException.CreateWithMessage("Tool call processing failed", Name);

        var completedToolCalls = new List<object>();

        foreach (var toolCall in toolCalls)
        {
            var id = toolCall?["id"]?.ToString();
            var function = toolCall?["function"];
            var name = function?["name"]?.ToString();
            var argsJson = function?["arguments"]?.ToString() ?? "{}";
            var args = DeserializeArguments(argsJson);

            LogToolCallReceived(name ?? string.Empty, args);

            var toolCallRequest = new ToolCall
            {
                Id = id ?? string.Empty,
                Name = name ?? string.Empty,
                Arguments = args
            };

            var result = await ExecuteToolCallAsync(toolCallRequest, null, _searchService);

            completedToolCalls.Add(new
            {
                id = id ?? string.Empty,
                type = "function",
                function = new { name = name ?? string.Empty, arguments = argsJson }
            });

            messages.Add(new
            {
                role = "tool",
                tool_call_id = id,
                name = name,
                content = result.Content
            });
        }

        messages.Insert(messages.Count - completedToolCalls.Count, new
        {
            role = "assistant",
            tool_calls = completedToolCalls
        });

        var newRequestBody = BuildRequestBody(messages, null);

        var originalTools = requestBodyDict?.TryGetValue("tools", out var toolsObj) == true
            ? toolsObj as List<object>
            : null;
        if (originalTools != null && originalTools.Count > 0)
        {
            newRequestBody["tools"] = originalTools;
        }
        ThrowIfDisposed();
        return await ExecuteSendAsync(newRequestBody, cancellationToken);
    }

    // ─── Override: Tool Call Execution with RaiseOnToolCall pattern ────
    protected override async Task<ToolResult> ExecuteToolCallAsync(
        ToolCall toolCall,
        Dictionary<string, Func<string, Task<string>>>? toolHandlers,
        TavilySearchService? searchService)
    {
        var result = await RaiseOnToolCallAsync(toolCall);
        if (result != null)
        {
            LogToolExecution(toolCall.Name);
            LogToolResult(toolCall.Name, result.Content);
            return result;
        }

        return await base.ExecuteToolCallAsync(toolCall, toolHandlers, searchService);
    }

    // ─── Shared: Streaming Chunk Parsing Infrastructure ────────────────
    protected class StreamingState
    {
        public StringBuilder FullContent { get; } = new();
        public Dictionary<string, object>? FollowUpRequest { get; set; }
    }

    protected class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    protected class ToolCallDelta
    {
        public int Index { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }

    protected class StreamChunkParseResult
    {
        public string? Error { get; set; }
        public string? Chunk { get; set; }
        public bool HasToolCallsFinish { get; set; }
        public List<ToolCallDelta> ToolCallDeltas { get; } = new();
    }

    protected StreamChunkParseResult TryParseStreamChunk(string jsonPart)
    {
        var result = new StreamChunkParseResult();

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];

                if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                    finishReason.GetString() == "tool_calls")
                {
                    result.HasToolCallsFinish = true;
                }

                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var content) &&
                        content.ValueKind != JsonValueKind.Null)
                    {
                        result.Chunk = content.GetString() ?? "";
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCallsDelta))
                    {
                        foreach (var tc in toolCallsDelta.EnumerateArray())
                        {
                            var deltaInfo = new ToolCallDelta
                            {
                                Index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0
                            };

                            if (tc.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
                                deltaInfo.Id = id.GetString() ?? "";

                            if (tc.TryGetProperty("function", out var func))
                            {
                                if (func.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null)
                                    deltaInfo.Name = name.GetString() ?? "";

                                if (func.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null)
                                    deltaInfo.Arguments = args.GetString() ?? "";
                            }

                            result.ToolCallDeltas.Add(deltaInfo);
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    // ─── Shared: Build completed ToolCall list from builders ───────────
    protected static List<ToolCall> BuildCompletedToolCalls(Dictionary<int, ToolCallBuilder> toolCalls)
    {
        return toolCalls.OrderBy(kv => kv.Key)
            .Select(kv => new ToolCall
            {
                Id = kv.Value.Id,
                Name = kv.Value.Name,
                Arguments = string.IsNullOrEmpty(kv.Value.Arguments)
                    ? new Dictionary<string, object?>()
                    : DeserializeArguments(kv.Value.Arguments)
            })
            .ToList();
    }
}
