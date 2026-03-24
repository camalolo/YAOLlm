using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace YAOLlm;

public class OpenRouterProvider : ILLMProvider
{
    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultReferer = "https://github.com/camalolo/YAOLlm";
    private const string DefaultTitle = "YAOLlm";
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 1000;

    private readonly RestClient _client;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly Logger _logger;
    private readonly TavilySearchService? _searchService;

    public string Name => "openrouter";
    public string Model => _model;
    public bool SupportsWebSearch => true;

    public event Func<ToolCall, Task<ToolResult>>? OnToolCall;
    public event Action<string?>? OnStatusChange;

    public OpenRouterProvider(string model, string? apiKey = null, TavilySearchService? searchService = null, Logger? logger = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        _logger = logger ?? new Logger();
        _searchService = searchService;

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

        ProviderLogger.LogRequest(_logger, Name, _model, history.Count, tools != null && tools.Count > 0);

        var messages = BuildMessages(history, image);
        var requestBody = BuildRequestBody(messages, tools);

        return await SendWithRetryAsync(requestBody, cancellationToken);
    }

    public async IAsyncEnumerable<string> StreamAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        ProviderLogger.LogRequest(_logger, Name, _model, history.Count, tools != null && tools.Count > 0);

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
            ["model"] = _model,
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
        int retryCount = 0;

        while (true)
        {
            StreamResult? result = null;
            try
            {
                result = await ExecuteStreamRequestAsync(requestBody, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ProviderLogger.LogCancelled(_logger, Name);
                throw;
            }
            catch (HttpRequestException) when (retryCount < MaxRetries)
            {
                retryCount++;
                ProviderLogger.LogRetry(_logger, Name, retryCount, MaxRetries, RetryDelayMs);
                await Task.Delay(RetryDelayMs * retryCount, cancellationToken);
                continue;
            }

            if (result == null)
            {
                yield break;
            }

            if (result.Retry)
            {
                retryCount++;
                ProviderLogger.LogRetry(_logger, Name, retryCount, MaxRetries, RetryDelayMs);
                await Task.Delay(RetryDelayMs * retryCount, cancellationToken);
                continue;
            }

            foreach (var chunk in result.Chunks)
            {
                yield return chunk;
            }

            if (result.FollowUpRequest != null)
            {
                await foreach (var chunk in StreamWithRetryAsync(result.FollowUpRequest, cancellationToken))
                {
                    yield return chunk;
                }
            }

            yield break;
        }
    }

    private async Task<StreamResult?> ExecuteStreamRequestAsync(
        Dictionary<string, object> requestBody,
        CancellationToken cancellationToken)
    {
        var chunks = new List<string>();
        var fullContent = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallBuilder>();
        bool hasToolCalls = false;

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        httpClient.DefaultRequestHeaders.Add("HTTP-Referer", DefaultReferer);
        httpClient.DefaultRequestHeaders.Add("X-Title", DefaultTitle);

        var jsonPayload = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if ((int)response.StatusCode == 429)
            {
                return new StreamResult { Retry = true };
            }

            throw LLMException.CreateWithStatusCode((int)response.StatusCode, errorContent, Name);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var jsonPart = line.Substring(6);
            if (jsonPart == "[DONE]")
                break;

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
                        hasToolCalls = true;
                    }

                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var content) &&
                            content.ValueKind != JsonValueKind.Null)
                        {
                            var chunk = content.GetString() ?? "";
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                fullContent.Append(chunk);
                                chunks.Add(chunk);
                            }
                        }

                        if (delta.TryGetProperty("tool_calls", out var toolCallsDelta))
                        {
                            foreach (var tc in toolCallsDelta.EnumerateArray())
                            {
                                var index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

                                if (!toolCalls.TryGetValue(index, out var builder))
                                {
                                    builder = new ToolCallBuilder();
                                    toolCalls[index] = builder;
                                }

                                if (tc.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
                                    builder.Id = id.GetString() ?? "";

                                if (tc.TryGetProperty("function", out var func))
                                {
                                    if (func.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null)
                                        builder.Name = name.GetString() ?? "";

                                    if (func.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null)
                                        builder.Arguments += args.GetString() ?? "";
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (hasToolCalls && toolCalls.Count > 0)
        {
            var completedToolCalls = toolCalls.OrderBy(kv => kv.Key)
                .Select(kv => new ToolCall
                {
                    Id = kv.Value.Id,
                    Name = kv.Value.Name,
                    Arguments = string.IsNullOrEmpty(kv.Value.Arguments)
                        ? new Dictionary<string, object?>()
                        : JsonSerializer.Deserialize<Dictionary<string, object?>>(kv.Value.Arguments)
                          ?? new Dictionary<string, object?>()
                })
                .ToList();

            foreach (var toolCall in completedToolCalls)
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
                else if (toolCall.Name == "web_search" && _searchService != null)
                {
                    result = await ExecuteWebSearchAsync(toolCall);
                }

                OnStatusChange?.Invoke(null);

                if (result != null)
                {
                    var messages = (List<object>)requestBody["messages"];
                    var newMessages = new List<object>(messages);

                    newMessages.Add(new
                    {
                        role = "assistant",
                        content = fullContent.ToString() ?? "",
                        tool_calls = completedToolCalls.Select(tc => new
                        {
                            id = tc.Id,
                            type = "function",
                            function = new
                            {
                                name = tc.Name,
                                arguments = tc.Arguments.Count > 0
                                    ? JsonSerializer.Serialize(tc.Arguments)
                                    : "{}"
                            }
                        }).ToArray()
                    });

                    newMessages.Add(new
                    {
                        role = "tool",
                        tool_call_id = toolCall.Id,
                        content = result.Content
                    });

                    requestBody["messages"] = newMessages;

                    return new StreamResult
                    {
                        Chunks = chunks,
                        FollowUpRequest = requestBody
                    };
                }
            }
        }

        return new StreamResult { Chunks = chunks };
    }

    private class StreamResult
    {
        public List<string> Chunks { get; set; } = new();
        public bool Retry { get; set; }
        public Dictionary<string, object>? FollowUpRequest { get; set; }
    }

    private class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
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
            RestResponse response;
            try
            {
                response = await _client.ExecuteAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ProviderLogger.LogCancelled(_logger, Name);
                throw;
            }

            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
            {
                ProviderLogger.LogResponse(_logger, Name, response.Content);
                return await ProcessResponseAsync(response.Content, requestBody, cancellationToken);
            }

            if ((int)response.StatusCode == 429 && retryCount < MaxRetries)
            {
                retryCount++;
                var delay = RetryDelayMs * (int)Math.Pow(2, retryCount - 1);
                ProviderLogger.LogRetry(_logger, Name, retryCount, MaxRetries, delay);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (!response.IsSuccessful)
            {
                var errorMessage = string.IsNullOrEmpty(response.Content)
                    ? $"Status: {response.StatusCode}"
                    : response.Content;
                ProviderLogger.LogError(_logger, Name, "SendWithRetryAsync", errorMessage);
                throw LLMException.CreateWithStatusCode((int)response.StatusCode, errorMessage, Name);
            }

            ProviderLogger.LogError(_logger, Name, "SendWithRetryAsync", "Empty response from API");
            throw LLMException.CreateWithMessage("Empty response from API", Name);
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

    private async Task<string> HandleToolCallsAsync(JsonArray toolCalls, object originalRequestBody, CancellationToken cancellationToken)
    {
        var requestBodyDict = originalRequestBody as Dictionary<string, object>;
        var messages = requestBodyDict?["messages"] as List<object>;

        if (messages == null)
            throw LLMException.CreateWithMessage("Tool call processing failed", Name);

        foreach (var toolCall in toolCalls)
        {
            var id = toolCall?["id"]?.ToString();
            var function = toolCall?["function"];
            var name = function?["name"]?.ToString();
            var argsJson = function?["arguments"]?.ToString() ?? "{}";
            var args = DeserializeArguments(argsJson);

            ProviderLogger.LogToolCallReceived(_logger, Name, name ?? string.Empty, args);

            var toolCallRequest = new ToolCall
            {
                Id = id ?? string.Empty,
                Name = name ?? string.Empty,
                Arguments = args
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
        if (OnToolCall != null)
        {
            try
            {
                ProviderLogger.LogToolExecution(_logger, Name, toolCall.Name);
                var result = await OnToolCall.Invoke(toolCall);
                ProviderLogger.LogToolResult(_logger, Name, toolCall.Name, result.Content);
                return result;
            }
            catch (Exception ex)
            {
                ProviderLogger.LogError(_logger, Name, "ExecuteToolCallAsync", ex.Message);
                return new ToolResult(toolCall.Id, $"Tool execution error: {ex.Message}", isError: true);
            }
        }

        if (toolCall.Name == "web_search" && _searchService != null)
        {
            return await ExecuteWebSearchAsync(toolCall);
        }

        ProviderLogger.LogError(_logger, Name, "ExecuteToolCallAsync", $"No handler for tool: {toolCall.Name}");
        return new ToolResult(toolCall.Id, $"No handler for tool: {toolCall.Name}", isError: true);
    }

    private async Task<ToolResult> ExecuteWebSearchAsync(ToolCall toolCall)
    {
        try
        {
            var query = toolCall.Arguments.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : null;
            var maxResults = toolCall.Arguments.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is int max
                ? max
                : 5;

            if (string.IsNullOrEmpty(query))
            {
                ProviderLogger.LogError(_logger, Name, "web_search", "Missing query parameter");
                return new ToolResult(toolCall.Id, "Error: Missing query parameter", isError: true);
            }

            OnStatusChange?.Invoke("searching");
            ProviderLogger.LogToolExecution(_logger, Name, "web_search");
            var result = await _searchService!.SearchAsync(query, maxResults);
            OnStatusChange?.Invoke(null);
            ProviderLogger.LogToolResult(_logger, Name, "web_search", result);
            return new ToolResult(toolCall.Id, result);
        }
        catch (Exception ex)
        {
            ProviderLogger.LogError(_logger, Name, "web_search", ex.Message);
            return new ToolResult(toolCall.Id, $"Error executing web search: {ex.Message}", isError: true);
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
