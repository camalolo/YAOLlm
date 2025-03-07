using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RestSharp;

namespace Gemini
{
    public static class ApiFunctions
    {

        private static async Task<(string?, RestResponse)> SendRequestToLLM(GeminiClient client, object payload)
        {
            var clientRest = new RestClient(client.LlmApiUrl);
            var request = new RestRequest { Method = Method.Post };
            request.AddHeader("Content-Type", "application/json");
            request.AddJsonBody(payload);

            var jsonPayload = JsonSerializer.Serialize(payload);
            client.Logger.Log($"Full request payload: {jsonPayload.Replace("\\n", "\n")}");
            client.UpdateStatus(Status.ReceivingData);

            const int maxRetries = 3;
            int retryCount = 0;
            int delaySeconds = 1;

            while (true)
            {
                try
                {
                    var response = await clientRest.ExecuteAsync(request);
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (retryCount >= maxRetries)
                        {
                            client.Logger.Log("Max retries reached for LLM request due to 429 Too Many Requests");
                            client.UpdateChat("Error: Too many requests to the server. Please try again later.\n", "system");
                            return (null, response);
                        }
                        retryCount++;
                        int retryDelay = delaySeconds * (int)Math.Pow(2, retryCount - 1);
                        client.Logger.Log($"Received 429 Too Many Requests. Retrying in {retryDelay} seconds (attempt {retryCount}/{maxRetries})");
                        client.UpdateChat($"System: Server busy, retrying in {retryDelay} seconds...\n", "system");
                        await Task.Delay(retryDelay * 1000);
                        continue;
                    }
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        client.Logger.Log($"BadRequest (400) received. Response content: {response.Content}");
                        client.Logger.Log($"Request sent: {jsonPayload}");
                        client.UpdateChat("Error: The request was invalid. Please check your input and try again.\n", "system");
                        return (null, response);
                    }
                    response.ThrowIfError();
                    client.Logger.Log($"Raw LLM response: {response.Content}");
                    return (response.Content, response);
                }
                catch (Exception ex)
                {
                    client.Logger.Log($"Error in SendRequestToLLM: {ex.Message}");
                    client.UpdateChat($"Error processing LLM request: {ex.Message}\n", "system");
                    return (null, new RestResponse());
                }
                finally
                {
                    client.UpdateStatus(Status.Idle);
                }
            }
        }

        public static async Task<string?> SendToLLM(GeminiClient client, string prompt, string? imageBase64 = null)
        {
            var userMessage = new Dictionary<string, string> { { "role", "user" }, { "content", prompt } };
            if (imageBase64 != null) userMessage["image"] = imageBase64;
            var messages = new List<Dictionary<string, string>>(client.ConversationHistory) { userMessage };

            if (client.ConversationHistory.Count > GeminiClient.MaxHistoryLength * 2)
            {
                var trimmedHistory = client.ConversationHistory.Skip(client.ConversationHistory.Count - GeminiClient.MaxHistoryLength * 2).ToList();
                client.ConversationHistory.Clear();
                client.ConversationHistory.AddRange(trimmedHistory);
            }

            var payload = new
            {
                contents = messages.Select(m => new
                {
                    role = m["role"],
                    parts = new List<object>
            {
                m.ContainsKey("content") ? new { text = m["content"] } : new object(),
                m.ContainsKey("image") ? new { inlineData = new { mimeType = "image/png", data = m["image"] } } : new object()
            }.Where(p => p.GetType() != typeof(object)).ToList()
                }),
                tools = client.Tools
            };

            client.UpdateChat("\nSystem: Waiting for LLM reply...\n", "system");
            var (content, _) = await SendRequestToLLM(client, payload);
            if (content == null) return null;

            var data = JsonSerializer.Deserialize<JsonElement>(content);
            var llmResponse = ExtractLLMResponse(client.Logger, data);
            var processedResponse = await HandleLLMResponse(client, llmResponse);
            if (!string.IsNullOrEmpty(processedResponse))
            {
                client.ConversationHistory.Add(userMessage);
                client.ConversationHistory.Add(new Dictionary<string, string> { { "role", "model" }, { "content", processedResponse } });
                client.UpdateHistoryCounter();
            }
            return processedResponse;
        }

        public static async Task<string?> SendToLLMWithNoContext(GeminiClient client, string prompt)
        {
            if (string.IsNullOrEmpty(prompt))
            {
                client.Logger.Log("Prompt cannot be empty in SendToLLMWithNoContext");
                return null;
            }

            var messages = new List<Dictionary<string, string>>
    {
        new Dictionary<string, string> { { "role", "user" }, { "content", prompt } }
    };

            var payload = new
            {
                contents = messages.Select(m => new
                {
                    role = m["role"],
                    parts = new[] { new { text = m["content"] } }
                }).ToList()
            };

            var (content, _) = await SendRequestToLLM(client, payload);
            if (content == null) return null;

            var data = JsonSerializer.Deserialize<JsonElement>(content);
            var (message, _) = ExtractLLMResponse(client.Logger, data);
            return message;
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
                    if (funcCall.HasValue)
                    {
                        if (funcCall.Value.name == "search_google")
                        {
                            var searchTerms = funcCall.Value.args["search_terms"].ToString();
                            client.PendingSearchRequest = searchTerms ?? string.Empty;
                            client.Logger.Log($"Initiating search for terms: {searchTerms}");
                            await PerformSearchAndDisplaySummaries(client);
                            return llmResponse.message;
                        }
                        else if (funcCall.Value.name == "search_memory_summaries")
                        {
                            var query = funcCall.Value.args["query"]?.ToString();
                            client.Logger.Log($"Initiating memory summaries search for query: {query}");
                            var summaries = string.IsNullOrEmpty(query)
                                ? new List<(long id, string summary, float score, DateTime createdAt)>()
                                : client.MemoryManager.SearchMemorySummaries(query);
                            if (summaries.Any())
                            {
                                var summaryContent = string.Join("\n\n", summaries.Select(s => $"Summary (ID: {s.id}, score: {s.score:F2}):\n{s.summary}"));
                                var prompt = $"Found the following relevant memory summaries:\n\n{summaryContent}\n\nIf these summaries are relevant, fetch the full content using 'search_memory_content' with the IDs, or respond directly if sufficient. The user question was '{client.OriginalUserQuery}'";
                                return await SendToLLM(client, prompt) ?? "The LLM did not reply.";
                            }
                            else
                            {
                                client.Logger.Log($"No relevant memory summaries found for query '{query}'");
                                var prompt = $"No memories summaries found for '{query}'. You might try a slightly different query, or give up on searching.";
                                return await SendToLLM(client, prompt) ?? "The LLM did not reply.";

                            }
                        }
                        else if (funcCall.Value.name == "search_memory_content")
                        {
                            var idsJson = (JsonElement)funcCall.Value.args["ids"];
                            var ids = idsJson.EnumerateArray().Select(id => id.GetInt64()).ToList();
                            client.Logger.Log($"Initiating memory content search for IDs: [{string.Join(", ", ids)}]");
                            var memories = ids.Any() ? client.MemoryManager.FetchMemoryContent(ids) : new List<string>();
                            if (memories.Any())
                            {
                                var memoryContent = string.Join("\n\n", memories.Select(m => $"Memory :\n{m}"));
                                var prompt = $"Found the following relevant memory content:\n\n{memoryContent}\n\nBased on these memories, please provide a helpful response to the user query : '{client.OriginalUserQuery}'.";
                                return await SendToLLM(client, prompt) ?? "No relevant information found in memory content.";
                            }
                            else
                            {
                                client.Logger.Log($"No relevant content found for IDs [{string.Join(", ", ids)}]");
                                var prompt = $"No memories content found with IDs [{string.Join(", ", ids)}]. You might try a slightly different query, or give up on searching.";
                                return await SendToLLM(client, prompt) ?? "No relevant information found in memory content.";
                            }
                        }
                    }
                }
            }
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
                return null;
            }
        }

        private static async Task PerformSearchAndDisplaySummaries(GeminiClient client)
        {
            if (string.IsNullOrEmpty(client.PendingSearchRequest))
            {
                client.UpdateChat("No pending search request.\n", "system");
                client.Logger.Log("No pending search request to process");
                return;
            }

            string searchTerms = client.PendingSearchRequest;
            client.PendingSearchRequest = string.Empty;
            client.OriginalUserQueryInternal ??= "the user's question";

            try
            {
                var contentUrlPairs = await client.Search.PerformSearch(searchTerms, client.OriginalUserQueryInternal, client.UpdateChat, client.UpdateStatus);
                if (contentUrlPairs.Any())
                {
                    var prompt = ToolsAndPrompts.GetProcessedContentPrompt(searchTerms, client.OriginalUserQueryInternal, contentUrlPairs);
                    await client.ProcessLLMRequest(prompt);
                }
                else
                {
                    await client.ProcessLLMRequest(ToolsAndPrompts.GetNoRelevantResultsPrompt(client.PendingSearchRequest, client.OriginalUserQueryInternal));
                }
            }
            catch (Exception ex)
            {
                client.Logger.Log($"Error in PerformSearchAndDisplaySummaries: {ex.Message}");
                client.UpdateChat($"Error during search: {ex.Message}\n", "system");
            }
            finally
            {
                client.UpdateStatus(Status.Idle);
            }
        }
    }
}