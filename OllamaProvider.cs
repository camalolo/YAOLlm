using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace YAOLlm;

public class OllamaProvider : ILLMProvider
{
    private readonly string _baseUrl;
    private readonly RestClient _client;
    private readonly Logger _logger;

    public string Name => "ollama";
    public string Model { get; }
    public bool SupportsWebSearch => false;

    public event Func<ToolCall, Task<ToolResult>>? OnToolCall;
    public event Action<string?>? OnStatusChange;

    public OllamaProvider(string model, string? baseUrl = null, Logger? logger = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _baseUrl = baseUrl 
            ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") 
            ?? "http://localhost:11434";
        _client = new RestClient(_baseUrl);
        _logger = logger ?? new Logger();
    }

    public async Task<string> SendAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(history, image);
        var payload = BuildPayload(messages, tools);

        ProviderLogger.LogRequest(_logger, Name, Model, history.Count, tools != null);

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
                ProviderLogger.LogError(_logger, Name, "SendRequestAsync", $"Status: {response.StatusCode}");
                throw LLMException.CreateWithStatusCode((int)response.StatusCode, response.Content, Name);
            }

            if (string.IsNullOrEmpty(response.Content))
            {
                ProviderLogger.LogError(_logger, Name, "SendRequestAsync", "Empty response");
                throw LLMException.CreateWithMessage("Empty response from server", Name);
            }

            ProviderLogger.LogResponse(_logger, Name, response.Content);
            var responseJson = JsonSerializer.Deserialize<JsonElement>(response.Content);
            
            if (responseJson.TryGetProperty("error", out var errorProp))
            {
                var errorMsg = errorProp.GetString() ?? "Unknown error";
                throw LLMException.CreateWithMessage(errorMsg, Name);
            }
            
            return responseJson;
        }
        catch (TaskCanceledException)
        {
            ProviderLogger.LogCancelled(_logger, Name);
            throw;
        }
        catch (Exception ex)
        {
            ProviderLogger.LogError(_logger, Name, "SendRequestAsync", ex.Message);
            throw LLMException.CreateWithMessage(ex.Message, Name);
        }
    }

    private async Task<string> ProcessResponseAsync(
        JsonElement response,
        List<object> originalMessages,
        List<ToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        if (response.TryGetProperty("error", out var errorProp))
        {
            var errorMsg = errorProp.GetString() ?? "Unknown error";
            throw LLMException.CreateWithMessage(errorMsg, Name);
        }

        if (!response.TryGetProperty("message", out var message))
        {
            ProviderLogger.LogError(_logger, Name, "ProcessResponse", "No message in response");
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
            ProviderLogger.LogError(_logger, Name, "HandleToolCall", "No handler attached");
            return null;
        }

        if (!toolCall.TryGetProperty("function", out var function))
        {
            ProviderLogger.LogError(_logger, Name, "HandleToolCall", "Missing function property");
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

        ProviderLogger.LogToolCallReceived(_logger, Name, name, arguments);
        var call = new ToolCall
        {
            Id = callId,
            Name = name,
            Arguments = arguments
        };

        try
        {
            ProviderLogger.LogToolExecution(_logger, Name, name);
            var result = await OnToolCall(call);
            ProviderLogger.LogToolResult(_logger, Name, name, result.Content);
            return result;
        }
        catch (Exception ex)
        {
            ProviderLogger.LogError(_logger, Name, "HandleToolCall", $"Handler error: {ex.Message}");
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

    public async IAsyncEnumerable<string> StreamAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        ProviderLogger.LogRequest(_logger, Name, Model, history.Count, tools != null && tools.Count > 0);

        var messages = BuildMessages(history, image);
        var requestBody = BuildStreamingRequestBody(messages, tools);

        await foreach (var chunk in StreamWithRetryAsync(requestBody, cancellationToken))
        {
            yield return chunk;
        }
    }

    private Dictionary<string, object> BuildStreamingRequestBody(List<object> messages, List<ToolDefinition>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["stream"] = true
        };

        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new
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

        return body;
    }

    private async IAsyncEnumerable<string> StreamWithRetryAsync(
        Dictionary<string, object> requestBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fullContent = new StringBuilder();
        var pendingToolCalls = new List<ToolCall>();
        var chunks = new List<string>();
        
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(5);
        
        var jsonPayload = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat");
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
                
            var (chunk, toolCalls) = ParseStreamLine(line, fullContent, pendingToolCalls);
            if (chunk != null)
                yield return chunk;
        }
        
        if (pendingToolCalls.Count > 0)
        {
            foreach (var toolCall in pendingToolCalls)
            {
                if (toolCall.Name == "web_search")
                {
                    OnStatusChange?.Invoke("searching");
                }
                
                ToolResult? result = null;
                if (OnToolCall != null)
                {
                    result = await OnToolCall.Invoke(toolCall);
                }
                
                OnStatusChange?.Invoke(null);
                
                if (result != null)
                {
                    var messages = (List<object>)requestBody["messages"];
                    var newMessages = new List<object>(messages);
                    
                    newMessages.Add(new
                    {
                        role = "assistant",
                        content = fullContent.ToString(),
                        tool_calls = pendingToolCalls.Select(tc => new
                        {
                            function = new
                            {
                                name = tc.Name,
                                arguments = tc.Arguments
                            }
                        }).ToArray()
                    });
                    
                    newMessages.Add(new
                    {
                        role = "tool",
                        content = result.Content
                    });
                    
                    requestBody["messages"] = newMessages;
                    
                    await foreach (var chunk in StreamWithRetryAsync(requestBody, cancellationToken))
                    {
                        yield return chunk;
                    }
                    yield break;
                }
            }
        }
    }

    private (string? chunk, List<ToolCall>? toolCalls) ParseStreamLine(string line, StringBuilder fullContent, List<ToolCall> pendingToolCalls)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            
            string? chunk = null;
            
            if (root.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind != JsonValueKind.Null)
            {
                var chunkText = content.GetString() ?? "";
                if (!string.IsNullOrEmpty(chunkText))
                {
                    fullContent.Append(chunkText);
                    chunk = chunkText;
                }
            }
            
            if (root.TryGetProperty("message", out var msgForTools) &&
                msgForTools.TryGetProperty("tool_calls", out var toolCallsArr))
            {
                foreach (var tc in toolCallsArr.EnumerateArray())
                {
                    var toolCall = new ToolCall();
                    
                    if (tc.TryGetProperty("function", out var func))
                    {
                        toolCall.Name = func.TryGetProperty("name", out var name) 
                            ? name.GetString() ?? "" 
                            : "";
                        toolCall.Id = Guid.NewGuid().ToString();
                        
                        if (func.TryGetProperty("arguments", out var args))
                        {
                            toolCall.Arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(args.ToString())
                                ?? new Dictionary<string, object?>();
                        }
                    }
                    
                    pendingToolCalls.Add(toolCall);
                }
            }
            
            return (chunk, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
