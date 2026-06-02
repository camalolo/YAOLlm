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

    protected virtual string ChatCompletionsPath => "/v1/chat/completions";

    public OpenAICompatibleProvider(string model, string baseUrl = "http://localhost:11434", HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null)
        : base(httpClient ?? new HttpClient(), searchService, webFetchService, logger)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _baseUrl = baseUrl.TrimEnd('/');
    }

    protected override async IAsyncEnumerable<string> ExecuteStreamAsync(
        Dictionary<string, object> requestBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ResetDsmlBuffer();
        const int maxRetries = 3;

        HttpResponseMessage? response = null;
        HttpRequestMessage? request = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            request?.Dispose();

            try
            {
                var jsonPayload = JsonSerializer.Serialize(requestBody);
                request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{ChatCompletionsPath}");
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
            var fullReasoning = new StringBuilder();
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

                if (!string.IsNullOrEmpty(parseResult.ReasoningChunk))
                {
                    fullReasoning.Append(parseResult.ReasoningChunk);
                }

                if (!string.IsNullOrEmpty(parseResult.Chunk))
                {
                    fullContent.Append(parseResult.Chunk);
                    chunkIndex++;
                    LogStreamChunk(chunkIndex);
                    var filtered = FilterDsmlChunk(parseResult.Chunk);
                    if (filtered.Length > 0)
                        yield return filtered;
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
                var toolResults = new List<ToolResult>();
                var ttsText = (string?)null;

                // Separate tts_summary from other tool calls
                var otherToolCalls = new List<ToolCall>();
                foreach (var toolCall in completedToolCalls)
                {
                    if (toolCall.Name == "tts_summary")
                    {
                        ttsText = toolCall.Arguments.TryGetValue("text", out var textObj) ? textObj?.ToString() : null;
                    }
                    else
                    {
                        otherToolCalls.Add(toolCall);
                    }
                }

                // Only raise TTS if tts_summary is the sole tool call (final response)
                // Skip TTS if it came alongside search/fetch to avoid premature playback
                if (!string.IsNullOrEmpty(ttsText) && otherToolCalls.Count == 0)
                    RaiseOnStatusChange($"{StatusManager.TtsStatus}:{ttsText}");

                // Process remaining (non-TTS) tool calls
                foreach (var toolCall in otherToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (toolCall.Name == "web_search" && _searchService != null)
                    {
                        var query = toolCall.Arguments.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : null;
                        if (!string.IsNullOrEmpty(query))
                            RaiseOnStatusChange($"{StatusManager.SearchingStatus}:{_searchService.Name}:{query}");
                        var result = await ExecuteWebSearchFallbackAsync(toolCall);
                        toolResults.Add(result);
                        CompletedSearchCount++;
                        if (!string.IsNullOrEmpty(query))
                            CompletedSearchSummaries = (CompletedSearchSummaries != null ? CompletedSearchSummaries + "\n\n---\n\n" : "") + $"**Search: {query}**\n{result.Content}";
                    }
                    else if (toolCall.Name == "web_fetch" && _webFetchService != null)
                    {
                        var fetchUrl = toolCall.Arguments.TryGetValue("url", out var urlObj) ? urlObj?.ToString() : null;
                        if (!string.IsNullOrEmpty(fetchUrl))
                            RaiseOnStatusChange($"{StatusManager.FetchingStatus}:{fetchUrl}");
                        var result = await ExecuteWebFetchAsync(toolCall, cancellationToken);
                        toolResults.Add(result);
                        CompletedFetchCount++;
                    }
                    else
                    {
                        toolResults.Add(new ToolResult(toolCall.Id, $"Unknown tool: {toolCall.Name}", isError: true));
                    }
                }

                if (otherToolCalls.Count > 0)
                    RaiseOnStatusChange(null);

                if (toolResults.Count > 0)
                {
                    var messages = (List<object>)requestBody["messages"];
                    var newMessages = new List<object>(messages);

                    newMessages.Add(new
                    {
                        role = "assistant",
                        content = fullContent.Length > 0 ? StripDsmlTags(fullContent.ToString()) : (string?)null,
                        reasoning_content = fullReasoning.Length > 0 ? fullReasoning.ToString() : (string?)null,
                        tool_calls = otherToolCalls.Select(tc => new
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

                    foreach (var tr in toolResults)
                    {
                        newMessages.Add(new
                        {
                            role = "tool",
                            tool_call_id = tr.ToolCallId,
                            content = tr.Content
                        });
                    }

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

            LogToolExecution("web_search");
            var searchResult = await _searchService!.SearchAsync(query, maxResults);
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

    private async Task<ToolResult> ExecuteWebFetchAsync(ToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            var url = toolCall.Arguments.TryGetValue("url", out var urlObj) ? urlObj?.ToString() : null;

            if (string.IsNullOrEmpty(url))
            {
                LogError("web_fetch", "Missing url parameter");
                return new ToolResult(toolCall.Id, "Error: Missing url parameter", isError: true);
            }

            LogToolExecution("web_fetch");
            var fetchResult = await _webFetchService!.FetchAsync(url, cancellationToken: cancellationToken);
            LogToolResult("web_fetch", fetchResult);
            return new ToolResult(toolCall.Id, fetchResult);
        }
        catch (Exception ex)
        {
            RaiseOnStatusChange(null);
            LogError("web_fetch", ex.Message);
            return new ToolResult(toolCall.Id, $"Error fetching URL: {ex.Message}", isError: true);
        }
    }
}
