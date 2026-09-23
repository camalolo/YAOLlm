using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace YAOLlm.Providers;

/// <summary>
/// Base class for OpenAI-compatible chat-completions providers.
/// Implements the full streaming template: request building, SSE parsing,
/// DSML filtering, tool-call handling with follow-up rounds, and TTS extraction.
/// Subclasses only supply the endpoint URL and (optionally) auth headers.
/// </summary>
public abstract class OpenAIStyleProvider : BaseLLMProvider
{
    private string _dsmlBuffer = "";

    protected OpenAIStyleProvider(HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null, IFileReadService? fileReadService = null, IBrowserService? browserService = null, IYouTubeCaptionService? captionService = null, IFileWriteService? fileWriteService = null)
        : base(httpClient, searchService, webFetchService, logger, fileReadService, browserService, captionService, fileWriteService)
    {
    }

    /// <summary>
    /// Full URL of the streaming chat-completions endpoint.
    /// </summary>
    protected abstract string StreamUrl { get; }

    // ─── Template: StreamAsync ─────────────────────────────────────────
    public override async IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        LogRequest(history.Count, tools != null && tools.Count > 0);
        CompletedSearchCount = 0;
        CompletedFetchCount = 0;
        CompletedFileReadCount = 0;
        CompletedSearchSummaries = null;
        LastFinishReason = null;
        LastStreamEndedCleanly = false;

        var messages = BuildMessages(history, image);
        var requestBody = BuildStreamingRequestBody(messages, tools);

        ResetDsmlBuffer();

        while (true)
        {
            ThrowIfDisposed();

            var jsonPayload = JsonSerializer.Serialize(requestBody);
            using var response = await PostWithRetryAsync(StreamUrl, jsonPayload, cancellationToken);

            var state = new StreamingState();
            await foreach (var chunk in StreamFromResponseAsync(response, requestBody, state, cancellationToken))
            {
                yield return chunk;
            }

            if (state.FollowUpRequest == null)
                yield break;

            // Tool results were appended — re-issue the request with the extended history.
            requestBody = state.FollowUpRequest;
            RaiseOnStatusChange(ProviderStatus.Sending);
        }
    }

    // ─── Shared: Message Building ──────────────────────────────────────
    protected List<object> BuildMessages(List<ChatMessage> history, byte[]? image)
    {
        var messages = new List<object>();

        for (int i = 0; i < history.Count; i++)
        {
            var entry = history[i];
            string role = MapRoleToOpenAI(entry.Role);
            var content = entry.Content ?? "";

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

        if (MaxTokens is int maxTokens)
        {
            body["max_tokens"] = maxTokens;
        }

        ApplyReasoningOptions(body);

        return body;
    }

    /// <summary>
    /// Maps the configured ReasoningMode onto profile-specific request
    /// parameters. Subclasses override to match their endpoint's dialect.
    /// </summary>
    protected virtual void ApplyReasoningOptions(Dictionary<string, object> body)
    {
        // Generic OpenAI-style endpoints have no universal thinking toggle;
        // reasoning_effort is the most portable knob. Off still sends "low" —
        // the lowest universally-accepted value (models without reasoning
        // support simply ignore it).
        var effort = Reasoning switch
        {
            ReasoningMode.Off or ReasoningMode.Low => "low",
            ReasoningMode.Medium => "medium",
            ReasoningMode.High => "high",
            _ => null,
        };
        if (effort != null)
            body["reasoning_effort"] = effort;
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
        public string? ReasoningChunk { get; set; }
        public bool HasToolCallsFinish { get; set; }
        /// <summary>Raw finish_reason from this chunk ("stop", "length", "tool_calls", ...), if present.</summary>
        public string? FinishReason { get; set; }
        public List<ToolCallDelta> ToolCallDeltas { get; } = new();
    }

    protected StreamChunkParseResult TryParseStreamChunk(string jsonPart)
    {
        var result = new StreamChunkParseResult();

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];

                if (choice.TryGetProperty("finish_reason", out var finishReason) &&
                    finishReason.ValueKind == JsonValueKind.String)
                {
                    result.FinishReason = finishReason.GetString();
                    if (result.FinishReason == "tool_calls")
                        result.HasToolCallsFinish = true;
                }

                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                        reasoning.ValueKind != JsonValueKind.Null)
                    {
                        result.ReasoningChunk = reasoning.GetString() ?? "";
                    }

                    if (delta.TryGetProperty("content", out var content) &&
                        content.ValueKind != JsonValueKind.Null)
                    {
                        result.Chunk = content.GetString() ?? "";
                    }

                    // Models/proxies may send "tool_calls": null explicitly —
                    // TryGetProperty matches that, so check the kind too.
                    if (delta.TryGetProperty("tool_calls", out var toolCallsDelta) &&
                        toolCallsDelta.ValueKind == JsonValueKind.Array)
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
        catch (InvalidOperationException ex)
        {
            // Unexpected element kinds (e.g. "choices": null) — skip the chunk
            // rather than aborting the whole stream.
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

    // ─── Shared: SSE response processing + tool rounds ────────────────
    private async IAsyncEnumerable<string> StreamFromResponseAsync(
        HttpResponseMessage response,
        Dictionary<string, object> requestBody,
        StreamingState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var toolCalls = new Dictionary<int, ToolCallBuilder>();
        var fullReasoning = new StringBuilder();
        bool hasToolCalls = false;
        bool sawDoneSentinel = false;
        string? finishReason = null;
        int chunkIndex = 0;

        await foreach (var jsonPart in ReadSseDataLinesAsync(response, cancellationToken))
        {
            if (jsonPart == "[DONE]")
            {
                sawDoneSentinel = true;
                break;
            }

            var parseResult = TryParseStreamChunk(jsonPart);
            if (parseResult.Error != null)
            {
                LogJsonParseError(jsonPart, parseResult.Error);
                continue;
            }

            if (parseResult.FinishReason != null)
                finishReason = parseResult.FinishReason;

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
                state.FullContent.Append(parseResult.Chunk);
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

        // Expose how the stream ended: a clean end has either the [DONE]
        // sentinel or an explicit finish_reason. A stream that just closes
        // (proxy drop, upstream reset) means the answer may be cut off.
        LastFinishReason = finishReason;
        LastStreamEndedCleanly = sawDoneSentinel || finishReason != null;
        _logger.Log($"[{Name}] stream ending: finish_reason={LastFinishReason ?? "(none)"}, clean_end={LastStreamEndedCleanly}");
        if (!LastStreamEndedCleanly)
            _logger.Log("[warn] SSE stream ended without [DONE] or finish_reason — response may be truncated");

        if (hasToolCalls && toolCalls.Count > 0)
        {
            var followUp = await ProcessToolCallsAsync(requestBody, state, fullReasoning, BuildCompletedToolCalls(toolCalls), cancellationToken);
            if (followUp != null)
                state.FollowUpRequest = followUp;
        }
    }

    /// <summary>
    /// Executes completed tool calls and builds the follow-up request body,
    /// or returns null when no tool results need to be sent back.
    /// </summary>
    private async Task<Dictionary<string, object>?> ProcessToolCallsAsync(
        Dictionary<string, object> requestBody,
        StreamingState state,
        StringBuilder fullReasoning,
        List<ToolCall> completedToolCalls,
        CancellationToken cancellationToken)
    {
        var toolResults = new List<ToolResult>();
        string? ttsText = null;
        var otherToolCalls = new List<ToolCall>();

        foreach (var toolCall in completedToolCalls)
        {
            var tts = ExtractTtsText(toolCall);
            if (tts != null)
                ttsText = tts;
            else
                otherToolCalls.Add(toolCall);
        }

        // Only raise TTS if tts_summary is the sole tool call (final response)
        // Skip TTS if it came alongside search/fetch to avoid premature playback
        if (!string.IsNullOrEmpty(ttsText) && otherToolCalls.Count == 0)
            RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.Tts, ttsText));

        foreach (var toolCall in otherToolCalls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (toolCall.Name == "web_search" && _searchService != null)
            {
                // "info", when present, replaces the raw query in the chat line (spoiler-free display)
                var query = GetInfoLabel(toolCall.Arguments, "query");
                if (!string.IsNullOrEmpty(query))
                    RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.Searching, query, _searchService.Name));
                toolResults.Add(await ExecuteWebSearchToolAsync(toolCall, cancellationToken));
            }
            else if (toolCall.Name == "web_scrape" && _webFetchService != null)
            {
                var fetchUrl = GetInfoLabel(toolCall.Arguments, "url");
                if (!string.IsNullOrEmpty(fetchUrl))
                    RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.Fetching, fetchUrl));
                toolResults.Add(await ExecuteWebScrapeToolAsync(toolCall, cancellationToken));
            }
            else if (toolCall.Name == "file_read" && _fileReadService != null)
            {
                var filePath = toolCall.Arguments.TryGetValue("path", out var fp) ? fp?.ToString() : null;
                if (!string.IsNullOrEmpty(filePath))
                    RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.ReadingFile, filePath));
                toolResults.Add(await ExecuteFileReadToolAsync(toolCall, cancellationToken));
            }
            else if (toolCall.Name == "list_files" && _fileReadService != null)
            {
                var listPath = toolCall.Arguments.TryGetValue("path", out var lp) ? lp?.ToString() : null;
                if (!string.IsNullOrEmpty(listPath))
                    RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.ReadingFile, listPath));
                toolResults.Add(await ExecuteFileListToolAsync(toolCall, cancellationToken));
            }
            else if (toolCall.Name != null && toolCall.Name.StartsWith("browse_") && _browserService != null)
            {
                toolResults.Add(await ExecuteBrowseToolAsync(toolCall, cancellationToken));
            }
            else if (toolCall.Name == "youtube_captions" && _captionService != null)
            {
                toolResults.Add(await ExecuteYouTubeCaptionsToolAsync(toolCall, cancellationToken));
            }
            else if (toolCall.Name == "file_write" && _fileWriteService != null)
            {
                toolResults.Add(await ExecuteFileWriteToolAsync(toolCall, cancellationToken));
            }
            else if (toolCall.Name == "memory_write" && _fileWriteService != null)
            {
                toolResults.Add(await ExecuteMemoryWriteToolAsync(toolCall, cancellationToken));
            }
            else
            {
                toolResults.Add(new ToolResult(toolCall.Id, $"Unknown tool: {toolCall.Name}", isError: true));
            }
        }

        if (otherToolCalls.Count > 0)
            RaiseOnStatusChange(ProviderStatus.Sending);

        if (toolResults.Count == 0)
            return null;

        var messages = (List<object>)requestBody["messages"];
        var newMessages = new List<object>(messages);

        var assistantMessage = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = state.FullContent.Length > 0 ? StripDsmlTags(state.FullContent.ToString()) : null,
            ["tool_calls"] = otherToolCalls.Select(tc => new
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
        };
        if (fullReasoning.Length > 0)
            assistantMessage["reasoning_content"] = fullReasoning.ToString();

        newMessages.Add(assistantMessage);

        foreach (var tr in toolResults)
        {
            newMessages.Add(new
            {
                role = "tool",
                tool_call_id = tr.ToolCallId,
                content = tr.Content
            });
        }

        return new Dictionary<string, object>(requestBody)
        {
            ["messages"] = newMessages
        };
    }

    // ─── DSML filtering ────────────────────────────────────────────────
    protected void ResetDsmlBuffer() => _dsmlBuffer = "";

    protected string FilterDsmlChunk(string chunk)
    {
        chunk = _dsmlBuffer + chunk;
        _dsmlBuffer = "";

        var lastAngle = chunk.LastIndexOf('<');
        if (lastAngle >= 0 && lastAngle >= chunk.Length - 4)
        {
            var tail = chunk[lastAngle..];
            if (tail.Contains('|') && !tail.Contains('>'))
            {
                _dsmlBuffer = tail;
                chunk = chunk[..lastAngle];
            }
        }

        return StripDsmlTags(chunk);
    }

    private static readonly System.Text.RegularExpressions.Regex _dsmlRegex = new(
        @"<\|\|DSML\|\|[^>]*>",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    protected static string StripDsmlTags(string input) => _dsmlRegex.Replace(input, "");
}
