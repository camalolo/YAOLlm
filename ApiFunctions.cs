using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        private static readonly SemaphoreSlim RateLimiterEmbedding = new SemaphoreSlim(EmbeddingRpmLimit, EmbeddingRpmLimit);
        private static readonly SemaphoreSlim RateLimiterGeneration = new SemaphoreSlim(GenerationRpmLimit, GenerationRpmLimit);
        private static readonly TimeSpan RateLimitReset = TimeSpan.FromSeconds(60);
        private const int ExpectedEmbeddingDimension = 768;

        private static async Task<(string?, RestResponse)> SendRequestToLLM(GeminiClient client, object payload, bool embed = false)
        {
            var url = embed ? client.getEmbedUrl() : client.getUrl();
            var clientRest = new RestClient(url);
            var request = new RestRequest { Method = Method.Post };
            request.AddHeader("Content-Type", "application/json");

            var finalPayload = embed
                ? new { content = new { parts = new[] { new { text = payload } } } }
                : payload;

            var jsonPayload = JsonSerializer.Serialize(finalPayload);
            client.Logger.Log($"Payload: {jsonPayload}");

            request.AddJsonBody(finalPayload);

            client.Logger.Log($"Sending request to {url}");
            client.UpdateStatus(Status.ReceivingData);

            const int maxRetries = 3;
            var rateLimiter = embed ? RateLimiterEmbedding : RateLimiterGeneration;

            try
            {
                await rateLimiter.WaitAsync();
                try
                {
                    for (int retryCount = 0; retryCount <= maxRetries; retryCount++)
                    {
                        try
                        {
                            if (retryCount == 0 && embed) await Task.Delay(100);
                            var response = await clientRest.ExecuteAsync(request);
                            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                            {
                                if (retryCount == maxRetries) return (null, response);
                                int retryDelay = (int)Math.Pow(2, retryCount) * 1000;
                                client.Logger.Log($"429 Too Many Requests. Retrying in {retryDelay / 1000} seconds (attempt {retryCount + 1}/{maxRetries})");
                                await Task.Delay(retryDelay);
                                continue;
                            }
                            response.ThrowIfError();
                            // Modify the response log if it contains an embedding section
                            string modifiedResponse = response.Content ?? "";
                            try
                            {
                                // Deserialize the raw response into a dictionary
                                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(modifiedResponse);
                                if (dict != null && dict.TryGetValue("embedding", out var embObj))
                                {
                                    if (embObj is JsonElement embElem &&
                                        embElem.TryGetProperty("values", out var valuesElement) &&
                                        valuesElement.ValueKind == JsonValueKind.Array)
                                    {
                                        int count = valuesElement.GetArrayLength();
                                        // Replace the embedding section with a summary of the count
                                        dict["embedding"] = $"{count} values not shown in the log";
                                        modifiedResponse = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                client.Logger.Log($"Error processing embedding section: {ex.Message}");
                            }
                            client.Logger.Log($"Raw LLM response: {modifiedResponse}");
                            return (response.Content, response);
                        }
                        catch (Exception ex)
                        {
                            client.Logger.Log($"Error in SendRequestToLLM: {ex.Message}");
                            return (null, new RestResponse());
                        }
                    }
                }
                finally
                {
                    rateLimiter.Release();
                }
            }
            finally
            {
                client.UpdateStatus(Status.Idle);
            }
            return (null, new RestResponse());
        }

        public static async Task<string?> SendToLLM(GeminiClient client, string prompt, string? imageBase64 = null, bool useHistory = true)
        {
            var userMessage = new Dictionary<string, string> { { "role", "user" } };
            if (!string.IsNullOrEmpty(prompt)) userMessage["content"] = prompt;
            if (imageBase64 != null) userMessage["image"] = imageBase64;

            var messages = useHistory ? new List<Dictionary<string, string>>(client.ConversationHistory) { userMessage }
                                      : new List<Dictionary<string, string>> { userMessage };

            // Trim history if needed, but preserve recent context
            if (useHistory && messages.Count > GeminiClient.MaxHistoryLength * 4)
            {
                client.Logger.Log($"Trimming history from {messages.Count} to {GeminiClient.MaxHistoryLength * 2} entries");
                messages = messages.Skip(messages.Count - GeminiClient.MaxHistoryLength * 2).ToList();
            }

            var payload = new
            {
                contents = messages.Select(m =>
                {
                    var parts = new List<object>();
                    if (m.ContainsKey("content")) parts.Add(new { text = m["content"] });
                    if (m.ContainsKey("image")) parts.Add(new { inlineData = new { mimeType = "image/png", data = m["image"] } });
                    return new { role = m["role"], parts };
                }),
                tools = client.Tools
            };

            client.Logger.Log($"Sending payload to LLM (prompt length: {prompt.Length} chars, history entries: {messages.Count})");
            var (content, _) = await SendRequestToLLM(client, payload);
            if (content == null)
            {
                client.Logger.Log("LLM returned null content");
                return null;
            }

            var data = JsonSerializer.Deserialize<JsonElement>(content);
            var llmResponse = ExtractLLMResponse(client.Logger, data);
            var processedResponse = await HandleLLMResponse(client, llmResponse);

            if (!string.IsNullOrEmpty(processedResponse) && useHistory)
            {
                // Update history only once, avoiding duplicates
                var updatedHistory = new List<Dictionary<string, string>>(messages)
        {
            userMessage,
            new Dictionary<string, string> { { "role", "model" }, { "content", processedResponse } }
        };
                client.ConversationHistory = updatedHistory; // Use setter
                client.Logger.Log($"History updated, new length: {client.ConversationHistory.Count}");
                client.UpdateHistoryCounter();
            }
            return processedResponse;
        }

        private static (string message, List<JsonElement> functionCalls) ExtractLLMResponse(Logger logger, JsonElement data)
        {
            if (data.TryGetProperty("candidates", out var candidates) && candidates.EnumerateArray().Any())
            {
                var messageParts = new List<string>();
                var functionCalls = new List<JsonElement>();
                foreach (var candidate in candidates.EnumerateArray())
                {
                    foreach (var part in candidate.GetProperty("content").GetProperty("parts").EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text) && text.GetString() != null)
                            messageParts.Add(text.GetString()!);
                        else if (part.TryGetProperty("functionCall", out var func))
                            functionCalls.Add(part);
                    }
                }
                logger.Log($"Extracted message: {string.Join("", messageParts)}, Function calls count: {functionCalls.Count}");
                return (string.Join("", messageParts), functionCalls);
            }
            logger.Log("No candidates found in LLM response");
            return (string.Empty, new List<JsonElement>());
        }

        private static async Task<string> HandleLLMResponse(GeminiClient client, (string message, List<JsonElement> functionCalls) llmResponse)
        {
            client.Logger.Log($"Handling LLM response - Message: {llmResponse.message}, Function calls: {llmResponse.functionCalls.Count}");

            if (llmResponse.functionCalls?.Any() == true)
            {
                if (!string.IsNullOrEmpty(llmResponse.message))
                {
                    client.UpdateChat($"{llmResponse.message}\n", "model");
                }

                foreach (var func in llmResponse.functionCalls)
                {
                    var funcCall = ExtractFunctionCall(client.Logger, func);
                    if (!funcCall.HasValue) continue;

                    if (funcCall.Value.name == "search_google")
                    {
                        var searchTerms = funcCall.Value.args["search_terms"].ToString() ?? string.Empty;
                        client.Logger.Log($"Initiating google search for terms: {searchTerms}");

                        if (string.IsNullOrEmpty(searchTerms))
                        {
                            client.Logger.Log("No valid query provided for google search.");
                            return "No query provided to search google. Please provide a query or refine your request.";
                        }

                        var contentUrlPairs = await client.Search.PerformSearch(searchTerms, client.OriginalUserQuery, client.UpdateChat, client.UpdateStatus);
                        client.UpdateStatus(Status.Idle);

                        if (contentUrlPairs.Any())
                        {
                            client.Logger.Log($"Search returned {contentUrlPairs.Count} results:");
                            foreach (var (content, url) in contentUrlPairs)
                            {
                                client.Logger.Log($"URL: {url}, Content length: {content.Length} chars, Preview: {content.Substring(0, Math.Min(100, content.Length))}...");
                            }

                            var prompt = ToolsAndPrompts.GetProcessedContentPrompt(searchTerms, client.OriginalUserQuery, contentUrlPairs);
                            client.Logger.Log($"Prompt sent to LLM (length: {prompt.Length} chars): {prompt.Substring(0, Math.Min(500, prompt.Length))}...");

                            // Recursive call with history disabled to avoid nesting issues
                            var llmResponseText = await SendToLLM(client, prompt, null, false);
                            if (!string.IsNullOrEmpty(llmResponseText))
                            {
                                client.Logger.Log($"LLM response (length: {llmResponseText.Length} chars): {llmResponseText.Substring(0, Math.Min(500, llmResponseText.Length))}...");

                                // Append search summary as a system message
                                var searchSummary = $"System: Search results for '{searchTerms}' have been stored. Found {contentUrlPairs.Count} items:\n" +
                                                    string.Join("\n", contentUrlPairs.Select(p => $"- {p.url} (Content preview: {p.content.Substring(0, Math.Min(100, p.content.Length))}...)"));
                                var updatedHistory = new List<Dictionary<string, string>>(client.ConversationHistory)
                        {
                            new Dictionary<string, string> { { "role", "system" }, { "content", searchSummary } }
                        };
                                client.ConversationHistory = updatedHistory; // Use setter
                                client.UpdateHistoryCounter();

                                return llmResponseText;
                            }
                            else
                            {
                                client.Logger.Log("LLM returned null or empty response, using fallback");
                                return $"Search for '{searchTerms}' yielded {contentUrlPairs.Count} results, but no response was generated.";
                            }
                        }
                        else
                        {
                            client.Logger.Log($"No relevant results for '{searchTerms}', returning fallback response");
                            return $"No relevant information found online for '{searchTerms}'. Please refine your query.";
                        }
                    }
                    else if (funcCall.Value.name == "search_memory")
                    {
                        var searchTerms = funcCall.Value.args["search_terms"].ToString() ?? string.Empty;
                        client.Logger.Log($"Initiating memory search for terms: {searchTerms}");

                        if (string.IsNullOrEmpty(searchTerms))
                        {
                            client.Logger.Log("No valid query provided for memory search.");
                            var prompt = "No query provided to search memory content. Please provide a query or refine your request.";
                            return await SendToLLM(client, prompt) ?? "No relevant information found in memory content.";
                        }

                        client.Logger.Log($"Initiating memory search for query: '{searchTerms}'");
                        var memories = await client.MemoryManager.SearchMemory(searchTerms);

                        if (memories.Any())
                        {
                            var memoryContent = string.Join("\n\n", memories.Select(m => $"Memory (ID: {m.id}, Score: {m.score:F3}):\n{m.content}"));
                            var prompt = $"Found the following relevant memory content:\n\n{memoryContent}\n\nBased on these memories, please provide a helpful response to the user query: '{client.OriginalUserQuery}'.";

                            // Append memory search results to history
                            var memorySummary = $"System: Memory search for '{searchTerms}' returned {memories.Count} results:\n" +
                                                string.Join("\n", memories.Select(m => $"- ID: {m.id}, Score: {m.score:F3}, Preview: {m.content.Substring(0, Math.Min(100, m.content.Length))}..."));
                            var updatedHistory = new List<Dictionary<string, string>>(client.ConversationHistory)
                                {
                                    new Dictionary<string, string> { { "role", "system" }, { "content", memorySummary } }
                                };
                            client.ConversationHistory = updatedHistory; // Use property setter
                            client.UpdateHistoryCounter();

                            return await SendToLLM(client, prompt) ?? "No relevant information found in memory content.";
                        }
                        else
                        {
                            client.Logger.Log($"No relevant memories found for query '{searchTerms}'");
                            var prompt = $"No memories found matching the query '{searchTerms}'. You might try a slightly different query.";
                            return await SendToLLM(client, prompt) ?? "No relevant information found in memory content.";
                        }
                    }
                    else if (funcCall.Value.name == "delete_memories")
                    {
                        var idsJson = (JsonElement)funcCall.Value.args["ids"];
                        var ids = idsJson.EnumerateArray().Select(id => id.GetInt64()).ToList();
                        client.Logger.Log($"Deleting memories with IDs: [{string.Join(", ", ids)}]");
                        try
                        {
                            client.MemoryManager.DeleteMemories(ids);
                            var prompt = $"Successfully deleted memories with IDs [{string.Join(", ", ids)}].";
                            return await SendToLLM(client, prompt) ?? "Couldn't delete memories.";
                        }
                        catch (Exception ex)
                        {
                            client.UpdateChat($"Failed to delete memories with IDs: {string.Join(", ", ids)}. Error: {ex.Message}\n", "system");
                            var prompt = $"Failed to delete memories with IDs [{string.Join(", ", ids)}]. Error: {ex.Message}";
                            return await SendToLLM(client, prompt) ?? "Couldn't delete memories.";
                        }
                    }
                }
            }
            // Default return when no function calls are present
            return llmResponse.message;
        }

        private static (string name, Dictionary<string, object> args)? ExtractFunctionCall(Logger logger, JsonElement functionCallJson)
        {
            try
            {
                logger.Log($"Extracting function call from JSON: {functionCallJson.ToString()}");
                if (!functionCallJson.TryGetProperty("functionCall", out var funcCallElement))
                {
                    logger.Log("No 'functionCall' property found in JSON");
                    return null;
                }

                var name = funcCallElement.GetProperty("name").GetString();
                var args = JsonSerializer.Deserialize<Dictionary<string, object>>(funcCallElement.GetProperty("args").ToString());

                if (name != null && args != null)
                {
                    logger.Log($"Successfully extracted function call: name={name}, args={args.Count} entries");
                    return (name, args);
                }

                logger.Log("Function call extraction failed: name or args is null");
                return null;
            }
            catch (Exception ex)
            {
                logger.Log($"Error extracting function call: {ex.Message}");
                throw;
            }
        }

        public static async Task<float[]> Embed(GeminiClient client, string text)
        {
            try
            {
                var (content, _) = await SendRequestToLLM(client, text, true);

                if (content == null)
                {
                    client.Logger.Log("Embedding API returned null content");
                    return Array.Empty<float>();
                }

                var json = JsonDocument.Parse(content);
                var root = json.RootElement;

                if (!root.TryGetProperty("embedding", out var embeddingElement) ||
                    !embeddingElement.TryGetProperty("values", out var valuesElement))
                {
                    client.Logger.Log($"Invalid embedding response format: {content}");
                    return Array.Empty<float>();
                }

                var embeddings = valuesElement.EnumerateArray()
                                             .Select(v => v.GetSingle())
                                             .ToArray();
                if (embeddings.Length != ExpectedEmbeddingDimension)
                {
                    client.Logger.Log($"Embedding dimension mismatch: expected {ExpectedEmbeddingDimension}, got {embeddings.Length}");
                    return Array.Empty<float>();
                }
                client.Logger.Log($"Successfully retrieved embedding with {embeddings.Length} dimensions");
                return embeddings;
            }
            catch (Exception ex)
            {
                client.Logger.Log($"Error in Embed: {ex.Message}");
                return Array.Empty<float>();
            }
        }
    }
}