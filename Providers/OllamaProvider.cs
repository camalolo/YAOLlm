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

public class OllamaProvider : BaseLLMProvider
{
    private const int MaxRetries = 3;

    private readonly string _baseUrl;
    private string _model;

    public override string Name => "ollama";
    public override string Model { get => _model; protected set => _model = value; }
    public override bool SupportsWebSearch => false;

    public OllamaProvider(string model, string? baseUrl = null, HttpClient? httpClient = null, Logger? logger = null)
        : base(httpClient ?? new HttpClient(), null, logger)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _baseUrl = baseUrl 
            ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") 
            ?? "http://localhost:11434";
    }

    private List<object> BuildMessages(List<ChatMessage> history, byte[]? image)
    {
        var messages = new List<object>();

        for (int i = 0; i < history.Count; i++)
        {
            var msg = history[i];
            string role = msg.Role == ChatRole.Model ? "assistant" : msg.Role.ToApiString();
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

    public override async IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        LogRequest(history.Count, tools != null && tools.Count > 0);

        var messages = BuildMessages(history, image);
        var requestBody = BuildStreamingRequestBody(messages, tools);

        await foreach (var chunk in StreamInternalAsync(requestBody, cancellationToken))
        {
            yield return chunk;
        }
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
            body["tools"] = FormatToolDefinitions(tools);
        }

        return body;
    }

    private async IAsyncEnumerable<string> StreamInternalAsync(
        Dictionary<string, object> requestBody,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fullContent = new StringBuilder();
        var pendingToolCalls = new List<ToolCall>();
        int chunkIndex = 0;
        
        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(5);

        HttpResponseMessage response = null!;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            HttpResponseMessage? attemptResponse = null;
            try
            {
                var jsonPayload = JsonSerializer.Serialize(requestBody);
                using (var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat"))
                {
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    attemptResponse = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                attemptResponse.EnsureSuccessStatusCode();
                response = attemptResponse;
                break;
            }
            catch (Exception ex)
            {
                attemptResponse?.Dispose();
                if (ShouldRetry(ex, attempt, MaxRetries))
                {
                    var delay = GetRetryDelay(attempt);
                    LogRetry(attempt + 1, MaxRetries, (int)delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }
        
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
                
            var chunk = ParseStreamLine(line, fullContent, pendingToolCalls);
            if (chunk != null)
            {
                chunkIndex++;
                LogStreamChunk(chunkIndex, chunk);
                yield return chunk;
            }
        }
        
        LogStreamComplete(chunkIndex, pendingToolCalls.Count);
        
        if (pendingToolCalls.Count > 0)
        {
            foreach (var toolCall in pendingToolCalls)
            {
                LogError("StreamInternalAsync", $"Unsupported tool call: {toolCall.Name}");
            }
        }
    }

    private string? ParseStreamLine(string line, StringBuilder fullContent, List<ToolCall> pendingToolCalls)
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
                {
                    fullContent.Append(chunkText);
                    chunk = chunkText;
                }
            }
            
            if (root.TryGetProperty("message", out var msgForTools) &&
                msgForTools.TryGetProperty("tool_calls", out var toolCallsArr))
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

    public override void Dispose()
    {
        base.Dispose();
    }
}
