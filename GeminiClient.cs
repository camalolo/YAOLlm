using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using dotenv.net;

namespace Gemini
{
    public class GeminiClient
    {
        private readonly string _apiKey;
        private readonly List<Dictionary<string, string>> _conversationHistory;
        private readonly object _historyLock = new object();
        private const int MaxHistoryEntries = 32;
        private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
        private string _model = "gemini-2.0-flash";

        public Action<string, string> UpdateChat { get; private set; } = (_, __) => { };
        public Action UpdateHistoryCounter { get; private set; } = () => { };
        public Action<Status> UpdateStatus { get; private set; } = (_) => { }; // Ensure this is called
        public Logger Logger { get; private set; }

        public GeminiClient(Logger logger)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            LoadEnvironmentVariables();
            _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? throw new ArgumentException("GEMINI_API_KEY not set");
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
                new() { { "role", "model" }, { "content", GetInitialPrompt() } }
            };
        }

        private static string GetInitialPrompt()
        {
            return $"Current Date: {DateTime.Now:yyyy-MM-dd}\n" +
                   "You are an AI assistant designed to provide accurate and helpful responses using the built-in Google Search tool when needed.";
        }

        public void SetUICallbacks(Action<string, string> updateChat, Action updateHistoryCounter, Action<Status> updateStatus)
        {
            UpdateChat = updateChat ?? throw new ArgumentNullException(nameof(updateChat));
            UpdateHistoryCounter = updateHistoryCounter ?? throw new ArgumentNullException(nameof(updateHistoryCounter));
            UpdateStatus = updateStatus ?? throw new ArgumentNullException(nameof(updateStatus));
            Logger.Log("UI callbacks set in GeminiClient."); // Debug log
            UpdateHistoryCounter();
            UpdateStatus(Status.Idle); // Test initial status
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
                UpdateStatus(Status.Sending); // This should trigger the label update

                var messages = new List<Dictionary<string, string>>(GetHistorySnapshot());
                var userMessage = new Dictionary<string, string> { { "role", "user" } };
                if (!string.IsNullOrEmpty(prompt))
                {
                    if (!string.IsNullOrEmpty(imageBase64) && !string.IsNullOrEmpty(activeWindowTitle))
                    {
                        UpdateStatus(Status.Analyzing);
                        string describePrompt = $"Describe this screenshot from '{activeWindowTitle}' accurately, including all visible text and components.";
                        string? description = await ApiFunctions.SendToLLM(this, describePrompt, imageBase64);
                        userMessage["content"] = $"{prompt}\nScreenshot Description: {description}";
                    }
                    else
                    {
                        userMessage["content"] = prompt;
                    }
                }
                if (!string.IsNullOrEmpty(imageBase64)) userMessage["image"] = imageBase64;

                messages.Add(userMessage);
                TrimHistoryIfNeeded(messages);
                string response = await ApiFunctions.SendToLLM(this, messages);

                lock (_historyLock)
                {
                    _conversationHistory.Add(userMessage);
                    _conversationHistory.Add(new Dictionary<string, string> { { "role", "model" }, { "content", response } });
                    UpdateHistoryCounter();
                }

                if (!string.IsNullOrEmpty(response)) UpdateChat(response + "\n", "model");
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

        public string GetGenerateUrl() => $"{ApiBaseUrl}{_model}:generateContent?key={_apiKey}";
    }
}