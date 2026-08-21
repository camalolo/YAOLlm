using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace YAOLlm.Providers;

public class GeminiProvider : BaseLLMProvider
{
    private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    private readonly string _apiKey;

    public override string Name => "gemini";
    public override string Model { get; protected set; }
    public override bool SupportsWebSearch => true;

    private string StreamUrl => $"{ApiBaseUrl}{Model}:streamGenerateContent?alt=sse";

    public GeminiProvider(string model, string apiKey, HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null)
        : base(httpClient, searchService, webFetchService, logger)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    protected override void CustomizeRequest(HttpRequestMessage request)
        => request.Headers.Add("x-goog-api-key", _apiKey);

    public override async IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        var contentsArr = BuildContentsArray(history, image);
        var toolsPayload = BuildToolsPayload(tools);

        LogRequest(history.Count, tools != null && tools.Count > 0);
        CompletedSearchCount = 0;
        CompletedFetchCount = 0;
        CompletedSearchSummaries = null;

        ThrowIfDisposed();

        int totalChunks = 0;

        while (true)
        {
            var payload = new Dictionary<string, object> { ["contents"] = contentsArr };
            if (toolsPayload != null)
                payload["tools"] = toolsPayload;

            var jsonPayload = JsonSerializer.Serialize(payload);
            using var response = await PostWithRetryAsync(StreamUrl, jsonPayload, cancellationToken);

            var roundContent = new StringBuilder();
            var roundToolCalls = new List<ToolCall>();
            int roundChunks = 0;
            string? lastFinishReason = null;

            await foreach (var jsonPart in ReadSseDataLinesAsync(response, cancellationToken))
            {
                var (textChunks, toolCalls, finishReason) = ParseStreamChunk(jsonPart);
                if (finishReason != null)
                    lastFinishReason = finishReason;
                foreach (var chunk in textChunks)
                {
                    totalChunks++;
                    roundChunks++;
                    LogStreamChunk(totalChunks);
                    roundContent.Append(chunk);
                    yield return chunk;
                }
                roundToolCalls.AddRange(toolCalls);
            }

            LogStreamComplete(roundChunks, roundToolCalls.Count);

            if (roundToolCalls.Count == 0)
            {
                if (roundChunks == 0 && totalChunks == 0 && lastFinishReason != null && lastFinishReason != "STOP")
                    throw LLMException.CreateWithMessage($"Model returned no response (finish reason: {lastFinishReason})", Name);
                yield break;
            }

            // Separate tts_summary from other tool calls
            string? ttsText = null;
            var otherToolCalls = new List<ToolCall>();
            foreach (var toolCall in roundToolCalls)
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

            if (otherToolCalls.Count == 0)
                yield break;

            // Append the model turn: optional pre-tool text + function calls
            var modelParts = new JsonArray();
            if (roundContent.Length > 0)
                modelParts.Add(new JsonObject { ["text"] = roundContent.ToString() });
            foreach (var toolCall in otherToolCalls)
            {
                modelParts.Add(new JsonObject
                {
                    ["functionCall"] = new JsonObject
                    {
                        ["name"] = toolCall.Name,
                        ["args"] = JsonSerializer.SerializeToNode(toolCall.Arguments)
                    }
                });
            }
            contentsArr.Add(new JsonObject { ["role"] = "model", ["parts"] = modelParts });

            // Execute each tool call and append its function response
            foreach (var toolCall in otherToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();

                ToolResult result;
                if (toolCall.Name == "web_search" && _searchService != null)
                {
                    var query = toolCall.Arguments.TryGetValue("query", out var q) ? q?.ToString() : null;
                    if (!string.IsNullOrEmpty(query))
                        RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.Searching, query, _searchService.Name));
                    result = await ExecuteWebSearchToolAsync(toolCall, cancellationToken);
                }
                else if (toolCall.Name == "web_fetch" && _webFetchService != null)
                {
                    var fetchUrl = toolCall.Arguments.TryGetValue("url", out var u) ? u?.ToString() : null;
                    if (!string.IsNullOrEmpty(fetchUrl))
                        RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.Fetching, fetchUrl));
                    result = await ExecuteWebFetchToolAsync(toolCall, cancellationToken);
                }
                else
                {
                    result = new ToolResult(toolCall.Id, $"Unknown tool: {toolCall.Name}", isError: true);
                }

                contentsArr.Add(new JsonObject
                {
                    ["role"] = "function",
                    ["parts"] = new JsonArray(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = toolCall.Name,
                            ["response"] = new JsonObject { ["result"] = result.Content }
                        }
                    })
                });
            }

            RaiseOnStatusChange(ProviderStatus.Sending);
        }
    }

    private (List<string> textChunks, List<ToolCall> toolCalls, string? finishReason) ParseStreamChunk(string jsonPart)
    {
        var textChunks = new List<string>();
        var toolCalls = new List<ToolCall>();
        string? finishReason = null;

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];

                if (candidate.TryGetProperty("finishReason", out var fr))
                    finishReason = fr.GetString();

                if (candidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text))
                        {
                            var chunk = text.GetString() ?? "";
                            if (!string.IsNullOrEmpty(chunk))
                            {
                                textChunks.Add(chunk);
                            }
                        }
                        else if (part.TryGetProperty("functionCall", out var funcCall))
                        {
                            var toolCall = new ToolCall
                            {
                                Name = funcCall.GetProperty("name").GetString() ?? "",
                                Id = Guid.NewGuid().ToString()
                            };
                            if (funcCall.TryGetProperty("args", out var args))
                            {
                                toolCall.Arguments = new Dictionary<string, object?>();
                                foreach (var prop in args.EnumerateObject())
                                {
                                    toolCall.Arguments[prop.Name] = prop.Value.ValueKind switch
                                    {
                                        JsonValueKind.String => prop.Value.GetString(),
                                        JsonValueKind.Number => prop.Value.GetDouble(),
                                        JsonValueKind.True => true,
                                        JsonValueKind.False => false,
                                        _ => prop.Value.ToString()
                                    };
                                }
                            }
                            toolCalls.Add(toolCall);
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            LogJsonParseError(jsonPart, ex.Message);
        }

        return (textChunks, toolCalls, finishReason);
    }

    // ─── Contents building (no reflection) ────────────────────────────

    private static JsonArray BuildContentsArray(List<ChatMessage> history, byte[]? image)
    {
        var contents = new JsonArray();

        foreach (var message in history)
        {
            var parts = new JsonArray();

            if (!string.IsNullOrEmpty(message.Content))
                parts.Add(new JsonObject { ["text"] = message.Content });

            if (message.Image != null)
            {
                var (mimeType, base64Data) = GetImageInfo(message.Image);
                if (mimeType != null && base64Data != null)
                    parts.Add(BuildInlineDataPart(mimeType, base64Data));
            }

            if (parts.Count > 0)
                contents.Add(new JsonObject { ["role"] = MapRoleToGemini(message.Role), ["parts"] = parts });
        }

        // Attach the new image to the last (user) message
        if (image != null && contents.Count > 0)
        {
            var (mimeType, base64Data) = GetImageInfo(image);
            if (mimeType != null && base64Data != null)
            {
                var lastParts = (JsonArray)contents[^1]!["parts"]!;
                lastParts.Add(BuildInlineDataPart(mimeType, base64Data));
            }
        }

        return contents;
    }

    private static JsonObject BuildInlineDataPart(string mimeType, string base64Data)
        => new() { ["inlineData"] = new JsonObject { ["mimeType"] = mimeType, ["data"] = base64Data } };

    private static (string? mimeType, string? base64Data) GetImageInfo(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            return (null, null);

        var mimeType = DetectImageMimeType(imageBytes);
        var base64 = Convert.ToBase64String(imageBytes);
        return (mimeType, base64);
    }

    private static JsonNode? BuildToolsPayload(List<ToolDefinition>? tools)
    {
        if (tools == null || tools.Count == 0)
            return null;

        var functionDeclarations = new JsonArray();
        foreach (var t in tools)
        {
            functionDeclarations.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonSerializer.SerializeToNode(t.Parameters)
            });
        }

        return new JsonObject { ["functionDeclarations"] = functionDeclarations };
    }

    private static string MapRoleToGemini(ChatRole role)
    {
        return role switch
        {
            ChatRole.System => "user",
            _ => role.ToApiString()
        };
    }
}
