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

public class OpenRouterProvider : BaseLLMProvider
{
    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultReferer = "https://github.com/camalolo/YAOLlm";
    private const string DefaultTitle = "YAOLlm";
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 1000;

    private readonly RestClient _client;
    private readonly string? _apiKey;

    public override string Name => "openrouter";
    public override string Model { get; protected set; }
    public override bool SupportsWebSearch => true;

    public OpenRouterProvider(string model, string? apiKey = null, TavilySearchService? searchService = null, Logger? logger = null)
        : base(new HttpClient(), searchService, logger)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("OpenRouter API key not provided. Set OPENROUTER_API_KEY environment variable or pass apiKey parameter.");

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

        return await SendWithRetryAsync(requestBody, cancellationToken);
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

    protected override async Task<ToolResult> ExecuteToolCallAsync(
        ToolCall toolCall,
        Dictionary<string, Func<string, Task<string>>>? toolHandlers,
        TavilySearchService? searchService)
    {
        var result = await RaiseOnToolCallAsync(toolCall);
        if (result != null)
        {
            LogToolResult(toolCall.Name, result.Content);
            return result;
        }

        return await base.ExecuteToolCallAsync(toolCall, toolHandlers, searchService);
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
        int retryCount = 0;

        while (true)
        {
            var (response, shouldRetry, error) = await TrySendStreamRequestAsync(requestBody, retryCount, cancellationToken);

            if (shouldRetry)
            {
                retryCount++;
                LogRetry(retryCount, MaxRetries, RetryDelayMs);
                await Task.Delay(RetryDelayMs * retryCount, cancellationToken);
                continue;
            }

            if (error != null)
            {
                throw error;
            }

            var state = new StreamingState();
            await foreach (var chunk in StreamFromResponseAsync(response!, requestBody, state, cancellationToken))
            {
                yield return chunk;
            }

            if (state.FollowUpRequest != null)
            {
                requestBody = state.FollowUpRequest;
                retryCount = 0;
                continue;
            }

            yield break;
        }
    }

    private async Task<(HttpResponseMessage? Response, bool ShouldRetry, Exception? Error)> TrySendStreamRequestAsync(
        Dictionary<string, object> requestBody,
        int currentRetryCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            httpClient.DefaultRequestHeaders.Add("HTTP-Referer", DefaultReferer);
            httpClient.DefaultRequestHeaders.Add("X-Title", DefaultTitle);

            var jsonPayload = JsonSerializer.Serialize(requestBody);
            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if ((int)response.StatusCode == 429 && currentRetryCount < MaxRetries)
                {
                    response.Dispose();
                    return (null, true, null);
                }

                var exception = LLMException.CreateWithStatusCode((int)response.StatusCode, errorContent, Name);
                response.Dispose();
                return (null, false, exception);
            }

            return (response, false, null);
        }
        catch (OperationCanceledException)
        {
            LogCancelled();
            throw;
        }
        catch (HttpRequestException) when (currentRetryCount < MaxRetries)
        {
            return (null, true, null);
        }
    }

    private async IAsyncEnumerable<string> StreamFromResponseAsync(
        HttpResponseMessage response,
        Dictionary<string, object> requestBody,
        StreamingState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (response)
        {
            var toolCalls = new Dictionary<int, ToolCallBuilder>();
            bool hasToolCalls = false;
            int chunkIndex = 0;

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

                var parseResult = TryParseStreamChunk(jsonPart);
                if (parseResult.Error != null)
                {
                    LogJsonParseError(jsonPart, parseResult.Error);
                    continue;
                }

                if (parseResult.HasToolCallsFinish)
                {
                    hasToolCalls = true;
                }

                if (!string.IsNullOrEmpty(parseResult.Chunk))
                {
                    state.FullContent.Append(parseResult.Chunk);
                    chunkIndex++;
                    LogStreamChunk(chunkIndex, parseResult.Chunk);
                    yield return parseResult.Chunk;
                }

                foreach (var tc in parseResult.ToolCallDeltas)
                {
                    if (!toolCalls.TryGetValue(tc.Index, out var builder))
                    {
                        builder = new ToolCallBuilder();
                        toolCalls[tc.Index] = builder;
                    }

                    if (!string.IsNullOrEmpty(tc.Id))
                        builder.Id = tc.Id;

                    if (!string.IsNullOrEmpty(tc.Name))
                        builder.Name = tc.Name;

                    if (!string.IsNullOrEmpty(tc.Arguments))
                        builder.Arguments += tc.Arguments;
                }
            }

            LogStreamComplete(chunkIndex, toolCalls.Count);

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
                        RaiseOnStatusChange("searching");
                    }

                    ToolResult? result = await RaiseOnToolCallAsync(toolCall);

                    if (result == null && toolCall.Name == "web_search" && _searchService != null)
                    {
                        var query = toolCall.Arguments.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : null;
                        var maxResults = toolCall.Arguments.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is int max
                            ? max
                            : 5;

                        if (string.IsNullOrEmpty(query))
                        {
                            LogError("web_search", "Missing query parameter");
                            result = new ToolResult(toolCall.Id, "Error: Missing query parameter", isError: true);
                        }
                        else
                        {
                            try
                            {
                                LogToolExecution("web_search");
                                var searchResult = await _searchService.SearchAsync(query, maxResults);
                                LogToolResult("web_search", searchResult);
                                result = new ToolResult(toolCall.Id, searchResult);
                            }
                            catch (Exception ex)
                            {
                                LogError("web_search", ex.Message);
                                result = new ToolResult(toolCall.Id, $"Error executing web search: {ex.Message}", isError: true);
                            }
                        }
                    }

                    RaiseOnStatusChange(null);

                    if (result != null)
                    {
                        var messages = (List<object>)requestBody["messages"];
                        var newMessages = new List<object>(messages);

                        newMessages.Add(new
                        {
                            role = "assistant",
                            content = state.FullContent.ToString() ?? "",
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

                        state.FollowUpRequest = new Dictionary<string, object>(requestBody)
                        {
                            ["messages"] = newMessages
                        };
                        yield break;
                    }
                }
            }
        }
    }

    private class StreamingState
    {
        public StringBuilder FullContent { get; } = new();
        public Dictionary<string, object>? FollowUpRequest { get; set; }
    }

    private class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    private class ToolCallDelta
    {
        public int Index { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }

    private class StreamChunkParseResult
    {
        public string? Error { get; set; }
        public string? Chunk { get; set; }
        public bool HasToolCallsFinish { get; set; }
        public List<ToolCallDelta> ToolCallDeltas { get; } = new();
    }

    private StreamChunkParseResult TryParseStreamChunk(string jsonPart)
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

    private Dictionary<string, object> BuildRequestBody(List<object> messages, List<ToolDefinition>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = Model,
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
                LogCancelled();
                throw;
            }

            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
            {
                LogResponse(response.Content);
                return await ProcessResponseAsync(response.Content, requestBody, cancellationToken);
            }

            if ((int)response.StatusCode == 429 && retryCount < MaxRetries)
            {
                retryCount++;
                var delay = RetryDelayMs * (int)Math.Pow(2, retryCount - 1);
                LogRetry(retryCount, MaxRetries, delay);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (!response.IsSuccessful)
            {
                var errorMessage = string.IsNullOrEmpty(response.Content)
                    ? $"Status: {response.StatusCode}"
                    : response.Content;
                LogError("SendWithRetryAsync", errorMessage);
                throw LLMException.CreateWithStatusCode((int)response.StatusCode, errorMessage, Name);
            }

            LogError("SendWithRetryAsync", "Empty response from API");
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

            LogToolCallReceived(name ?? string.Empty, args);

            var toolCallRequest = new ToolCall
            {
                Id = id ?? string.Empty,
                Name = name ?? string.Empty,
                Arguments = args
            };

            var result = await ExecuteToolCallAsync(toolCallRequest, null, _searchService);

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
}
