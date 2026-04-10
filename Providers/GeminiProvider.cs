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

public class GeminiProvider : BaseLLMProvider
{
    private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const int MaxRetries = 3;

    private readonly string _apiKey;

    public override string Name => "gemini";
    public override string Model { get; protected set; }
    public override bool SupportsWebSearch => true;

    public GeminiProvider(string model, string apiKey, HttpClient? httpClient = null, TavilySearchService? searchService = null, Logger? logger = null)
        : base(httpClient ?? new HttpClient(), searchService, logger)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    public override async IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        var contents = BuildContents(history, image);
        var toolsPayload = BuildToolsPayload(tools);
        var payload = new Dictionary<string, object>
        {
            ["contents"] = contents,
            ["generationConfig"] = new { }
        };
        if (toolsPayload != null)
            payload["tools"] = toolsPayload;

        LogRequest(history.Count, tools != null && tools.Count > 0);

        var url = $"{ApiBaseUrl}{Model}:streamGenerateContent?alt=sse&key={_apiKey}";

        ThrowIfDisposed();

        HttpResponseMessage response = null!;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            HttpResponseMessage? attemptResponse = null;
            try
            {
                var jsonPayload = JsonSerializer.Serialize(payload);
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    attemptResponse = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

        var fullContent = new StringBuilder();
        string? line;
        var pendingToolCalls = new List<ToolCall>();
        int chunkIndex = 0;

        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (!line.StartsWith("data: "))
                continue;

            var jsonPart = line.Substring(6);
            
            if (jsonPart == "[DONE]")
                break;

            var (textChunks, toolCalls) = ParseStreamChunk(jsonPart);
            foreach (var chunk in textChunks)
            {
                chunkIndex++;
                LogStreamChunk(chunkIndex, chunk);
                fullContent.Append(chunk);
                yield return chunk;
            }
            pendingToolCalls.AddRange(toolCalls);
        }
        
        LogStreamComplete(chunkIndex, pendingToolCalls.Count);

        if (pendingToolCalls.Count > 0)
        {
            foreach (var toolCall in pendingToolCalls)
            {
                if (toolCall.Name == "web_search")
                {
                    RaiseOnStatusChange(StatusManager.SearchingStatus);
                }

                ToolResult? result = null;
                if (toolCall.Name == "web_search" && _searchService != null)
                {
                    result = new ToolResult(toolCall.Id, await ExecuteWebSearchAsync(toolCall.Arguments ?? new Dictionary<string, object?>()));
                }

                RaiseOnStatusChange(null);

                if (result != null)
                {
                    ThrowIfDisposed();
                    var toolHistory = new List<ChatMessage>(history)
                    {
                        new ChatMessage(ChatRole.User, fullContent.ToString())
                    };

                    await foreach (var chunk in StreamWithToolResultAsync(toolHistory, result, tools, cancellationToken))
                    {
                        yield return chunk;
                    }
                    yield break;
                }
            }
        }
    }

    private (List<string> textChunks, List<ToolCall> toolCalls) ParseStreamChunk(string jsonPart)
    {
        var textChunks = new List<string>();
        var toolCalls = new List<ToolCall>();

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];

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

        return (textChunks, toolCalls);
    }

    private async IAsyncEnumerable<string> StreamWithToolResultAsync(
        List<ChatMessage> history,
        ToolResult toolResult,
        List<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var toolContents = BuildContents(history, null);
        var toolContentsList = toolContents.ToList();

        toolContentsList.Add(new
        {
            role = "model",
            parts = new object[]
            {
                new
                {
                    functionCall = new
                    {
                        name = "web_search",
                        args = new Dictionary<string, object?>()
                    }
                }
            }
        });

        toolContentsList.Add(new
        {
            role = "function",
            parts = new object[]
            {
                new
                {
                    functionResponse = new
                    {
                        name = "web_search",
                        response = new { result = toolResult.Content }
                    }
                }
            }
        });

        var payload = new Dictionary<string, object>
        {
            ["contents"] = toolContentsList.ToArray()
        };

        var toolsPayload = BuildToolsPayload(tools);
        if (toolsPayload != null)
            payload["tools"] = toolsPayload;

        var url = $"{ApiBaseUrl}{Model}:streamGenerateContent?alt=sse&key={_apiKey}";

        ThrowIfDisposed();

        HttpResponseMessage response = null!;
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            HttpResponseMessage? attemptResponse = null;
            try
            {
                var jsonPayload = JsonSerializer.Serialize(payload);
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    attemptResponse = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var jsonPart = line.Substring(6);
            if (jsonPart == "[DONE]")
                break;

            var (textChunks, _) = ParseStreamChunk(jsonPart);
            foreach (var chunk in textChunks)
            {
                yield return chunk;
            }
        }
    }

    private object[] BuildContents(List<ChatMessage> history, byte[]? image)
    {
        var contents = new List<object>();

        foreach (var message in history)
        {
            var role = message.Role;

            var parts = new List<object>();

            if (!string.IsNullOrEmpty(message.Content))
            {
                parts.Add(new { text = message.Content });
            }

            if (message.Image != null)
            {
                (string? mimeType, string? base64Data) = GetImageInfo(message.Image);

                if (mimeType != null && base64Data != null)
                {
                    parts.Add(new { inlineData = new { mimeType, data = base64Data } });
                }
            }

            if (parts.Count > 0)
                contents.Add(new { role = MapRoleToGemini(role), parts = parts.ToArray() });
        }

        if (image != null && contents.Count > 0)
        {
            (string? mimeType, string? base64Data) = GetImageInfo(image);
            if (mimeType != null && base64Data != null)
            {
                var lastContent = contents[^1];
                if (lastContent is { } obj)
                {
                    var existingParts = GetPartsFromContent(obj);
                    var newParts = existingParts.ToList();
                    newParts.Add(new { inlineData = new { mimeType, data = base64Data } });
                    contents[^1] = new { role = GetRoleFromContent(obj), parts = newParts.ToArray() };
                }
            }
        }

        return contents.ToArray();
    }

    private static (string? mimeType, string? base64Data) GetImageInfo(byte[] imageBytes)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            return (null, null);

        var mimeType = DetectImageMimeType(imageBytes);
        var base64 = Convert.ToBase64String(imageBytes);
        return (mimeType, base64);
    }

    private static IEnumerable<object> GetPartsFromContent(object content)
    {
        var contentDict = content.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(content));

        if (contentDict.TryGetValue("parts", out var parts) && parts is object[] partsArray)
            return partsArray;

        return Array.Empty<object>();
    }

    private static string GetRoleFromContent(object content)
    {
        var contentDict = content.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(content));

        if (contentDict.TryGetValue("role", out var role) && role is string roleStr)
            return roleStr;

        return "user";
    }

    private object[]? BuildToolsPayload(List<ToolDefinition>? tools)
    {
        if (tools == null || tools.Count == 0)
            return null;

        var functionDeclarations = tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.Parameters
        }).ToArray();

        return new[] { new { functionDeclarations } };
    }

    private async Task<string> ExecuteWebSearchAsync(Dictionary<string, object?> args)
    {
        try
        {
            var query = args.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : null;
            int maxResults;
            if (args.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is long l && l >= 0)
                maxResults = (int)l;
            else
                maxResults = 5;

            if (string.IsNullOrEmpty(query))
            {
                LogError("web_search", "Missing query parameter");
                return "Error: Missing query parameter";
            }

            RaiseOnStatusChange(StatusManager.SearchingStatus);
            LogToolExecution("web_search");
            var result = await _searchService!.SearchAsync(query, maxResults);
            RaiseOnStatusChange(null);
            LogToolResult("web_search", result);
            return result;
        }
        catch (Exception ex)
        {
            LogError("web_search", ex.Message);
            return $"Error executing web search: {ex.Message}";
        }
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
