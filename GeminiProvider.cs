using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace YAOLlm;

public class GeminiProvider : ILLMProvider
{
    private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const int GenerationRpmLimit = 15;
    private const int MaxRetries = 3;

    private static readonly SemaphoreSlim RateLimiter = new(GenerationRpmLimit, GenerationRpmLimit);
    private readonly string _apiKey;
    private readonly string _model;
    private readonly TavilySearchService _searchService;
    private readonly Logger _logger;

    public string Name => "gemini";
    public string Model => _model;
    public bool SupportsWebSearch => false; // Gemini has built-in grounding

    public event Func<ToolCall, Task<ToolResult>>? OnToolCall;

    public GeminiProvider(string model, string apiKey, TavilySearchService searchService, Logger logger)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> SendAsync(
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

        ProviderLogger.LogRequest(_logger, Name, _model, history.Count, tools != null && tools.Count > 0);
        return await SendWithRetryAsync(payload, contents, cancellationToken);
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

        var mimeType = DetectMimeType(imageBytes);
        var base64 = Convert.ToBase64String(imageBytes);
        return (mimeType, base64);
    }

    private static string DetectMimeType(byte[] bytes)
    {
        if (bytes.Length < 4) return "image/png";

        return bytes[0] switch
        {
            0x89 when bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 => "image/png",
            0xFF when bytes[1] == 0xD8 && bytes[2] == 0xFF => "image/jpeg",
            0x47 when bytes[1] == 0x49 && bytes[2] == 0x46 => "image/gif",
            0x52 when bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 => "image/webp",
            _ => "image/png"
        };
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

    private string GetGenerateUrl() => $"{ApiBaseUrl}{_model}:generateContent?key={_apiKey}";

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
                        ProviderLogger.LogError(_logger, Name, "rate limit", "Max retries reached");
                        throw new Exception("Max retries reached for rate limit error");
                    }

                    int delay = (int)Math.Pow(2, retry) * 1000;
                    ProviderLogger.LogRetry(_logger, Name, retry + 1, MaxRetries, delay);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (content == null)
                {
                    ProviderLogger.LogError(_logger, Name, "request", $"StatusCode={response.StatusCode}, Error={response.ErrorMessage}");
                    throw new Exception($"Request failed: {response.StatusCode}");
                }

                return await ExtractResponseAsync(content, originalContents, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            ProviderLogger.LogCancelled(_logger, Name);
            throw;
        }
        catch (Exception ex)
        {
            ProviderLogger.LogError(_logger, Name, "SendWithRetryAsync", ex.Message);
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
                ProviderLogger.LogResponse(_logger, Name, response.Content);
                return (response.Content, response);
            }

            return (null, response);
        }
        catch (Exception ex)
        {
            ProviderLogger.LogError(_logger, Name, "ExecuteRequestAsync", ex.Message);
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

            if (!data.TryGetProperty("candidates", out var candidates) || !candidates.EnumerateArray().Any())
            {
                ProviderLogger.LogError(_logger, Name, "ExtractResponseAsync", "No candidates in LLM response");
                return string.Empty;
            }

            var messageParts = new List<string>();
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
                    if (part.TryGetProperty("text", out var text) && text.GetString() is string textValue)
                    {
                        messageParts.Add(textValue);
                    }
                    else if (part.TryGetProperty("functionCall", out var functionCall))
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
            ProviderLogger.LogError(_logger, Name, "ExtractResponseAsync", ex.Message);
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
            ProviderLogger.LogError(_logger, Name, "HandleFunctionCall", "Function call missing name");
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

        ProviderLogger.LogToolCallReceived(_logger, Name, functionName, args);

        var toolCall = new ToolCall
        {
            Id = Guid.NewGuid().ToString(),
            Name = functionName,
            Arguments = args
        };

        string functionResult;

        if (OnToolCall != null)
        {
            ProviderLogger.LogToolExecution(_logger, Name, functionName);
            var result = await OnToolCall.Invoke(toolCall);
            functionResult = result?.Content ?? string.Empty;
            ProviderLogger.LogToolResult(_logger, Name, functionName, functionResult);
        }
        else if (functionName == "web_search" && _searchService != null)
        {
            functionResult = await ExecuteWebSearchAsync(args);
        }
        else
        {
            ProviderLogger.LogError(_logger, Name, "HandleFunctionCall", $"No handler for function: {functionName}");
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
                ProviderLogger.LogError(_logger, Name, "web_search", "Missing query parameter");
                return "Error: Missing query parameter";
            }

            ProviderLogger.LogToolExecution(_logger, Name, "web_search");
            var result = await _searchService.SearchAsync(query, maxResults);
            ProviderLogger.LogToolResult(_logger, Name, "web_search", result);
            return result;
        }
        catch (Exception ex)
        {
            ProviderLogger.LogError(_logger, Name, "web_search", ex.Message);
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
                ProviderLogger.LogError(_logger, Name, "ContinueWithFunctionResult", "Failed to get response after function call");
                return string.Empty;
            }

            return ExtractSimpleResponse(content);
        }
        catch (Exception ex)
        {
            ProviderLogger.LogError(_logger, Name, "ContinueWithFunctionResult", ex.Message);
            return $"Error: {ex.Message}";
        }
    }

    private string ExtractSimpleResponse(string content)
    {
        try
        {
            var data = JsonSerializer.Deserialize<JsonElement>(content);

            if (!data.TryGetProperty("candidates", out var candidates))
                return string.Empty;

            var messageParts = new List<string>();

            foreach (var candidate in candidates.EnumerateArray())
            {
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

            return string.Join("\n\n", messageParts);
        }
        catch (Exception ex)
        {
            ProviderLogger.LogError(_logger, Name, "ExtractSimpleResponse", ex.Message);
            return string.Empty;
        }
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
