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
        private readonly Logger _logger;
        private readonly string _apiKey;
        private readonly Search _search;
        private readonly MemoryManager _memoryManager;
        public const int MaxHistoryLength = 32; // Changed to public
        private string _llmApiModel = "gemini-2.0-flash";
        private string _llmApiUrl => $"https://generativelanguage.googleapis.com/v1beta/models/{_llmApiModel}:generateContent?key={_apiKey}";
        private List<Dictionary<string, string>> _conversationHistory;
        private readonly List<object> _tools;
        private string _originalUserQuery;

        // Public getters for private fields
        public Logger Logger => _logger;
        public Search Search => _search;
        public MemoryManager MemoryManager => _memoryManager;
        public string LlmApiUrl => _llmApiUrl;
        public List<Dictionary<string, string>> ConversationHistory => _conversationHistory;
        public List<object> Tools => _tools.ToList(); // Return a copy to prevent external modification
        public string OriginalUserQueryInternal { get => _originalUserQuery; set => _originalUserQuery = value; }

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

            _conversationHistory = new List<Dictionary<string, string>> { new() { { "role", "model" }, { "content", ToolsAndPrompts.GetInitialPrompt() } } };
            _tools = ToolsAndPrompts.DefineTools();
            _originalUserQuery = string.Empty;
            UpdateChat = (_, __) => { };
            UpdateHistoryCounter = () => { };
            UpdateStatus = (_) => { };
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
            _conversationHistory = new List<Dictionary<string, string>> { new() { { "role", "model" }, { "content", ToolsAndPrompts.GetInitialPrompt() } } };
            UpdateHistoryCounter();
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
                    string describePrompt = ToolsAndPrompts.GetImageDescriptionPrompt(activeWindowTitle ?? string.Empty);
                    var response = await ApiFunctions.SendToLLM(this, describePrompt, imageBase64);

                    prompt = string.IsNullOrEmpty(prompt) ? ToolsAndPrompts.GetImageDescriptionDefaultUserPrompt() : prompt;
                    response = await ApiFunctions.SendToLLM(this, prompt, imageBase64);
                    if (response != null) UpdateChat(response + "\n", "model");
                }
                else
                {
                    var response = await ApiFunctions.SendToLLM(this, prompt);
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

        private async void CreateMemoryFromSearchResults(List<(string content, string url)> results)
        {
            await MemoryManager.CreateMemoryFromSearchResults(_logger, _memoryManager, results);
        }
    }
}