using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RestSharp;

namespace Gemini
{
    public static class ApiFunctions
    {
        private const int EmbeddingRpmLimit = 1500;
        private const int GenerationRpmLimit = 15;
        private const int ExpectedEmbeddingDimension = 768;
        private static readonly SemaphoreSlim EmbeddingRateLimiter = new(EmbeddingRpmLimit, EmbeddingRpmLimit);
        private static readonly SemaphoreSlim GenerationRateLimiter = new(GenerationRpmLimit, GenerationRpmLimit);
        private static readonly TimeSpan RateLimitReset = TimeSpan.FromSeconds(60);

        public static async Task<string?> SendToLLM(GeminiClient client, object payload, string? imageBase64 = null)
        {
            var logger = client.Logger;
            logger.Log($"Preparing LLM request with payload size: {JsonSerializer.Serialize(payload).Length} chars");

            var (content, response) = await SendRequest(client, payload, imageBase64 != null);
            if (content == null)
            {
                logger.Log("LLM request returned null content");
                return null;
            }

            var data = JsonSerializer.Deserialize<JsonElement>(content);
            var (message, functionCalls) = ExtractLLMResponse(logger, data);
            return await ProcessLLMResponse(client, message, functionCalls);
        }

        private static async Task<(string?, RestResponse)> SendRequest(GeminiClient client, object payload, bool isImageRequest)
        {
            var url = isImageRequest ? client.GetGenerateUrl() : client.GetGenerateUrl();
            var restClient = new RestClient(url);
            var request = new RestRequest { Method = Method.Post };
            request.AddHeader("Content-Type", "application/json");

            var jsonPayload = JsonSerializer.Serialize(payload);
            request.AddJsonBody(payload);
            client.Logger.Log($"Sending request to {url} with payload: {jsonPayload.Substring(0, Math.Min(500, jsonPayload.Length))}...");

            client.UpdateStatus(Status.ReceivingData);
            var rateLimiter = isImageRequest ? GenerationRateLimiter : GenerationRateLimiter;

            const int maxRetries = 3;
            try
            {
                await rateLimiter.WaitAsync();
                try
                {
                    for (int retry = 0; retry <= maxRetries; retry++)
                    {
                        try
                        {
                            var response = await restClient.ExecuteAsync(request);
                            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                            {
                                if (retry == maxRetries)
                                {
                                    client.Logger.Log("Max retries reached for 429 error");
                                    return (null, response);
                                }
                                int delay = (int)Math.Pow(2, retry) * 1000;
                                client.Logger.Log($"429 Too Many Requests, retrying in {delay}ms (attempt {retry + 1}/{maxRetries})");
                                await Task.Delay(delay);
                                continue;
                            }
                            response.ThrowIfError();
                            client.Logger.Log($"LLM response received: {response.Content?.Substring(0, Math.Min(500, response.Content?.Length ?? 0))}...");
                            return (response.Content, response);
                        }
                        catch (Exception ex)
                        {
                            client.Logger.Log($"Request failed: {ex.Message}");
                            throw;
                        }
                    }
                }
                finally
                {
                    rateLimiter.Release();
                }
            }
            catch (Exception ex)
            {
                client.Logger.Log($"SendRequest error: {ex.Message}");
                return (null, new RestResponse());
            }
            finally
            {
                client.UpdateStatus(Status.Idle);
            }
            return (null, new RestResponse());
        }

        private static (string message, List<JsonElement> functionCalls) ExtractLLMResponse(Logger logger, JsonElement data)
        {
            if (!data.TryGetProperty("candidates", out var candidates) || !candidates.EnumerateArray().Any())
            {
                logger.Log("No candidates in LLM response");
                return (string.Empty, new List<JsonElement>());
            }

            var messageParts = new List<string>();
            var functionCalls = new List<JsonElement>();

            foreach (var candidate in candidates.EnumerateArray())
            {
                foreach (var part in candidate.GetProperty("content").GetProperty("parts").EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.GetString() is string textValue)
                        messageParts.Add(textValue);
                    else if (part.TryGetProperty("functionCall", out var func))
                        functionCalls.Add(part);
                }
            }

            var message = string.Join("", messageParts);
            logger.Log($"Extracted: Message='{message.Substring(0, Math.Min(100, message.Length))}...', FunctionCalls={functionCalls.Count}");
            return (message, functionCalls);
        }
        private static async Task<string> ProcessLLMResponse(GeminiClient client, string message, List<JsonElement> functionCalls)
        {
            if (functionCalls.Any())
            {
                if (!string.IsNullOrEmpty(message))
                    client.UpdateChat($"{message}\n", "model");

                foreach (var func in functionCalls)
                {
                    var funcCall = ExtractFunctionCall(client.Logger, func);

                    if (!funcCall.HasValue) continue;

                    if (funcCall.Value.args == null)
                    {
                        string errorMessage = $"Error: Function '{funcCall.Value.name}' requires arguments, but none were provided. Please retry with valid arguments.";
                        client.Logger.Log(errorMessage);
                        client.UpdateChat($"System: {errorMessage}\n", "system");

                        // Retry with original query plus error context
                        string retryPrompt = $"{client.OriginalUserQuery}\nSystem: {errorMessage}";
                        return await client.RetryWithPrompt(retryPrompt);
                    }

                    switch (funcCall.Value.name)
                    {
                        case "search_google":
                            return await HandleGoogleSearch(client, funcCall.Value.args);
                        case "search_memory":
                            return await HandleMemorySearch(client, funcCall.Value.args);
                        case "delete_memories":
                            return await HandleDeleteMemories(client, funcCall.Value.args);
                        default:
                            client.Logger.Log($"Unknown function call: {funcCall.Value.name}");
                            break;
                    }
                }
            }
            return message;
        }

        private static async Task<string> HandleGoogleSearch(GeminiClient client, Dictionary<string, object> args)
        {
            var searchTerms = args.GetValueOrDefault("search_terms", "").ToString();
            if (string.IsNullOrEmpty(searchTerms))
            {
                client.Logger.Log("Empty search terms for Google search");
                return "No query provided for Google search.";
            }

            client.Logger.Log($"Executing Google search: {searchTerms}");
            var results = await client.SearchService.PerformSearch(searchTerms, client.OriginalUserQuery);
            if (!results.Any())
                return $"No relevant results found for '{searchTerms}'.";

            var chunks = ChunkSearchResults(results);
            var combinedResponse = new List<string>();

            foreach (var chunk in chunks)
            {
                var prompt = ToolsAndPrompts.GetProcessedContentPrompt(searchTerms, client.OriginalUserQuery, chunk);
                var response = await SendToLLM(client, prompt);
                if (!string.IsNullOrEmpty(response)) combinedResponse.Add(response);
            }

            var finalResponse = string.Join("\n", combinedResponse);
            client.Logger.Log($"Google search processed, response length: {finalResponse.Length}");
            return finalResponse;
        }

        private static async Task<string> HandleMemorySearch(GeminiClient client, Dictionary<string, object> args)
        {
            var searchTerms = args.GetValueOrDefault("search_terms", "").ToString();
            if (string.IsNullOrEmpty(searchTerms))
            {
                client.Logger.Log("Empty search terms for memory search");
                return "No query provided for memory search.";
            }

            var memories = await client.MemoryManager.SearchMemory(searchTerms);
            if (!memories.Any())
                return $"No memories found for '{searchTerms}'.";

            var chunks = ChunkMemories(memories);
            var combinedResponse = new List<string>();

            foreach (var chunk in chunks)
            {
                var memoryContent = string.Join("\n\n", chunk.Select(m => $"Memory (ID: {m.id}, Score: {m.score:F3}):\n{m.content}"));
                var prompt = $"Found relevant memories:\n\n{memoryContent}\n\nRespond to: '{client.OriginalUserQuery}'.";
                var response = await SendToLLM(client, prompt);
                if (!string.IsNullOrEmpty(response)) combinedResponse.Add(response);
            }

            var finalResponse = string.Join("\n", combinedResponse);
            client.Logger.Log($"Memory search processed, response length: {finalResponse.Length}");
            return finalResponse;
        }

        private static async Task<string> HandleDeleteMemories(GeminiClient client, Dictionary<string, object> args)
        {
            var idsJson = (JsonElement)args["ids"];
            var ids = idsJson.EnumerateArray().Select(id => id.GetInt64()).ToList();
            try
            {
                await Task.Run(() => client.MemoryManager.DeleteMemories(ids));
                return $"Deleted memories with IDs: {string.Join(", ", ids)}.";
            }
            catch (Exception ex)
            {
                client.Logger.Log($"Failed to delete memories: {ex.Message}");
                return $"Failed to delete memories: {ex.Message}";
            }
        }

        private static List<List<(string content, string url)>> ChunkSearchResults(List<(string content, string url)> results)
        {
            const int maxTokens = 32000;
            var chunks = new List<List<(string content, string url)>>();
            var currentChunk = new List<(string content, string url)>();
            int currentLength = 0;

            foreach (var result in results)
            {
                int length = result.content.Length / 4; // Rough token estimate
                if (currentLength + length > maxTokens && currentChunk.Any())
                {
                    chunks.Add(currentChunk);
                    currentChunk = new List<(string content, string url)>();
                    currentLength = 0;
                }
                currentChunk.Add(result);
                currentLength += length;
            }

            if (currentChunk.Any()) chunks.Add(currentChunk);
            return chunks;
        }

        private static List<List<(long id, string content, float score, DateTime createdAt)>> ChunkMemories(List<(long id, string content, float score, DateTime createdAt)> memories)
        {
            const int maxTokens = 32000;
            var chunks = new List<List<(long id, string content, float score, DateTime createdAt)>>();
            var currentChunk = new List<(long id, string content, float score, DateTime createdAt)>();
            int currentLength = 0;

            foreach (var memory in memories)
            {
                int length = memory.content.Length / 4; // Rough token estimate
                if (currentLength + length > maxTokens && currentChunk.Any())
                {
                    chunks.Add(currentChunk);
                    currentChunk = new List<(long id, string content, float score, DateTime createdAt)>();
                    currentLength = 0;
                }
                currentChunk.Add(memory);
                currentLength += length;
            }

            if (currentChunk.Any()) chunks.Add(currentChunk);
            return chunks;
        }

        private static (string? name, Dictionary<string, object>? args)? ExtractFunctionCall(Logger logger, JsonElement functionCallJson)
        {
            try
            {
                if (!functionCallJson.TryGetProperty("functionCall", out var funcCall))
                    return null;

                var name = funcCall.GetProperty("name").GetString();
                var args = JsonSerializer.Deserialize<Dictionary<string, object>>(funcCall.GetProperty("args").ToString());
                return (name, args);
            }
            catch (Exception ex)
            {
                logger.Log($"Error extracting function call: {ex.Message}");
                return null;
            }
        }

        public static async Task<float[]> Embed(GeminiClient client, string text)
        {
            await EmbeddingRateLimiter.WaitAsync();
            try
            {
                var payload = new { content = new { parts = new[] { new { text } } } };
                var (content, _) = await SendRequest(client, payload, false);
                if (string.IsNullOrEmpty(content))
                    return Array.Empty<float>();

                var json = JsonDocument.Parse(content);
                var values = json.RootElement.GetProperty("embedding").GetProperty("values").EnumerateArray()
                    .Select(v => v.GetSingle()).ToArray();

                if (values.Length != ExpectedEmbeddingDimension)
                {
                    client.Logger.Log($"Embedding dimension mismatch: expected {ExpectedEmbeddingDimension}, got {values.Length}");
                    return Array.Empty<float>();
                }

                return values;
            }
            catch (Exception ex)
            {
                client.Logger.Log($"Embedding error: {ex.Message}");
                return Array.Empty<float>();
            }
            finally
            {
                EmbeddingRateLimiter.Release();
            }
        }
    }
}