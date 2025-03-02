using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RestSharp;
using HtmlAgilityPack;
using System.Text;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using dotenv.net;

namespace Gemini
{
    public class GeminiClient
    {
        private readonly Logger _logger;
        private readonly string _apiKey;
        private readonly string _googleSearchApiKey;
        private readonly string _googleSearchEngineId;
        private const int MaxHistoryLength = 32;
        private const double RelevanceThreshold = 0.33;
        private string _llmApiModel = "gemini-2.0-flash";
        private string _llmApiUrl => $"https://generativelanguage.googleapis.com/v1beta/models/{_llmApiModel}:generateContent?key={_apiKey}";
        private List<Dictionary<string, string>> _conversationHistory;
        private readonly List<object> _tools;
        private string _pendingSearchRequest;
        private string _originalUserQuery;
        private readonly Dictionary<string, List<string>> _searchResultsCache = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, int> _searchResultsIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, (string, float[])> _scrapedContentCache = new Dictionary<string, (string, float[])>();

        public Action<string, string> UpdateChat { get; set; }
        public Action UpdateHistoryCounter { get; set; }
        public Action<Status> UpdateStatus { get; set; }
        public string OriginalUserQuery { get; set; }

        public GeminiClient(Logger logger)
        {
            _logger = logger;
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var geminiFile = Path.Combine(homeDir, ".gemini");

            // Load environment variables from the .gemini file
            try
            {
                DotEnv.Load(new DotEnvOptions(envFilePaths: new[] { geminiFile }));
                _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? throw new ArgumentException("GEMINI_API_KEY is not set");
                _googleSearchApiKey = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_API_KEY") ?? throw new ArgumentException("GOOGLE_SEARCH_API_KEY is not set");
                _googleSearchEngineId = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_ENGINE_ID") ?? throw new ArgumentException("GOOGLE_SEARCH_ENGINE_ID is not set");
            }
            catch (Exception ex)
            {
                _logger.Log($"Error loading .gemini file: {ex.Message}");
                throw new ArgumentException("Failed to load environment variables from .gemini", ex);
            }

            _conversationHistory = new List<Dictionary<string, string>> { new Dictionary<string, string> { { "role", "model" }, { "content", GetInitialPrompt() } } };
            _tools = DefineTools();
            _pendingSearchRequest = string.Empty;
            _originalUserQuery = string.Empty;
            UpdateChat = (s1, s2) => { };
            UpdateHistoryCounter = () => { };
            UpdateStatus = (status) => { };
            OriginalUserQuery = string.Empty;
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
            _conversationHistory = new List<Dictionary<string, string>> { new Dictionary<string, string> { { "role", "model" }, { "content", GetInitialPrompt() } } };
        }

        private List<object> DefineTools()
        {
            return new List<object>
            {
                new { function_declarations = new[]
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
                    }
                } }
            };
        }

        public async Task ProcessLLMRequest(string prompt, string? imageBase64 = null, string? activeWindowTitle = null)
        {
            UpdateStatus(Status.SendingData);
            if (!string.IsNullOrEmpty(imageBase64))
            {
                string describePrompt = GetImageDescriptionPrompt(activeWindowTitle ?? String.Empty);
                var response = await SendToLLM(describePrompt, imageBase64);
                if (response != null) UpdateChat("\nImage analysis done.", "model");

                prompt = string.IsNullOrEmpty(prompt) ? GetImageDescriptionDefaultUserPrompt() : prompt;
                UpdateChat("\nProcessing image...", "system");
                response = await SendToLLM(prompt, imageBase64);
                if (response != null) UpdateChat(response, "model");
            }
            else
            {
                var response = await SendToLLM(prompt);
                if (response != null) UpdateChat(response, "model");
            }
            UpdateStatus(Status.Idle);
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

            UpdateChat("\nSystem: Waiting for LLM reply...", "system");
            UpdateStatus(Status.ReceivingData);

            try
            {
                var response = await client.ExecuteAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Request failed with status code {response.StatusCode}");
                }
                var data = response.Content != null ? JsonSerializer.Deserialize<JsonElement>(response.Content) : default;
                var llmResponse = ExtractLLMResponse(data);

                var processedResponse = await HandleLLMResponse(llmResponse);
                if (processedResponse != null)
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
                UpdateChat($"Error processing LLM request: {ex.Message}", "system");
                _logger.Log($"Error: {ex.Message}");
                UpdateStatus(Status.Idle);
                return null;
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
                        {
                            messageParts.Add(text.GetString()!);
                        }
                        else if (part.TryGetProperty("functionCall", out var func))
                        {
                            functionCalls.Add(part);
                        }
                    }
                }
                return (string.Join("", messageParts), functionCalls);
            }
            return (string.Empty, new List<JsonElement>());
        }

        private async Task<string> HandleLLMResponse((string message, List<JsonElement> functionCalls) llmResponse)
        {
            if (llmResponse.functionCalls?.Any() == true)
            {
                foreach (var func in llmResponse.functionCalls)
                {
                    var funcCall = ExtractFunctionCall(func);
                    if (funcCall.HasValue && funcCall.Value.name == "search_google")
                    {
                        var searchTerms = funcCall.Value.args["search_terms"].ToString();
                        _pendingSearchRequest = searchTerms??string.Empty;
                        await PerformGoogleSearchAndDisplaySummaries();
                        return llmResponse.message;
                    }
                }
            }
            return llmResponse.message;
        }

        private (string name, Dictionary<string, object> args)? ExtractFunctionCall(JsonElement functionCallJson)
        {
            try
            {
                var name = functionCallJson.GetProperty("name").GetString();
                var args = JsonSerializer.Deserialize<Dictionary<string, object>>(functionCallJson.GetProperty("args").ToString());
                if (name != null && args != null)
                {
                    return (name, args);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error extracting function call: {ex.Message}");
                return null;
            }
        }

        private async Task PerformGoogleSearchAndDisplaySummaries()
        {
            if (string.IsNullOrEmpty(_pendingSearchRequest)) return;
            string searchTerms = _pendingSearchRequest;
            _pendingSearchRequest = string.Empty;
            _originalUserQuery ??= "the user's question";

            UpdateChat($"System: Searching for '{searchTerms}'...", "system");
            UpdateStatus(Status.Searching);

            List<string> urls;
            if (_searchResultsCache.ContainsKey(searchTerms))
            {
                urls = _searchResultsCache[searchTerms].Skip(_searchResultsIndex.GetValueOrDefault(searchTerms, 0)).Take(3).ToList();
                _searchResultsIndex[searchTerms] = _searchResultsIndex.GetValueOrDefault(searchTerms, 0) + 3;
                if (!urls.Any()) urls = await SearchGoogle(searchTerms);
            }
            else
            {
                urls = await SearchGoogle(searchTerms);
                _searchResultsIndex[searchTerms] = 0;
            }

            if (urls.Any())
            {
                UpdateStatus(Status.Scraping);
                var scrapedData = await Task.WhenAll(urls.Select(async url =>
                {
                    if (_scrapedContentCache.ContainsKey(url))
                        return _scrapedContentCache[url];
                    try
                    {
                        var client = new HttpClient();
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.36");
                        var html = await client.GetStringAsync(url);
                        var doc = new HtmlAgilityPack.HtmlDocument();
                        doc.LoadHtml(html);
                        foreach (var node in doc.DocumentNode.SelectNodes("//*[@href]") ?? new HtmlNodeCollection(null))
                            node.Remove();
                        var text = doc.DocumentNode.InnerText;
                        var embedding = ComputeEmbedding(text); // Placeholder for embedding logic
                        _scrapedContentCache[url] = (text, embedding);
                        return (text, embedding);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"Error scraping {url}: {ex.Message}");
                        return (string.Empty, new float[0]);
                    }
                }));

                UpdateStatus(Status.Processing);
                var docs = scrapedData.Where(d => d.Item1 != null && d.Item2 != null).Select(d => d.Item1).ToList();
                var embeddings = scrapedData.Where(d => d.Item1 != null && d.Item2 != null).Select(d => d.Item2).ToList();

                if (!docs.Any())
                {
                    await ProcessLLMRequest(GetNoRelevantResultsPrompt());
                    return;
                }

                var queryEmbedding = ComputeEmbedding(searchTerms); // Placeholder
                var scores = embeddings.Select(e => CosineSimilarity(e, queryEmbedding)).ToList();
                var relevantDocs = docs.Zip(scores, (d, s) => (d, s)).Where(x => x.s > RelevanceThreshold).OrderByDescending(x => x.s).Take(3).Select(x => x.d).ToList();

                if (!relevantDocs.Any())
                {
                    await ProcessLLMRequest(GetNoRelevantResultsPrompt());
                    return;
                }

                var contentUrlPairs = relevantDocs.Zip(urls, (content, url) => (content, url)).ToList();
                var prompt = GetProcessedContentPrompt(searchTerms, _originalUserQuery, contentUrlPairs);
                await ProcessLLMRequest(prompt);
            }
            else
            {
                await ProcessLLMRequest(GetNoRelevantResultsPrompt());
            }
            UpdateStatus(Status.Idle);
        }

        private async Task<List<string>> SearchGoogle(string searchTerms)
        {
            try
            {
                var client = new RestClient("https://www.googleapis.com/customsearch/v1");
                var request = new RestRequest{ Method = Method.Get };
                request.AddParameter("key", _googleSearchApiKey);
                request.AddParameter("cx", _googleSearchEngineId);
                request.AddParameter("q", searchTerms);
                request.AddParameter("num", 10);
                request.AddParameter("start", _searchResultsIndex.GetValueOrDefault(searchTerms, 1));

                var response = await client.ExecuteAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Request failed with status code {response.StatusCode}");
                }
                var data = response.Content != null ? JsonSerializer.Deserialize<JsonElement>(response.Content) : default;
                if (data.TryGetProperty("items", out var items))
                {
                    _searchResultsIndex[searchTerms] = _searchResultsIndex.GetValueOrDefault(searchTerms, 1) + 10;
                    var urls = items.EnumerateArray().Select(item => item.GetProperty("link").GetString() ?? string.Empty).ToList();
                    _searchResultsCache[searchTerms] = urls;
                    return urls;
                }
                return new List<string>();
            }
            catch (Exception ex)
            {
                _logger.Log($"Google Search error: {ex.Message}");
                return new List<string>();
            }
        }

        private string GetInitialPrompt() => $"Current Date: {DateTime.Now:yyyy-MM-dd}\nYou have access to tools that perform google search when your internal knowledge is insufficient.\nNEVER ask for user confirmation to use tools. Use them when needed.\nUse tools to complete your answer unless you are confident you can answer the question.";

        private string GetProcessedContentPrompt(string searchTerms, string originalUserQuery, List<(string content, string url)> contentUrlPairs)
        {
            var contentWithUrls = string.Join("\n\n", contentUrlPairs.Select(p => $"Content from: {p.url}\n\n{p.content}"));
            return $"*processed search request result*\nI searched for '{searchTerms}' to help answer '{originalUserQuery}'.\n\nHere is the resulting content: \n\n<content>\n{contentWithUrls}\n</content>\n\nBased on this content, please provide a helpful response to '{originalUserQuery}'.\n\n";
        }

        private string GetNoRelevantResultsPrompt() => "The search produced no relevant results. You may want to use different search terms.";

        private string GetImageDescriptionPrompt(string windowTitle) => string.IsNullOrEmpty(windowTitle) ? "This is a screenshot." : $"This is a screenshot. The screenshot was taken from an application with the window title '{windowTitle}'. Describe accurately the screenshot components in an organized way, including all the text you can read, for future reference, as we might need to use that information later.";

        private string GetImageDescriptionDefaultUserPrompt() => "If the screenshot is from a video game, it likely is a puzzle, a missing person or a list of collectibles. Please help me find the most likely solution to the question/puzzle/enigma, or find the missing collectibles. If the screenshot is from another type of application and shows an error message, or a coding language issue, please find how to solve it. If it's just a cute or strange picture, please respond to it in a fun and engaging way.";

        // Placeholder for embedding logic (simplified)
        private float[] ComputeEmbedding(string text) => text.Split().Select(w => (float)w.GetHashCode()).ToArray();
        private float CosineSimilarity(float[] a, float[] b)
        {
            float dot = a.Zip(b, (x, y) => x * y).Sum();
            float magA = (float)Math.Sqrt(a.Sum(x => x * x));
            float magB = (float)Math.Sqrt(b.Sum(x => x * x));
            return dot / (magA * magB);
        }
    }
}