using System;
using System.Text.Json;
using System.Threading.Tasks;
using RestSharp;

namespace GeminiDotnet
{
    public static class ApiFunctions
    {
        private const int GenerationRpmLimit = 15;
        private static readonly SemaphoreSlim GenerationRateLimiter = new(GenerationRpmLimit, GenerationRpmLimit);

        public static async Task<string> SendToLLM(GeminiClient client, object payload, string? imageBase64 = null)
        {
            var logger = client.Logger;
            
            // Define the web_search tool with proper function declaration
            var tools = new List<object>
            {
                new
                {
                    functionDeclarations = new[]
                    {
                        new
                        {
                            name = "web_search",
                            description = "Search the web for up-to-date information using Tavily",
                            parameters = new
                            {
                                type = "object",
                                properties = new
                                {
                                    query = new
                                    {
                                        type = "string",
                                        description = "The search query"
                                    },
                                    max_results = new
                                    {
                                        type = "integer",
                                        description = "Maximum number of results (default: 5)"
                                    }
                                },
                                required = new[] { "query" }
                            }
                        }
                    }
                }
            };

            object[] contents = payload switch
            {
                List<Dictionary<string, string>> messages => messages.Select(m =>
                {
                    var parts = new List<object>();
                    if (m.ContainsKey("content")) parts.Add(new { text = m["content"] });
                    if (m.ContainsKey("image")) parts.Add(new { inlineData = new { mimeType = "image/png", data = m["image"] } });
                    return (object)new { role = m["role"], parts = parts.ToArray() };
                }).ToArray(),
                string text => new[] { (object)new { role = "user", parts = new[] { new { text } } } },
                _ => throw new ArgumentException("Invalid payload type")
            };

            var enhancedPayload = new { contents, tools };
            var jsonPayload = JsonSerializer.Serialize(enhancedPayload);
            logger.Log($"Preparing LLM request with payload: {jsonPayload.Substring(0, Math.Min(500, jsonPayload.Length))}...");

            var (content, response) = await SendRequest(client, enhancedPayload);
            if (content == null)
            {
                logger.Log($"LLM request failed: StatusCode={response.StatusCode}, Error={response.ErrorMessage}");
                return string.Empty;
            }

            var data = JsonSerializer.Deserialize<JsonElement>(content);
            string message = await ExtractResponseAsync(logger, data, client, contents);
            logger.Log($"Response extracted: '{message.Substring(0, Math.Min(100, message.Length))}...'");
            return message;
        }

        private static async Task<(string?, RestResponse)> SendRequest(GeminiClient client, object payload)
        {
            var url = client.GetGenerateUrl();
            var restClient = new RestClient(url);
            var request = new RestRequest { Method = Method.Post };
            request.AddHeader("Content-Type", "application/json");
            request.AddJsonBody(payload);

            client.UpdateStatus(Status.Receiving);
            const int maxRetries = 3;

            await GenerationRateLimiter.WaitAsync();
            try
            {
                for (int retry = 0; retry <= maxRetries; retry++)
                {
                    var response = await restClient.ExecuteAsync(request);

                    client.Logger.Log(response.Content ?? "Empty response !");

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
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
                    client.Logger.Log($"Response received: {response.Content?.Substring(0, Math.Min(500, response.Content?.Length ?? 0))}...");
                    return (response.Content, response);
                }
            }
            catch (Exception ex)
            {
                client.Logger.Log($"SendRequest error: {ex.Message}");
                return (null, new RestResponse());
            }
            finally
            {
                GenerationRateLimiter.Release();
                client.UpdateStatus(Status.Idle);
            }
            return (null, new RestResponse());
        }

        private static async Task<string> ExtractResponseAsync(Logger logger, JsonElement data, GeminiClient client, object[] originalContents)
        {
            if (!data.TryGetProperty("candidates", out var candidates) || !candidates.EnumerateArray().Any())
            {
                logger.Log("No candidates in LLM response");
                return string.Empty;
            }

            var messageParts = new List<string>();
            var tavilyService = new TavilySearchService(client.TavilyApiKey, client.Logger);
            JsonElement? modelContentElement = null;

            foreach (var candidate in candidates.EnumerateArray())
            {
                // Check if this candidate has function calls
                if (candidate.TryGetProperty("content", out var contentElement))
                {
                    // Save the complete model content for conversation history
                    modelContentElement = contentElement;

                    foreach (var part in contentElement.GetProperty("parts").EnumerateArray())
                    {
                        // Check for text content
                        if (part.TryGetProperty("text", out var text) && text.GetString() is string textValue)
                        {
                            messageParts.Add(textValue);
                        }
                        // Check for function call
                        else if (part.TryGetProperty("functionCall", out var functionCall))
                        {
                            var functionName = functionCall.TryGetProperty("name", out var nameElement) 
                                ? nameElement.GetString() 
                                : null;
                            
                            if (functionName == "web_search")
                            {
                                // Extract function arguments
                                var argsJson = functionCall.TryGetProperty("args", out var argsElement)
                                    ? argsElement.GetRawText()
                                    : "{}";
                                
                                try
                                {
                                    var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                                    var query = args?.TryGetValue("query", out var queryElement) == true 
                                        ? queryElement.GetString() 
                                        : null;
                                    
                                    var maxResults = args?.TryGetValue("max_results", out var maxResultsElement) == true
                                        ? maxResultsElement.GetInt32()
                                        : 5;

                                    if (!string.IsNullOrEmpty(query))
                                    {
                                        logger.Log($"Executing web_search with query: '{query}', maxResults: {maxResults}");
                                        
                                        // Execute the search
                                        var searchResults = await tavilyService.SearchAsync(query, maxResults);
                                        
                                        // Build complete conversation history: original contents + model's content + function response
                                        var conversationHistory = new List<object>(originalContents);
                                        
                                        // Add the model's complete content (preserving thought_signature and other fields)
                                        if (modelContentElement.HasValue)
                                        {
                                            // Deserialize the content element to preserve all fields including thought_signature
                                            var contentObj = JsonSerializer.Deserialize<JsonElement>(modelContentElement.Value.GetRawText());
                                            var parts = new List<object>();
                                            
                                            foreach (var contentPart in contentObj.GetProperty("parts").EnumerateArray())
                                            {
                                                parts.Add(JsonSerializer.Deserialize<object>(contentPart.GetRawText())!);
                                            }
                                            
                                            var modelContent = new
                                            {
                                                role = "model",
                                                parts = parts.ToArray()
                                            };
                                            conversationHistory.Add(modelContent);
                                        }
                                        
                                        // Add the function response
                                        conversationHistory.Add(new
                                        {
                                            role = "function",
                                            parts = new[]
                                            {
                                                new
                                                {
                                                    functionResponse = new
                                                    {
                                                        name = "web_search",
                                                        response = new
                                                        {
                                                            result = searchResults
                                                        }
                                                    }
                                                }
                                            }
                                        });

                                        // Send complete conversation back to Gemini
                                        var functionResponsePayload = new
                                        {
                                            contents = conversationHistory.ToArray()
                                        };

                                        var (functionContent, functionResponse) = await SendRequest(client, functionResponsePayload);
                                        if (functionContent != null)
                                        {
                                            var functionData = JsonSerializer.Deserialize<JsonElement>(functionContent);
                                            var functionMessage = ExtractSimpleResponse(logger, functionData);
                                            if (!string.IsNullOrEmpty(functionMessage))
                                            {
                                                messageParts.Add(functionMessage);
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.Log($"Error executing function call: {ex.Message}");
                                    messageParts.Add($"Error executing web search: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }

            return string.Join("<br><br>", messageParts);
        }

        private static string ExtractSimpleResponse(Logger logger, JsonElement data)
        {
            if (!data.TryGetProperty("candidates", out var candidates) || !candidates.EnumerateArray().Any())
            {
                return string.Empty;
            }

            var messageParts = new List<string>();
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (candidate.TryGetProperty("content", out var contentElement))
                {
                    foreach (var part in contentElement.GetProperty("parts").EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text) && text.GetString() is string textValue)
                        {
                            messageParts.Add(textValue);
                        }
                    }
                }
            }

            return string.Join("<br><br>", messageParts);
        }
    }
}