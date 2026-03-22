using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace GeminiDotnet;

public class OllamaProvider : ILLMProvider
{
    private readonly string _baseUrl;
    private readonly RestClient _client;

    public string Name => "ollama";
    public string Model { get; }

    public event Func<ToolCall, Task<ToolResult>>? OnToolCall;

    public OllamaProvider(string model, string? baseUrl = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _baseUrl = baseUrl 
            ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") 
            ?? "http://localhost:11434";
        _client = new RestClient(_baseUrl);
    }

    public async Task<string> SendAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(history, image);
        var payload = BuildPayload(messages, tools);

        var response = await SendRequestAsync(payload, cancellationToken);
        
        if (response == null)
            return string.Empty;

        return await ProcessResponseAsync(response.Value, messages, tools, cancellationToken);
    }

    private List<object> BuildMessages(List<Dictionary<string, object>> history, byte[]? image)
    {
        var messages = new List<object>();

        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            var role = msg.TryGetValue("role", out var roleObj) ? roleObj?.ToString() ?? "user" : "user";
            var content = msg.TryGetValue("content", out var contentObj) ? contentObj?.ToString() ?? "" : "";

            if (i == history.Count - 1 && image != null && role == "user")
            {
                var imageBase64 = Convert.ToBase64String(image);
                messages.Add(new { role, content, images = new[] { imageBase64 } });
            }
            else
            {
                messages.Add(new { role, content });
            }
        }

        return messages;
    }

    private object BuildPayload(List<object> messages, List<ToolDefinition>? tools)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["stream"] = false
        };

        if (tools != null && tools.Count > 0)
        {
            payload["tools"] = tools.Select(t => new
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

        return payload;
    }

    private async Task<JsonElement?> SendRequestAsync(object payload, CancellationToken cancellationToken)
    {
        var request = new RestRequest("/api/chat", Method.Post);
        request.AddHeader("Content-Type", "application/json");
        request.AddJsonBody(payload);

        try
        {
            var response = await _client.ExecuteAsync(request, cancellationToken);

            if (!response.IsSuccessful)
            {
                if (response.ErrorException != null)
                {
                    Console.Error.WriteLine($"Ollama request failed: {response.ErrorException.Message}");
                }
                else
                {
                    Console.Error.WriteLine($"Ollama request failed with status: {response.StatusCode}");
                }
                return null;
            }

            if (string.IsNullOrEmpty(response.Content))
            {
                Console.Error.WriteLine("Ollama returned empty response");
                return null;
            }

            return JsonSerializer.Deserialize<JsonElement>(response.Content);
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("Ollama request was cancelled");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ollama request error: {ex.Message}");
            return null;
        }
    }

    private async Task<string> ProcessResponseAsync(
        JsonElement response,
        List<object> originalMessages,
        List<ToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        if (!response.TryGetProperty("message", out var message))
        {
            Console.Error.WriteLine("No message in Ollama response");
            return string.Empty;
        }

        var textContent = message.TryGetProperty("content", out var content) 
            ? content.GetString() ?? "" 
            : "";

        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                var result = await HandleToolCallAsync(toolCall);
                if (result != null)
                {
                    var continuationResponse = await SendToolResultAsync(
                        originalMessages, 
                        message, 
                        result, 
                        tools, 
                        cancellationToken);
                    
                    if (continuationResponse != null)
                    {
                        return await ProcessResponseAsync(continuationResponse.Value, originalMessages, tools, cancellationToken);
                    }
                }
            }
        }

        return textContent;
    }

    private async Task<ToolResult?> HandleToolCallAsync(JsonElement toolCall)
    {
        if (OnToolCall == null)
        {
            Console.Error.WriteLine("Tool call received but no handler attached");
            return null;
        }

        if (!toolCall.TryGetProperty("function", out var function))
        {
            Console.Error.WriteLine("Tool call missing function property");
            return null;
        }

        var name = function.TryGetProperty("name", out var nameElement) 
            ? nameElement.GetString() ?? ""
            : "";

        var callId = toolCall.TryGetProperty("id", out var idElement)
            ? idElement.GetString() ?? Guid.NewGuid().ToString()
            : Guid.NewGuid().ToString();

        var arguments = new Dictionary<string, object?>();
        if (function.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsElement.EnumerateObject())
            {
                arguments[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.ToString()
                };
            }
        }

        var call = new ToolCall
        {
            Id = callId,
            Name = name,
            Arguments = arguments
        };

        try
        {
            return await OnToolCall(call);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Tool call handler error: {ex.Message}");
            return new ToolResult(callId, ex.Message, isError: true);
        }
    }

    private async Task<JsonElement?> SendToolResultAsync(
        List<object> originalMessages,
        JsonElement assistantMessage,
        ToolResult toolResult,
        List<ToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        var messages = new List<object>(originalMessages);

        var assistantContent = assistantMessage.TryGetProperty("content", out var content)
            ? content.GetString() ?? ""
            : "";
        
        var assistantMsg = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = assistantContent };
        
        if (assistantMessage.TryGetProperty("tool_calls", out var toolCalls))
        {
            assistantMsg["tool_calls"] = JsonSerializer.Deserialize<object?>(toolCalls.GetRawText());
        }
        messages.Add(assistantMsg);

        var toolMsg = new Dictionary<string, object>
        {
            ["role"] = "tool",
            ["content"] = toolResult.IsError 
                ? $"Error: {toolResult.Content}" 
                : toolResult.Content
        };
        messages.Add(toolMsg);

        var payload = BuildPayload(messages, tools);
        return await SendRequestAsync(payload, cancellationToken);
    }
}
