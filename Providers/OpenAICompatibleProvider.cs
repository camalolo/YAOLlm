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

public class OpenAICompatibleProvider : OpenAIStyleProvider
{
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
    }

    protected override async IAsyncEnumerable<string> ExecuteStreamAsync(
        Dictionary<string, object> requestBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        const int maxRetries = 3;

        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            request?.Dispose();

            try
            {
                var jsonPayload = JsonSerializer.Serialize(requestBody);
                request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions");
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!resp.IsSuccessStatusCode)
                {
                    var errorContent = await resp.Content.ReadAsStringAsync(cancellationToken);
                    var ex = LLMException.CreateWithStatusCode((int)resp.StatusCode, errorContent, Name);
                    resp.Dispose();

                    if (ShouldRetry(ex, attempt, maxRetries))
                    {
                        var delay = GetRetryDelay(attempt);
                        LogRetry(attempt + 1, maxRetries, (int)delay.TotalMilliseconds);
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }

                    request.Dispose();
                    throw ex;
                }

                response = resp;
                break;
            }
            catch (OperationCanceledException)
            {
                request?.Dispose();
                LogCancelled();
                throw;
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt, maxRetries))
            {
                var delay = GetRetryDelay(attempt);
                LogRetry(attempt + 1, maxRetries, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        using (request!)
        using (response)
        {
            using var stream = await response!.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            var fullContent = new StringBuilder();
            var toolCalls = new Dictionary<int, ToolCallBuilder>();
            bool hasToolCalls = false;
            int chunkIndex = 0;

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
                    fullContent.Append(parseResult.Chunk);
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

                    ToolResult? toolResult = null;

                    if (toolResult == null && toolCall.Name == "web_search" && _searchService != null)
                    {
                        toolResult = await ExecuteWebSearchFallbackAsync(toolCall);
                    }

                    RaiseOnStatusChange(null);

                    if (toolResult != null)
                    {
                        var messages = (List<object>)requestBody["messages"];
                        var newMessages = new List<object>(messages);

                        newMessages.Add(new
                        {
                            role = "assistant",
                            content = fullContent.Length > 0 ? fullContent.ToString() : null,
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
                            content = toolResult.Content
                        });

                        requestBody["messages"] = newMessages;

                        ThrowIfDisposed();
                        await foreach (var chunk in ExecuteStreamAsync(requestBody, cancellationToken))
                        {
                            yield return chunk;
                        }
                        yield break;
                    }
                }
            }
        }
    }

    private async Task<ToolResult> ExecuteWebSearchFallbackAsync(ToolCall toolCall)
    {
        try
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
                return new ToolResult(toolCall.Id, "Error: Missing query parameter", isError: true);
            }

            RaiseOnStatusChange(StatusManager.SearchingStatus);
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

    public override void Dispose()
    {
        base.Dispose();
    }
}
