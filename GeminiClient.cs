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
        public const int MaxHistoryLength = 32;
        private string _apiBaseUrl => "https://generativelanguage.googleapis.com/v1beta/models/";
        private string _embedModel = "text-embedding-004";
        private string _model = "gemini-2.0-flash";
        private List<Dictionary<string, string>> _conversationHistory; // Already private
        private readonly List<object> _tools;
        private string _originalUserQuery;
        private readonly object _historyLock = new object();

        public Logger Logger => _logger;
        public Search Search => _search;
        public MemoryManager MemoryManager => _memoryManager;
        public List<Dictionary<string, string>> ConversationHistory
        {
            get
            {
                lock (_historyLock)
                {
                    return new List<Dictionary<string, string>>(_conversationHistory);
                }
            }
            set // Add setter to ensure controlled updates
            {
                lock (_historyLock)
                {
                    _conversationHistory = new List<Dictionary<string, string>>(value);
                }
            }
        }
        public List<object> Tools => _tools.ToList();
        public string OriginalUserQuery { get => _originalUserQuery; set => _originalUserQuery = value; }

        public Action<string, string> UpdateChat { get; set; }
        public Action UpdateHistoryCounter { get; set; }
        public Action<Status> UpdateStatus { get; set; }

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
                _search = new Search(this, googleSearchApiKey, googleSearchEngineId, CreateMemoryFromSearchResults);
            }
            catch (Exception ex)
            {
                _logger.Log($"Error loading .gemini file: {ex.Message}");
                throw new ArgumentException("Failed to load environment variables from .gemini", ex);
            }

            _conversationHistory = new List<Dictionary<string, string>> {
                new() { { "role", "model" }, { "content", ToolsAndPrompts.GetInitialPrompt() } }
            };
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

        public int GetConversationHistoryLength()
        {
            lock (_historyLock)
            {
                return _conversationHistory.Sum(turn => turn["content"].Length);
            }
        }

        public void ClearConversationHistory()
        {
            lock (_historyLock)
            {
                _conversationHistory = new List<Dictionary<string, string>> {
                    new() { { "role", "model" }, { "content", ToolsAndPrompts.GetInitialPrompt() } }
                };
                UpdateHistoryCounter();
            }
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

        private async Task CreateMemoryFromSearchResults(List<(string content, string url)> results)
        {
            await MemoryManager.CreateMemoryFromSearchResults(_logger, _memoryManager, results);
        }

        public async Task<float[]> Embed(string text)
        {
            return await ApiFunctions.Embed(this, text);
        }

        public string getUrl()
        {
            return $"{_apiBaseUrl}{_model}:generateContent?key={_apiKey}";
        }

        public string getEmbedUrl()
        {
            return $"{_apiBaseUrl}{_embedModel}:embedContent?key={_apiKey}";
        }
    }
}