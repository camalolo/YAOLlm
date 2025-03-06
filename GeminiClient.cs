using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RestSharp;
using dotenv.net;

namespace Gemini
{
    public class GeminiClient
    {
        private readonly Logger _logger;
        private readonly string _apiKey;
        private readonly Search _search;
        private readonly MemoryManager _memoryManager;
        private const int MaxHistoryLength = 32;
        private string _llmApiModel = "gemini-2.0-flash";
        private string _llmApiUrl => $"https://generativelanguage.googleapis.com/v1beta/models/{_llmApiModel}:generateContent?key={_apiKey}";
        private List<Dictionary<string, string>> _conversationHistory;
        private readonly List<object> _tools;
        private string _pendingSearchRequest;
        private string _originalUserQuery;

        public Action<string, string> UpdateChat { get; set; }
        public Action UpdateHistoryCounter { get; set; }
        public Action<Status> UpdateStatus { get; set; }
        public string OriginalUserQuery { get => _originalUserQuery; set => _originalUserQuery = value; }

        public GeminiClient(Logger logger)
        {
            _logger = logger;
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var geminiFile = Path.Combine(homeDir, ".gemini");

            try
            {
                DotEnv.Load(new DotEnvOptions(envFilePaths: new[] { geminiFile }));
                _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? throw new ArgumentException("GEMINI_API_KEY is not set");
                var googleSearchApiKey = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_API_KEY") ?? throw new ArgumentException("GOOGLE_SEARCH_API_KEY is not set");
                var googleSearchEngineId = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_ENGINE_ID") ?? throw new ArgumentException("GOOGLE_SEARCH_ENGINE_ID is not set");
                _memoryManager = new MemoryManager(logger, this);
                _search = new Search(logger, googleSearchApiKey, googleSearchEngineId, CreateMemoryFromSearchResults);
            }
            catch (Exception ex)
            {
                _logger.Log($"Error loading .gemini file: {ex.Message}");
                throw new ArgumentException("Failed to load environment variables from .gemini", ex);
            }

            _conversationHistory = new List<Dictionary<string, string>> { new() { { "role", "model" }, { "content", GetInitialPrompt() } } };
            _tools = DefineTools();
            _pendingSearchRequest = string.Empty;
            _originalUserQuery = string.Empty;
            UpdateChat = (_, __) => { };
            UpdateHistoryCounter = () => { };
            UpdateStatus = (_) => { };
        }

        public async Task<string> GenerateSummary(string content)
        {
            try
            {
                var prompt = $"Summarize the following content into a concise, keyword-rich one-sentence summary:\n\n{content}";
                return await SendToLLMWithNoContext(prompt) ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error generating summary: {ex.Message}");
                return string.Empty;
            }
        }

        public void SetUICallbacks(Action<string, string> updateChat, Action updateHistoryCounter, Action<Status> updateStatus)
        {
            UpdateChat = updateChat;
            UpdateHistoryCounter = updateHistoryCounter;
            UpdateStatus = updateStatus;
        }

        public void UpdateModel(string model)
        {
            _llmApiModel = model;
            ClearConversationHistory();
        }

        public int GetConversationHistoryLength()
        {
            return _conversationHistory.Sum(turn => turn["content"].Length);
        }

        public void ClearConversationHistory()
        {
            _conversationHistory = new List<Dictionary<string, string>> { new() { { "role", "model" }, { "content", GetInitialPrompt() } } };
            UpdateHistoryCounter();
        }

        private List<object> DefineTools()
        {
            return new List<object>
            {
                new
                {
                    function_declarations = new object[]
                    {
                        new
                        {
                            name = "search_google",
                            description = "Useful when you need to answer questions you don't have the answer to, especially real-time information.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { search_terms = new { type = "string", description = "The search query to use." } },
                                required = new[] { "search_terms" }
                            }
                        },
                        new
                        {
                            name = "search_memory",
                            description = "Searches for relevant memories stored locally before performing an online search. Should be used first.",
                            parameters = new
                            {
                                type = "object",
                                properties = new { query = new { type = "string", description = "The query to search for in memories." } },
                                required = new[] { "query" }
                            }
                        }
                    }
                }
            };
        }

        public async Task ProcessLLMRequest(string prompt, string? imageBase64 = null, string? activeWindowTitle = null)
        {
            try
            {
                _logger.Log($"Processing LLM request with prompt: {prompt}");
                UpdateStatus(Status.SendingData);
                if (!string.IsNullOrEmpty(imageBase64))
                {
                    UpdateStatus(Status.AnalyzingImage);
                    string describePrompt = GetImageDescriptionPrompt(activeWindowTitle ?? string.Empty);
                    var response = await SendToLLM(describePrompt, imageBase64);
                    if (response != null) UpdateChat("\nImage analysis done.\n", "model");

                    prompt = string.IsNullOrEmpty(prompt) ? GetImageDescriptionDefaultUserPrompt() : prompt;
                    UpdateChat("\nProcessing image...\n", "system");
                    response = await SendToLLM(prompt, imageBase64);
                    if (response != null) UpdateChat(response + "\n", "model");
                }
                else
                {
                    var response = await SendToLLM(prompt);
                    if (response != null) UpdateChat(response + "\n", "model");
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in ProcessLLMRequest: {ex.Message}");
                UpdateChat($"Error: {ex.Message}\n", "system");
            }
            finally
            {
                UpdateStatus(Status.Idle);
            }
        }

        private async Task<string?> SendToLLM(string prompt, string? imageBase64 = null)
        {
            var userMessage = new Dictionary<string, string> { { "role", "user" }, { "content", prompt } };
            if (imageBase64 != null) userMessage["image"] = imageBase64;
            var messages = new List<Dictionary<string, string>>(_conversationHistory) { userMessage };

            if (_conversationHistory.Count > MaxHistoryLength * 2)
                _conversationHistory = _conversationHistory.Skip(_conversationHistory.Count - MaxHistoryLength * 2).ToList();

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
                tools = _tools
            };

            var client = new RestClient(_llmApiUrl);
            var request = new RestRequest { Method = Method.Post };
            request.AddHeader("Content-Type", "application/json");
            request.AddJsonBody(payload);

            var jsonPayload = JsonSerializer.Serialize(payload);
            _logger.Log($"Full request payload: {jsonPayload}");

            UpdateChat("\nSystem: Waiting for LLM reply...\n", "system");
            UpdateStatus(Status.ReceivingData);

            const int maxRetries = 3;
            int retryCount = 0;
            int delaySeconds = 1;

            while (true)
            {
                try
                {
                    var response = await client.ExecuteAsync(request);
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (retryCount >= maxRetries)
                        {
                            _logger.Log("Max retries reached for LLM request due to 429 Too Many Requests");
                            UpdateChat("Error: Too many requests to the server. Please try again later.\n", "system");
                            return null;
                        }

                        retryCount++;
                        int retryDelay = delaySeconds * (int)Math.Pow(2, retryCount - 1);
                        _logger.Log($"Received 429 Too Many Requests. Retrying in {retryDelay} seconds (attempt {retryCount}/{maxRetries})");
                        UpdateChat($"System: Server busy, retrying in {retryDelay} seconds...\n", "system");
                        await Task.Delay(retryDelay * 1000);
                        continue;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        _logger.Log($"BadRequest (400) received. Response content: {response.Content}");
                        _logger.Log($"Request sent : {payload}");
                        UpdateChat("Error: The request was invalid. Please check your input and try again.\n", "system");
                        return null;
                    }

                    response.ThrowIfError();
                    _logger.Log($"Raw LLM response: {response.Content}");
                    var data = response.Content != null ? JsonSerializer.Deserialize<JsonElement>(response.Content) : default;
                    var llmResponse = ExtractLLMResponse(data);

                    var processedResponse = await HandleLLMResponse(llmResponse);
                    if (!string.IsNullOrEmpty(processedResponse))
                    {
                        _conversationHistory.Add(userMessage);
                        _conversationHistory.Add(new Dictionary<string, string> { { "role", "model" }, { "content", processedResponse } });
                        UpdateHistoryCounter();
                        return processedResponse;
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error in SendToLLM: {ex.Message}");
                    UpdateChat($"Error processing LLM request: {ex.Message}\n", "system");
                    return null;
                }
                finally
                {
                    UpdateStatus(Status.Idle);
                }
            }
        }

        private async Task<string?> SendToLLMWithNoContext(string prompt)
        {
            try
            {
                if (string.IsNullOrEmpty(prompt))
                {
                    _logger.Log("Prompt cannot be empty in SendToLLMWithNoContext");
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

                var jsonPayload = JsonSerializer.Serialize(payload);
                _logger.Log($"Sending payload to LLM in SendToLLMWithNoContext: {jsonPayload}");

                var client = new RestClient(_llmApiUrl);
                var request = new RestRequest { Method = Method.Post };
                request.AddHeader("Content-Type", "application/json");
                request.AddJsonBody(payload);

                UpdateStatus(Status.ReceivingData);

                var response = await client.ExecuteAsync(request);
                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    _logger.Log($"BadRequest (400) received in SendToLLMWithNoContext. Response content: {response.Content}");
                    _logger.Log($"Request payload: {jsonPayload}");
                    return null;
                }

                response.ThrowIfError();
                _logger.Log($"Raw LLM response in SendToLLMWithNoContext: {response.Content}");

                var data = response.Content != null ? JsonSerializer.Deserialize<JsonElement>(response.Content) : default;
                var (message, _) = ExtractLLMResponse(data);
                return message;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in SendToLLMWithNoContext: {ex.Message}");
                return null;
            }
            finally
            {
                UpdateStatus(Status.Idle);
            }
        }

        private async void CreateMemoryFromSearchResults(List<(string content, string url)> results)
        {
            try
            {
                var contentWithUrls = string.Join("\n\n", results.Select(p => $"Content from: {p.url}\n\n{p.content}"));
                //var prompt = $"Summarize and organize the following search results into a concise, well-structured memory entry. Focus on key information and clarity, and enhance with your own knowledge if relevant.\n\n{contentWithUrls}";

                //var summary = await SendToLLMWithNoContext(prompt);
                //if (!string.IsNullOrEmpty(summary))
                //{
                    //await _memoryManager.StoreMemory(summary);
                    await _memoryManager.StoreMemory(contentWithUrls);
                    _logger.Log("Successfully created and stored memory from search results");
                //}
                //else
                //{
                //    _logger.Log("Failed to create memory: LLM returned empty summary");
                //}
            }
            catch (Exception ex)
            {
                _logger.Log($"Error creating memory from search results: {ex.Message}");
            }
        }

        private (string message, List<JsonElement> functionCalls) ExtractLLMResponse(JsonElement data)
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
                _logger.Log($"Extracted message: {string.Join("", messageParts)}, Function calls count: {functionCalls.Count}");
                return (string.Join("", messageParts), functionCalls);
            }
            _logger.Log("No candidates found in LLM response");
            return (string.Empty, new List<JsonElement>());
        }

        private async Task<string> HandleLLMResponse((string message, List<JsonElement> functionCalls) llmResponse)
        {
            _logger.Log($"Handling LLM response - Message: {llmResponse.message}, Function calls: {llmResponse.functionCalls.Count}");

            if (llmResponse.functionCalls?.Any() == true)
            {
                if (!string.IsNullOrEmpty(llmResponse.message))
                {
                    UpdateChat($"{llmResponse.message}\n", "model");
                }

                foreach (var func in llmResponse.functionCalls)
                {
                    var funcCall = ExtractFunctionCall(func);
                    if (funcCall.HasValue)
                    {
                        if (funcCall.Value.name == "search_google")
                        {
                            var searchTerms = funcCall.Value.args["search_terms"].ToString();
                            _pendingSearchRequest = searchTerms ?? string.Empty;
                            _logger.Log($"Initiating search for terms: {searchTerms}");
                            await PerformSearchAndDisplaySummaries();
                            return llmResponse.message;
                        }
                        else if (funcCall.Value.name == "search_memory")
                        {
                            var query = funcCall.Value.args["query"]?.ToString();
                            _logger.Log($"Initiating memory search for query: {query}");
                            var memories = string.IsNullOrEmpty(query) ? new List<(string content, float score)>() : _memoryManager.SearchMemories(query);
                            if (memories.Any())
                            {
                                var memoryContent = string.Join("\n\n", memories.Select(m => $"Memory (score: {m.score:F2}):\n{m.content}"));
                                var prompt = $"Found the following relevant memories:\n\n{memoryContent}\n\nBased on these memories, please provide a helpful response to '{query}'.";
                                return await SendToLLM(prompt) ?? "No relevant information found in memories.";
                            }
                            else
                            {
                                _logger.Log($"No relevant memories found for query '{query}'");
                                return $"No relevant memories found for '{query}'.";
                            }
                        }
                    }
                }
            }
            return llmResponse.message;
        }

        private (string name, Dictionary<string, object> args)? ExtractFunctionCall(JsonElement functionCallJson)
        {
            try
            {
                _logger.Log($"Extracting function call from JSON: {functionCallJson.ToString()}");
                if (!functionCallJson.TryGetProperty("functionCall", out var funcCallElement))
                {
                    _logger.Log("No 'functionCall' property found in JSON");
                    return null;
                }

                var name = funcCallElement.GetProperty("name").GetString();
                var args = JsonSerializer.Deserialize<Dictionary<string, object>>(funcCallElement.GetProperty("args").ToString());

                if (name != null && args != null)
                {
                    _logger.Log($"Successfully extracted function call: name={name}, args={args.Count} entries");
                    return (name, args);
                }

                _logger.Log("Function call extraction failed: name or args is null");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error extracting function call: {ex.Message}");
                return null;
            }
        }

        private async Task PerformSearchAndDisplaySummaries()
        {
            if (string.IsNullOrEmpty(_pendingSearchRequest))
            {
                UpdateChat("No pending search request.\n", "system");
                _logger.Log("No pending search request to process");
                return;
            }

            string searchTerms = _pendingSearchRequest;
            _pendingSearchRequest = string.Empty;
            _originalUserQuery ??= "the user's question";

            try
            {
                var contentUrlPairs = await _search.PerformSearch(searchTerms, _originalUserQuery, UpdateChat, UpdateStatus);
                if (contentUrlPairs.Any())
                {
                    var prompt = GetProcessedContentPrompt(searchTerms, _originalUserQuery, contentUrlPairs);
                    await ProcessLLMRequest(prompt);
                }
                else
                {
                    await ProcessLLMRequest(GetNoRelevantResultsPrompt());
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in PerformSearchAndDisplaySummaries: {ex.Message}");
                UpdateChat($"Error during search: {ex.Message}\n", "system");
            }
            finally
            {
                UpdateStatus(Status.Idle);
            }
        }

        private string GetInitialPrompt() =>
            $"Current Date: {DateTime.Now:yyyy-MM-dd}\n" +
            "You have access to tools to search local memories and perform Google searches.\n" +
            "First, use the 'search_memory' tool to check for relevant local memories before using 'search_google', as memory searches are faster.\n" +
            "If no relevant memories are found, fall back to 'search_google'.\n" +
            "NEVER ask for user confirmation to use tools. Use them when needed.\n" +
            "Use tools to complete your answer unless you are confident you can answer the question.";

        private string GetProcessedContentPrompt(string searchTerms, string originalUserQuery, List<(string content, string url)> contentUrlPairs)
        {
            var contentWithUrls = string.Join("\n\n", contentUrlPairs.Select(p => $"Content from: {p.url}\n\n{p.content}"));
            return $"*processed search request result*\n" +
                   $"I searched for '{searchTerms}' to help answer '{originalUserQuery}'.\n\n" +
                   "Here is the resulting content: \n\n" +
                   $"<content>\n{contentWithUrls}\n</content>\n\n" +
                   $"Based on this content, please provide a helpful response to '{originalUserQuery}'.\n\n";
        }

        private string GetNoRelevantResultsPrompt() =>
            $"The previous search produced no relevant results for '{_pendingSearchRequest}'. " +
            "Please use the 'search_google' tool immediately to try again with different, more specific, or refined search terms. " +
            $"Base your new search terms on the original user query: '{_originalUserQuery}'. " +
            "Do not ask the user for confirmation or to repeat their question — proceed directly with the new search.";

        private string GetImageDescriptionPrompt(string windowTitle) =>
            string.IsNullOrEmpty(windowTitle)
                ? "This is a screenshot."
                : $"This is a screenshot. The screenshot was taken from an application with the window title '{windowTitle}'. " +
                  "Describe accurately the screenshot components in an organized way, including all the text you can read, " +
                  "for future reference, as we might need to use that information later.";

        private string GetImageDescriptionDefaultUserPrompt() =>
            "If the screenshot is from a video game, it likely is a puzzle, a missing person or a list of collectibles. " +
            "Please help me find the most likely solution to the question/puzzle/enigma, or find the missing collectibles. " +
            "If the screenshot is from another type of application and shows an error message, or a coding language issue, " +
            "please find how to solve it. If it's just a cute or strange picture, please respond to it in a fun and engaging way.";
    }
}