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

namespace YAOLlm.Providers;

public class OpenAICompatibleProvider : BaseLLMProvider
{
    private readonly RestClient _client;
    private readonly string _apiUrl;
    private readonly string _baseUrl;
    private string _model;

    public override string Name => "openai-compatible";
    public override string Model { get => _model; protected set => _model = value; }
    public override bool SupportsWebSearch => true;

    public OpenAICompatibleProvider(string model, string baseUrl = "http://localhost:11434", HttpClient? httpClient = null, TavilySearchService? searchService = null, Logger? logger = null)
        : base(httpClient ?? new HttpClient(), searchService, logger)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _baseUrl = baseUrl.TrimEnd('/');
        _apiUrl = $"{_baseUrl}/v1/chat/completions";
        _client = new RestClient();
    }

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

        return await SendAsync(requestBody, cancellationToken);
    }

    public override async IAsyncEnumerable<string> StreamAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        LogRequest(history.Count, tools != null && tools.Count > 0);

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
        const int maxRetries = 3;
        const int retryDelayMs = 1000;

        while (true)
        {
            StreamResult? result = null;
            Exception? caughtException = null;

            try
            {
                result = await ProcessStreamAsync(requestBody, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                LogCancelled();
                throw;
            }
            catch (HttpRequestException ex) when (retryCount < maxRetries)
            {
                caughtException = ex;
            }
            catch (LLMException ex) when (ex.StatusCode == 429 && retryCount < maxRetries)
            {
                caughtException = ex;
            }

            if (caughtException != null)
            {
                retryCount++;
                LogRetry(retryCount, maxRetries, retryDelayMs);
                await Task.Delay(retryDelayMs * retryCount, cancellationToken);
                continue;
            }

            if (result != null)
            {
                foreach (var chunk in result.Chunks)
                {
                    yield return chunk;
                }

                if (result.ToolCalls != null && result.ToolCalls.Count > 0 && result.HasToolCalls)
                {
                    foreach (var toolCall in result.ToolCalls)
                    {
                        if (toolCall.Name == "web_search")
                        {
                            RaiseOnStatusChange("searching");
                        }

                        ToolResult? toolResult = await RaiseOnToolCallAsync(toolCall);

                        if (toolResult == null && toolCall.Name == "web_search" && _searchService != null)
                        {
                            toolResult = await ExecuteWebSearchToolAsync(toolCall);
                        }

                        RaiseOnStatusChange(null);

                        if (toolResult != null)
                        {
                            var messages = (List<object>)requestBody["messages"];
                            var newMessages = new List<object>(messages);

                            newMessages.Add(new
                            {
                                role = "assistant",
                                content = result.FullContent ?? "",
                                tool_calls = result.ToolCalls.Select(tc => new
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
                                content = toolResult.Content
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

            yield break;
        }
    }

    private async Task<StreamResult> ProcessStreamAsync(
        Dictionary<string, object> requestBody,
        CancellationToken cancellationToken)
    {
        var chunks = new List<string>();
        var fullContent = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallBuilder>();
        bool hasToolCalls = false;
        int chunkIndex = 0;

        var jsonPayload = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions");
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw LLMException.CreateWithStatusCode((int)response.StatusCode, errorContent, Name);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                LogSseLineSkipped("empty line");
                continue;
            }
            if (!line.StartsWith("data: "))
            {
                LogSseLineSkipped($"no data prefix: {line.Substring(0, Math.Min(20, line.Length))}");
                continue;
            }

            var jsonPart = line.Substring(6);
            LogSseLineReceived(jsonPart);

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
                                chunkIndex++;
                                LogStreamChunk(chunkIndex, chunk);
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
            catch (JsonException ex)
            {
                LogJsonParseError(jsonPart, ex.Message);
            }
        }

        LogStreamComplete(chunks.Count, toolCalls.Count);

        var completedToolCalls = toolCalls.Count > 0
            ? toolCalls.OrderBy(kv => kv.Key)
                .Select(kv => new ToolCall
                {
                    Id = kv.Value.Id,
                    Name = kv.Value.Name,
                    Arguments = string.IsNullOrEmpty(kv.Value.Arguments)
                        ? new Dictionary<string, object?>()
                        : DeserializeArguments(kv.Value.Arguments)
                })
                .ToList()
            : null;

        return new StreamResult
        {
            Chunks = chunks,
            FullContent = fullContent.ToString(),
            ToolCalls = completedToolCalls,
            HasToolCalls = hasToolCalls
        };
    }

    private class StreamResult
    {
        public List<string> Chunks { get; set; } = new();
        public string? FullContent { get; set; }
        public List<ToolCall>? ToolCalls { get; set; }
        public bool HasToolCalls { get; set; }
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

    private async Task<string> SendAsync(object requestBody, CancellationToken cancellationToken)
    {
        var request = CreateRequest(requestBody);

        try
        {
            var response = await _client.ExecuteAsync(request, cancellationToken);

            if (!response.IsSuccessful)
            {
                var errorMessage = string.IsNullOrEmpty(response.Content)
                    ? $"Status: {response.StatusCode}"
                    : response.Content;
                LogError("SendAsync", errorMessage);
                throw LLMException.CreateWithStatusCode((int)response.StatusCode, errorMessage, Name);
            }

            if (string.IsNullOrEmpty(response.Content))
            {
                LogError("SendAsync", "Empty response");
                throw LLMException.CreateWithMessage("Empty response from API", Name);
            }

            return await ProcessResponseAsync(response.Content, requestBody, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogCancelled();
            throw;
        }
    }

    private RestRequest CreateRequest(object requestBody)
    {
        var request = new RestRequest(_apiUrl, Method.Post);
        request.AddHeader("Content-Type", "application/json");
        request.AddJsonBody(requestBody);
        return request;
    }

    private async Task<string> ProcessResponseAsync(string responseContent, object requestBody, CancellationToken cancellationToken)
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

            var toolCallRequest = new ToolCall
            {
                Id = id ?? string.Empty,
                Name = name ?? string.Empty,
                Arguments = DeserializeArguments(argsJson)
            };

            LogToolCallReceived(toolCallRequest.Name, toolCallRequest.Arguments);

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
        return await SendAsync(newRequestBody, cancellationToken);
    }

    private async Task<ToolResult> ExecuteToolCallAsync(ToolCall toolCall)
    {
        var result = await RaiseOnToolCallAsync(toolCall);

        if (result != null)
        {
            LogToolExecution(toolCall.Name);
            LogToolResult(toolCall.Name, result.Content);
            return result;
        }

        if (toolCall.Name == "web_search" && _searchService != null)
        {
            return await ExecuteWebSearchToolAsync(toolCall);
        }

        LogError("ExecuteToolCallAsync", $"No handler for tool: {toolCall.Name}");
        return new ToolResult(toolCall.Id, $"No handler for tool: {toolCall.Name}", isError: true);
    }

    private async Task<ToolResult> ExecuteWebSearchToolAsync(ToolCall toolCall)
    {
        try
        {
            var query = toolCall.Arguments.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : null;
            var maxResults = toolCall.Arguments.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is int max
                ? max
                : 5;

            if (string.IsNullOrEmpty(query))
            {
                LogError("web_search", "Missing query parameter");
                return new ToolResult(toolCall.Id, "Error: Missing query parameter", isError: true);
            }

            RaiseOnStatusChange("searching");
            LogToolExecution("web_search");
            var searchResult = await _searchService!.SearchAsync(query, maxResults);
            RaiseOnStatusChange(null);
            LogToolResult("web_search", searchResult);
            return new ToolResult(toolCall.Id, searchResult);
        }
        catch (Exception ex)
        {
            RaiseOnStatusChange(null);
            LogError("web_search", ex.Message);
            return new ToolResult(toolCall.Id, $"Error executing web search: {ex.Message}", isError: true);
        }
    }
}
