using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YAOLlm.Providers;

public class OpenRouterProvider : OpenAIStyleProvider
{
    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultReferer = "https://github.com/camalolo/YAOLlm";
    private const string DefaultTitle = "YAOLlm";
    private const int MaxRetries = 3;

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

        if (_httpClient.DefaultRequestHeaders.Authorization == null)
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
            _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", DefaultReferer);
            _httpClient.DefaultRequestHeaders.Add("X-Title", DefaultTitle);
        }
    }

    protected override async IAsyncEnumerable<string> ExecuteStreamAsync(
        Dictionary<string, object> requestBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in StreamWithRetryAsync(requestBody, cancellationToken))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<string> StreamWithRetryAsync(
        Dictionary<string, object> requestBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        int retryCount = 0;

        while (true)
        {
            var (response, shouldRetry, error) = await TrySendStreamRequestAsync(requestBody, retryCount, cancellationToken);

            if (shouldRetry)
            {
                var delay = GetRetryDelay(retryCount);
                LogRetry(retryCount + 1, MaxRetries, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
                retryCount++;
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
                ThrowIfDisposed();
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
            var jsonPayload = JsonSerializer.Serialize(requestBody);
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                var exception = LLMException.CreateWithStatusCode((int)response.StatusCode, errorContent, Name);
                response.Dispose();
                return (null, ShouldRetry(exception, currentRetryCount, MaxRetries), exception);
            }

            return (response, false, null);
        }
        catch (OperationCanceledException)
        {
            LogCancelled();
            throw;
        }
        catch (Exception ex)
        {
            return (null, ShouldRetry(ex, currentRetryCount, MaxRetries), ex);
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
                    continue;
                if (!line.StartsWith("data: "))
                    continue;

                var jsonPart = line.Substring(6);
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
                var completedToolCalls = BuildCompletedToolCalls(toolCalls);

                foreach (var toolCall in completedToolCalls)
                {
                    if (toolCall.Name == "web_search")
                    {
                        RaiseOnStatusChange(StatusManager.SearchingStatus);
                    }

                    ToolResult? result = null;
                    if (toolCall.Name == "web_search" && _searchService != null)
                    {
                        var query = toolCall.Arguments.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : null;
                        int maxResults;
                        if (toolCall.Arguments.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is long l && l >= 0)
                            maxResults = (int)l;
                        else
                            maxResults = 5;

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
                            content = state.FullContent.Length > 0 ? state.FullContent.ToString() : null,
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
}
