using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using dotenv.net;

namespace Gemini
{
    public class GeminiClient
    {
        private readonly string _apiKey;
        private readonly List<Dictionary<string, string>> _conversationHistory;
        private readonly List<object> _tools;
        private readonly object _historyLock = new object();
        private string _originalUserQuery = string.Empty;
        private const int MaxHistoryEntries = 32;
        private const int MaxCharsPerChunk = 32000; // For generateContent
        private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
        private string _model = "gemini-2.0-flash";
        private string _embedModel = "text-embedding-004";

        public Action<string, string> UpdateChat { get; private set; } = (_, __) => { };
        public Action UpdateHistoryCounter { get; private set; } = () => { };
        public Action<Status> UpdateStatus { get; private set; } = (_) => { };
        public Logger Logger { get; private set; }
        public SearchService SearchService { get; private set; }
        public MemoryManager MemoryManager { get; private set; }

        public string OriginalUserQuery
        {
            get => _originalUserQuery;
            set => _originalUserQuery = value ?? string.Empty;
        }

        public GeminiClient(Logger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            LoadEnvironmentVariables();
            _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? throw new ArgumentException("GEMINI_API_KEY not set");
            var googleSearchApiKey = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_API_KEY") ?? throw new ArgumentException("GOOGLE_SEARCH_API_KEY not set");
            var googleSearchEngineId = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_ENGINE_ID") ?? throw new ArgumentException("GOOGLE_SEARCH_ENGINE_ID not set");

            MemoryManager = new MemoryManager(logger, this);
            SearchService = new SearchService(this, googleSearchApiKey, googleSearchEngineId, MemoryManager.StoreSearchResults);
            _tools = ToolsAndPrompts.DefineTools();
            _conversationHistory = InitializeConversationHistory();

            Logger.Log("GeminiClient initialized.");
            UpdateHistoryCounter();
        }

        private void LoadEnvironmentVariables()
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var geminiFile = Path.Combine(homeDir, ".gemini");
            try
            {
                DotEnv.Load(new DotEnvOptions(envFilePaths: new[] { geminiFile }));
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading .gemini file: {ex.Message}");
                throw new ArgumentException("Failed to load environment variables", ex);
            }
        }

        private List<Dictionary<string, string>> InitializeConversationHistory()
        {
            return new List<Dictionary<string, string>>
            {
                new() { { "role", "model" }, { "content", ToolsAndPrompts.GetInitialPrompt() } }
            };
        }

        public void SetUICallbacks(Action<string, string> updateChat, Action updateHistoryCounter, Action<Status> updateStatus)
        {
            UpdateChat = updateChat ?? throw new ArgumentNullException(nameof(updateChat));
            UpdateHistoryCounter = updateHistoryCounter ?? throw new ArgumentNullException(nameof(updateHistoryCounter));
            UpdateStatus = updateStatus ?? throw new ArgumentNullException(nameof(updateStatus));
            UpdateHistoryCounter();
        }

        public int GetConversationHistoryLength()
        {
            lock (_historyLock)
            {
                return _conversationHistory.Sum(turn => turn.GetValueOrDefault("content", "").Length);
            }
        }

        public void ClearConversationHistory()
        {
            lock (_historyLock)
            {
                _conversationHistory.Clear();
                _conversationHistory.AddRange(InitializeConversationHistory());
                UpdateHistoryCounter();
            }
        }

        public async Task ProcessLLMRequest(string prompt, string? imageBase64 = null, string? activeWindowTitle = null)
        {
            try
            {
                Logger.Log($"Processing LLM request: {prompt}");
                UpdateStatus(Status.SendingData);

                if (!string.IsNullOrEmpty(imageBase64) && !string.IsNullOrEmpty(activeWindowTitle))
                {
                    await ProcessImageRequest(prompt, imageBase64, activeWindowTitle);
                }
                else
                {
                    string response = await SendChunkedRequest(prompt);
                    if (!string.IsNullOrEmpty(response)) UpdateChat(response + "\n", "model");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in ProcessLLMRequest: {ex.Message}");
                UpdateChat($"Error: {ex.Message}\n", "system");
            }
            finally
            {
                UpdateStatus(Status.Idle);
            }
        }

        private async Task ProcessImageRequest(string prompt, string imageBase64, string activeWindowTitle)
        {
            UpdateStatus(Status.AnalyzingImage);
            string describePrompt = ToolsAndPrompts.GetImageDescriptionPrompt(activeWindowTitle);
            string? description = await ApiFunctions.SendToLLM(this, describePrompt, imageBase64);

            prompt = string.IsNullOrEmpty(prompt)
                ? ToolsAndPrompts.GetImageDescriptionDefaultUserPrompt()
                : prompt;
            string response = await SendChunkedRequest(prompt, imageBase64);
            if (!string.IsNullOrEmpty(response)) UpdateChat(response + "\n", "model");
        }

        private async Task<string> SendChunkedRequest(string prompt, string? imageBase64 = null)
        {
            var messages = new List<Dictionary<string, string>>(GetHistorySnapshot());
            var userMessage = new Dictionary<string, string> { { "role", "user" } };
            if (!string.IsNullOrEmpty(prompt)) userMessage["content"] = prompt;
            if (!string.IsNullOrEmpty(imageBase64)) userMessage["image"] = imageBase64;
            messages.Add(userMessage);

            TrimHistoryIfNeeded(messages);
            var chunks = ChunkMessages(messages);
            string finalResponse = string.Empty;

            foreach (var chunk in chunks)
            {
                string chunkResponse = await SendSingleChunk(chunk);
                if (!string.IsNullOrEmpty(chunkResponse))
                {
                    finalResponse += chunkResponse + "\n";
                    UpdateHistory(messages, userMessage, chunkResponse);
                }
            }

            return finalResponse.Trim();
        }

        private List<Dictionary<string, string>> GetHistorySnapshot()
        {
            lock (_historyLock)
            {
                return new List<Dictionary<string, string>>(_conversationHistory);
            }
        }

        private void TrimHistoryIfNeeded(List<Dictionary<string, string>> messages)
        {
            if (messages.Count > MaxHistoryEntries)
            {
                Logger.Log($"Trimming history from {messages.Count} to {MaxHistoryEntries}");
                messages.RemoveRange(0, messages.Count - MaxHistoryEntries);
            }
        }

        private List<List<Dictionary<string, string>>> ChunkMessages(List<Dictionary<string, string>> messages)
        {
            var chunks = new List<List<Dictionary<string, string>>>();
            var currentChunk = new List<Dictionary<string, string>>();
            int currentLength = 0;

            foreach (var msg in messages)
            {
                string content = msg.GetValueOrDefault("content", "") + (msg.ContainsKey("image") ? "[image]" : ""); // Rough image placeholder
                var msgChunks = Utils.ChunkText(content, MaxCharsPerChunk);
                foreach (var chunk in msgChunks)
                {
                    var chunkMsg = new Dictionary<string, string>(msg) { ["content"] = chunk };
                    if (currentLength + chunk.Length > MaxCharsPerChunk && currentChunk.Any())
                    {
                        chunks.Add(currentChunk);
                        currentChunk = new List<Dictionary<string, string>>();
                        currentLength = 0;
                    }
                    currentChunk.Add(chunkMsg);
                    currentLength += chunk.Length;
                }
            }

            if (currentChunk.Any()) chunks.Add(currentChunk);
            Logger.Log($"Chunked messages into {chunks.Count} parts.");
            return chunks;
        }

        private async Task<string> SendSingleChunk(List<Dictionary<string, string>> chunk)
        {
            var partsList = new List<object>();

            var payload = new
            {
                contents = chunk.Select(m =>
                {
                    partsList.Clear();
                    if (m.ContainsKey("content"))
                        partsList.Add(new { text = m["content"] });
                    if (m.ContainsKey("image"))
                        partsList.Add(new { inlineData = new { mimeType = "image/png", data = m["image"] } });

                    return new { role = m["role"], parts = partsList.ToList() };
                }),
                tools = _tools
            };

            return await ApiFunctions.SendToLLM(this, payload) ?? string.Empty;
        }

        private void UpdateHistory(List<Dictionary<string, string>> messages, Dictionary<string, string> userMessage, string response)
        {
            lock (_historyLock)
            {
                _conversationHistory.Clear();
                _conversationHistory.AddRange(messages);
                _conversationHistory.Add(userMessage);
                _conversationHistory.Add(new Dictionary<string, string> { { "role", "model" }, { "content", response } });
                UpdateHistoryCounter();
            }
        }

        public async Task<float[]> Embed(string text)
        {
            return await ApiFunctions.Embed(this, text);
        }

        public async Task<string> RetryWithPrompt(string prompt)
        {
            Logger.Log($"Retrying LLM request with updated prompt: {prompt}");
            return await SendChunkedRequest(prompt);
        }

        public string GetGenerateUrl() => $"{ApiBaseUrl}{_model}:generateContent?key={_apiKey}";
        public string GetEmbedUrl() => $"{ApiBaseUrl}{_embedModel}:embedContent?key={_apiKey}";
    }
}