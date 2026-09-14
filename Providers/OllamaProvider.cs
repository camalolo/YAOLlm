using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace YAOLlm.Providers;

public class OllamaProvider : BaseLLMProvider
{
    private readonly string _baseUrl;
    private string _model;

    public override string Name => "ollama";
    public override string Model { get => _model; protected set => _model = value; }
    public override bool SupportsWebSearch => false;

    private string StreamUrl => $"{_baseUrl}/api/chat";

    public OllamaProvider(string model, string baseUrl, HttpClient? httpClient = null, Logger? logger = null)
        : base(httpClient, null, null, logger)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
    }

    private List<object> BuildMessages(List<ChatMessage> history, byte[]? image)
    {
        var messages = new List<object>();

        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            string role = MapRoleToOpenAI(msg.Role);
            var content = msg.Content ?? "";

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

    /// <summary>
    /// Builds the /api/chat request body. Thinking models (qwen3,
    /// deepseek-r1, ...) accept think:false to skip the reasoning pass
    /// entirely and think:true to force it on; Ollama has no effort scale, so
    /// Low keeps the saving behavior (think:false) and Medium/High restore
    /// full thinking. Older Ollama versions ignore the unknown field.
    /// MaxTokens maps to options.num_predict.
    /// </summary>
    internal Dictionary<string, object> BuildRequestBody(List<object> messages, List<ToolDefinition>? tools)
    {
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["stream"] = true
        };

        if (tools != null && tools.Count > 0)
        {
            requestBody["tools"] = FormatToolDefinitions(tools);
        }

        requestBody["think"] = Reasoning switch
        {
            ReasoningMode.Off or ReasoningMode.Low => false,
            ReasoningMode.Medium or ReasoningMode.High => true,
            _ => null,
        };
        if (requestBody["think"] == null)
            requestBody.Remove("think");

        if (MaxTokens is int maxTokens)
            requestBody["options"] = new Dictionary<string, object> { ["num_predict"] = maxTokens };

        return requestBody;
    }

    public override async IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        LogRequest(history.Count, tools != null && tools.Count > 0);
        LastFinishReason = null;
        LastStreamEndedCleanly = false;

        var messages = BuildMessages(history, image);
        var requestBody = BuildRequestBody(messages, tools);

        var jsonPayload = JsonSerializer.Serialize(requestBody);

        ThrowIfDisposed();
        using var response = await PostWithRetryAsync(StreamUrl, jsonPayload, cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var pendingToolCalls = new List<ToolCall>();
        int chunkIndex = 0;
        bool sawDone = false;
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.Contains("\"done\":true", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("\"done\": true", StringComparison.OrdinalIgnoreCase))
            {
                sawDone = true;
            }

            var chunk = ParseStreamLine(line, pendingToolCalls);
            if (chunk != null)
            {
                chunkIndex++;
                LogStreamChunk(chunkIndex);
                yield return chunk;
            }
        }

        LogStreamComplete(chunkIndex, pendingToolCalls.Count);

        // Clean end = the final JSON line carried done:true; otherwise the
        // connection dropped mid-generation
        LastStreamEndedCleanly = sawDone;
        LastFinishReason = sawDone ? "stop" : null;
        _logger.Log($"[{Name}] stream ending: finish_reason={LastFinishReason ?? "(none)"}, clean_end={LastStreamEndedCleanly}");

        // Ollama does not support tool-result round-trips in this provider;
        // the only advertised tool is tts_summary, which we handle as a
        // terminal spoken summary rather than a request for data.
        foreach (var toolCall in pendingToolCalls)
        {
            var ttsText = ExtractTtsText(toolCall);
            if (ttsText != null)
            {
                RaiseOnStatusChange(new ProviderStatus(ProviderStatusKind.Tts, ttsText));
            }
            else
            {
                LogError("StreamAsync", $"Unsupported tool call: {toolCall.Name}");
            }
        }
    }

    private string? ParseStreamLine(string line, List<ToolCall> pendingToolCalls)
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
                    chunk = chunkText;
            }

            if (root.TryGetProperty("message", out var msgForTools) &&
                msgForTools.TryGetProperty("tool_calls", out var toolCallsArr) &&
                toolCallsArr.ValueKind == JsonValueKind.Array)
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

            return chunk;
        }
        catch (JsonException ex)
        {
            LogJsonParseError(line, ex.Message);
            return null;
        }
    }
}
