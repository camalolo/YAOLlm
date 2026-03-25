using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace YAOLlm.Providers;

public class GeminiProvider : BaseLLMProvider
{
    private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const int GenerationRpmLimit = 15;
    private const int MaxRetries = 3;

    private static readonly SemaphoreSlim RateLimiter = new(GenerationRpmLimit, GenerationRpmLimit);
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

    public override async Task<string> SendAsync(
        List<Dictionary<string, object>> history,
        byte[]? image = null,
        List<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        if (history == null || history.Count == 0)
            throw new ArgumentException("History cannot be null or empty", nameof(history));

        var contents = BuildContents(history, image);
        var toolsPayload = BuildToolsPayload(tools);
        var payload = new Dictionary<string, object> { ["contents"] = contents };
        if (toolsPayload != null)
            payload["tools"] = toolsPayload;

        LogRequest(history.Count, tools != null && tools.Count > 0);
        return await SendWithRetryAsync(payload, contents, cancellationToken);
    }

    public override async IAsyncEnumerable<string> StreamAsync(
        List<Dictionary<string, object>> history,
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

        using var httpClient = new HttpClient();
        var jsonPayload = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var fullContent = new StringBuilder();
        string? line;
        var pendingToolCalls = new List<ToolCall>();
        int chunkIndex = 0;

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
                    RaiseOnStatusChange("searching");
                }

                ToolResult? result = await RaiseOnToolCallAsync(toolCall);

                if (result == null && toolCall.Name == "web_search" && _searchService != null)
                {
                    var searchResult = await ExecuteWebSearchAsync(toolCall.Arguments ?? new Dictionary<string, object?>());
                    result = new ToolResult(toolCall.Id, searchResult);
                }

                RaiseOnStatusChange(null);

                if (result != null)
                {
                    var toolHistory = new List<Dictionary<string, object>>(history)
                    {
                        new() { ["role"] = "user", ["content"] = fullContent.ToString() }
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
        List<Dictionary<string, object>> history,
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

        using var httpClient = new HttpClient();
        var jsonPayload = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

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

    private object[] BuildContents(List<Dictionary<string, object>> history, byte[]? image)
    {
        var contents = new List<object>();

        foreach (var message in history)
        {
            if (!message.TryGetValue("role", out var roleObj) || roleObj is not string role)
                continue;

            var parts = new List<object>();

            if (message.TryGetValue("content", out var contentObj) && contentObj is string content && !string.IsNullOrEmpty(content))
            {
                parts.Add(new { text = content });
            }

            if (message.TryGetValue("image", out var imageObj))
            {
                (string? mimeType, string? base64Data) = imageObj switch
                {
                    byte[] imgBytes => GetImageInfo(imgBytes),
                    string base64 => ("image/png", base64),
                    _ => (null, null)
                };

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

    private string GetGenerateUrl() => $"{ApiBaseUrl}{Model}:generateContent?key={_apiKey}";

    private async Task<string> SendWithRetryAsync(
        Dictionary<string, object> payload,
        object[] originalContents,
        CancellationToken cancellationToken)
    {
        await RateLimiter.WaitAsync(cancellationToken);

        try
        {
            for (int retry = 0; retry <= MaxRetries; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (content, response) = await ExecuteRequestAsync(payload, cancellationToken);

                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    if (retry == MaxRetries)
                    {
                        LogError("rate limit", "Max retries reached");
                        throw LLMException.CreateWithStatusCode(429, "Max retries reached", Name);
                    }

                    int delay = (int)Math.Pow(2, retry) * 1000;
                    LogRetry(retry + 1, MaxRetries, delay);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (content == null)
                {
                    LogError("request", $"StatusCode={response.StatusCode}, Error={response.ErrorMessage}");
                    throw LLMException.CreateWithStatusCode((int)response.StatusCode, response.ErrorMessage ?? $"Status: {response.StatusCode}", Name);
                }

                return await ExtractResponseAsync(content, originalContents, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            LogCancelled();
            throw;
        }
        catch (Exception ex)
        {
            LogError("SendWithRetryAsync", ex.Message);
            throw;
        }
        finally
        {
            RateLimiter.Release();
        }

        return string.Empty;
    }

    private async Task<(string? content, RestResponse response)> ExecuteRequestAsync(
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = GetGenerateUrl();
            var client = new RestClient(url);
            var request = new RestRequest { Method = Method.Post };
            request.AddHeader("Content-Type", "application/json");
            request.AddJsonBody(payload);

            var response = await client.ExecuteAsync(request, cancellationToken);

            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
            {
                LogResponse(response.Content);
                return (response.Content, response);
            }

            return (null, response);
        }
        catch (Exception ex)
        {
            LogError("ExecuteRequestAsync", ex.Message);
            return (null, new RestResponse());
        }
    }

    private async Task<string> ExtractResponseAsync(
        string content,
        object[] originalContents,
        CancellationToken cancellationToken)
    {
        try
        {
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            if (data.TryGetProperty("error", out var errorObj))
            {
                var errorMsg = errorObj.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "Unknown API error"
                    : errorObj.GetString() ?? "Unknown API error";
                throw LLMException.CreateWithMessage(errorMsg, Name);
            }

            if (!data.TryGetProperty("candidates", out var candidates) || !candidates.EnumerateArray().Any())
            {
                LogError("ExtractResponseAsync", "No candidates in LLM response");
                return string.Empty;
            }

            var messageParts = ExtractTextFromCandidates(data, Name);
            JsonElement? modelContentElement = null;

            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out var contentElement))
                    continue;

                modelContentElement = contentElement;

                if (!contentElement.TryGetProperty("parts", out var partsElement))
                    continue;

                foreach (var part in partsElement.EnumerateArray())
                {
                    if (part.TryGetProperty("functionCall", out var functionCall))
                    {
                        var functionMessage = await HandleFunctionCallAsync(
                            functionCall,
                            contentElement,
                            originalContents,
                            cancellationToken);

                        if (!string.IsNullOrEmpty(functionMessage))
                            messageParts.Add(functionMessage);
                    }
                }
            }

            return string.Join("\n\n", messageParts);
        }
        catch (Exception ex)
        {
            LogError("ExtractResponseAsync", ex.Message);
            throw;
        }
    }

    private async Task<string> HandleFunctionCallAsync(
        JsonElement functionCall,
        JsonElement modelContentElement,
        object[] originalContents,
        CancellationToken cancellationToken)
    {
        var functionName = functionCall.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;

        if (string.IsNullOrEmpty(functionName))
        {
            LogError("HandleFunctionCall", "Function call missing name");
            return string.Empty;
        }

        var args = new Dictionary<string, object?>();
        if (functionCall.TryGetProperty("args", out var argsElement))
        {
            foreach (var prop in argsElement.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var intVal) ? intVal : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.ToString()
                };
            }
        }

        LogToolCallReceived(functionName, args);

        var toolCall = new ToolCall
        {
            Id = Guid.NewGuid().ToString(),
            Name = functionName,
            Arguments = args
        };

        string functionResult;

        var result = await RaiseOnToolCallAsync(toolCall);
        if (result != null)
        {
            LogToolExecution(functionName);
            functionResult = result.Content ?? string.Empty;
            LogToolResult(functionName, functionResult);
        }
        else if (functionName == "web_search" && _searchService != null)
        {
            functionResult = await ExecuteWebSearchAsync(args);
        }
        else
        {
            LogError("HandleFunctionCall", $"No handler for function: {functionName}");
            functionResult = $"Error: No handler available for function '{functionName}'";
        }

        return await ContinueWithFunctionResultAsync(
            modelContentElement,
            originalContents,
            functionName,
            functionResult,
            cancellationToken);
    }

    private async Task<string> ExecuteWebSearchAsync(Dictionary<string, object?> args)
    {
        try
        {
            var query = args.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : null;
            var maxResults = args.TryGetValue("max_results", out var maxResultsObj) && maxResultsObj is int max
                ? max
                : 5;

            if (string.IsNullOrEmpty(query))
            {
                LogError("web_search", "Missing query parameter");
                return "Error: Missing query parameter";
            }

            RaiseOnStatusChange("searching");
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

    private async Task<string> ContinueWithFunctionResultAsync(
        JsonElement modelContentElement,
        object[] originalContents,
        string functionName,
        string functionResult,
        CancellationToken cancellationToken)
    {
        try
        {
            var conversationHistory = new List<object>(originalContents);

            var modelParts = new List<object>();
            foreach (var contentPart in modelContentElement.GetProperty("parts").EnumerateArray())
            {
                var partObj = JsonSerializer.Deserialize<object>(contentPart.GetRawText());
                if (partObj != null)
                    modelParts.Add(partObj);
            }

            conversationHistory.Add(new
            {
                role = "model",
                parts = modelParts.ToArray()
            });

            conversationHistory.Add(new
            {
                role = "function",
                parts = new[]
                {
                    new
                    {
                        functionResponse = new
                        {
                            name = functionName,
                            response = new { result = functionResult }
                        }
                    }
                }
            });

            var continuePayload = new Dictionary<string, object>
            {
                ["contents"] = conversationHistory.ToArray()
            };

            var (content, _) = await ExecuteRequestAsync(continuePayload, cancellationToken);

            if (content == null)
            {
                LogError("ContinueWithFunctionResult", "Failed to get response after function call");
                return string.Empty;
            }

            return ExtractSimpleResponse(content);
        }
        catch (Exception ex)
        {
            LogError("ContinueWithFunctionResult", ex.Message);
            return $"Error: {ex.Message}";
        }
    }

    private string ExtractSimpleResponse(string content)
    {
        try
        {
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            if (data.TryGetProperty("error", out var errorObj))
            {
                var errorMsg = errorObj.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "Unknown API error"
                    : errorObj.GetString() ?? "Unknown API error";
                throw LLMException.CreateWithMessage(errorMsg, Name);
            }

            var messageParts = ExtractTextFromCandidates(data, Name);
            return string.Join("\n\n", messageParts);
        }
        catch (LLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError("ExtractSimpleResponse", ex.Message);
            return string.Empty;
        }
    }

    /// <summary>
    /// Validates the finish reason and throws LLMException if the response was blocked or truncated.
    /// </summary>
    /// <param name="finishReason">The finish reason string from the API response</param>
    /// <exception cref="LLMException">Thrown when the finish reason indicates a blocked or truncated response</exception>
    private static void ValidateFinishReason(string? finishReason, string providerName)
    {
        if (string.IsNullOrEmpty(finishReason) || finishReason == "STOP" || finishReason == "END_TURN")
            return;

        var userMessage = finishReason switch
        {
            "SAFETY" => "Content blocked by safety filter",
            "RECITATION" => "Content blocked (copyright)",
            "PROHIBITED" => "Content prohibited",
            "BLOCKLIST" => "Content blocked",
            "MAX_TOKENS" => "Response truncated (max tokens)",
            "MALFORMED_FUNCTION_CALL" => "Tool call error",
            "IMAGE_SAFETY" => "Image blocked by safety filter",
            _ => $"API error: {finishReason}"
        };
        throw LLMException.CreateWithMessage(userMessage, providerName);
    }

    /// <summary>
    /// Extracts all text content from the candidates in a Gemini API response.
    /// Validates finish reasons for each candidate.
    /// </summary>
    /// <param name="data">The parsed JSON response</param>
    /// <returns>List of text strings extracted from all candidates' parts</returns>
    private static List<string> ExtractTextFromCandidates(JsonElement data, string providerName)
    {
        var messageParts = new List<string>();

        if (!data.TryGetProperty("candidates", out var candidates))
            return messageParts;

        foreach (var candidate in candidates.EnumerateArray())
        {
            var finishReason = candidate.TryGetProperty("finishReason", out var fr)
                ? fr.GetString()
                : null;

            ValidateFinishReason(finishReason, providerName);

            if (!candidate.TryGetProperty("content", out var contentElement))
                continue;

            if (!contentElement.TryGetProperty("parts", out var partsElement))
                continue;

            foreach (var part in partsElement.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.GetString() is string textValue)
                {
                    messageParts.Add(textValue);
                }
            }
        }

        return messageParts;
    }

    private static string MapRoleToGemini(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "assistant" => "model",
            "system" => "user",
            _ => role
        };
    }
}
