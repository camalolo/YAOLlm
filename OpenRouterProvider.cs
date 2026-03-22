using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace GeminiDotnet;

public class OpenRouterProvider : ILLMProvider
{
    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultReferer = "https://github.com/geminidotnet";
    private const string DefaultTitle = "GeminiDotnet";
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 1000;

    private readonly RestClient _client;
    private readonly string _model;
    private readonly string? _apiKey;

    public string Name => "openrouter";
    public string Model => _model;

    public event Func<ToolCall, Task<ToolResult>>? OnToolCall;

    public OpenRouterProvider(string model, string? apiKey = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("OpenRouter API key not provided. Set OPENROUTER_API_KEY environment variable or pass apiKey parameter.");

        _client = new RestClient();
    }

    public async Task<string> SendAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        var messages = BuildMessages(history, image);
        var requestBody = BuildRequestBody(messages, tools);

        return await SendWithRetryAsync(requestBody, cancellationToken);
    }

    private List<object> BuildMessages(List<Dictionary<string, object>> history, byte[]? image)
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

    private object BuildRequestBody(List<object> messages, List<ToolDefinition>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = messages
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

    private async Task<string> SendWithRetryAsync(object requestBody, CancellationToken cancellationToken)
    {
        int retryCount = 0;

        while (true)
        {
            var request = CreateRequest(requestBody);
            var response = await _client.ExecuteAsync(request, cancellationToken);

            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
            {
                return await ProcessResponseAsync(response.Content, requestBody, cancellationToken);
            }

            if ((int)response.StatusCode == 429 && retryCount < MaxRetries)
            {
                retryCount++;
                var delay = RetryDelayMs * (int)Math.Pow(2, retryCount - 1);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (!response.IsSuccessful)
            {
                var errorMessage = string.IsNullOrEmpty(response.Content)
                    ? $"OpenRouter API error: {response.StatusCode}"
                    : $"OpenRouter API error: {response.StatusCode} - {response.Content}";
                throw new Exception(errorMessage);
            }

            throw new Exception("OpenRouter API returned empty response");
        }
    }

    private RestRequest CreateRequest(object requestBody)
    {
        var request = new RestRequest(ApiUrl, Method.Post);
        request.AddHeader("Authorization", $"Bearer {_apiKey}");
        request.AddHeader("HTTP-Referer", DefaultReferer);
        request.AddHeader("X-Title", DefaultTitle);
        request.AddHeader("Content-Type", "application/json");
        request.AddJsonBody(requestBody);
        return request;
    }

    private async Task<string> ProcessResponseAsync(string responseContent, object requestBody, CancellationToken cancellationToken)
    {
        var json = JsonNode.Parse(responseContent);
        var choices = json?["choices"] as JsonArray;

        if (choices == null || choices.Count == 0)
            throw new Exception("OpenRouter API returned no choices");

        var message = choices[0]?["message"];
        if (message == null)
            throw new Exception("OpenRouter API returned no message");

        var toolCalls = message["tool_calls"] as JsonArray;

        if (toolCalls != null && toolCalls.Count > 0)
        {
            return await HandleToolCallsAsync(toolCalls, requestBody, cancellationToken);
        }

        return message["content"]?.ToString() ?? string.Empty;
    }

    private async Task<string> HandleToolCallsAsync(JsonArray toolCalls, object originalRequestBody, CancellationToken cancellationToken)
    {
        var requestBodyDict = originalRequestBody as Dictionary<string, object>;
        var messages = requestBodyDict?["messages"] as List<object>;

        if (messages == null)
            throw new Exception("Failed to reconstruct messages for tool call continuation");

        foreach (var toolCall in toolCalls)
        {
            var id = toolCall?["id"]?.ToString();
            var function = toolCall?["function"];
            var name = function?["name"]?.ToString();
            var argsJson = function?["arguments"]?.ToString() ?? "{}";

            var toolCallRequest = new ToolCall
            {
                Id = id ?? string.Empty,
                Name = name ?? string.Empty,
                Arguments = DeserializeArguments(argsJson)
            };

            var result = await ExecuteToolCallAsync(toolCallRequest);

            messages.Add(new
            {
                role = "tool",
                tool_call_id = id,
                name = name,
                content = result.Content
            });
        }

        var newRequestBody = BuildRequestBody(messages, null);
        return await SendWithRetryAsync(newRequestBody, cancellationToken);
    }

    private async Task<ToolResult> ExecuteToolCallAsync(ToolCall toolCall)
    {
        if (OnToolCall == null)
            return new ToolResult(toolCall.Id, "No tool handler registered", isError: true);

        try
        {
            return await OnToolCall.Invoke(toolCall);
        }
        catch (Exception ex)
        {
            return new ToolResult(toolCall.Id, $"Tool execution error: {ex.Message}", isError: true);
        }
    }

    private static Dictionary<string, object?> DeserializeArguments(string argsJson)
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

    private static object ConvertJsonNodeToObject(JsonNode? node)
    {
        return node switch
        {
            JsonObject obj => obj.ToDictionary(kvp => kvp.Key, kvp => ConvertJsonNodeToObject(kvp.Value)),
            JsonArray arr => arr.Select(ConvertJsonNodeToObject).ToList(),
            JsonValue val => val.TryGetValue(out object? v) ? v ?? node.ToString() : node.ToString(),
            _ => node?.ToString() ?? string.Empty
        };
    }

    private static string MapRoleToOpenAI(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "model" => "assistant",
            _ => role
        };
    }

    private static string DetectImageMimeType(byte[] imageBytes)
    {
        if (imageBytes.Length < 4)
            return "image/jpeg";

        if (imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
            return "image/png";

        if (imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
            return "image/jpeg";

        if (imageBytes[0] == 0x47 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46)
            return "image/gif";

        if (imageBytes.Length >= 12 && imageBytes[8] == 0x57 && imageBytes[9] == 0x45 && imageBytes[10] == 0x42 && imageBytes[11] == 0x50)
            return "image/webp";

        return "image/jpeg";
    }
}
