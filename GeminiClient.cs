using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Drawing.Imaging;
using dotenv.net;

namespace GeminiDotnet
{
    public class GeminiClient
    {
        private readonly string _apiKey;
        private readonly List<Dictionary<string, string>> _conversationHistory;
        private readonly object _historyLock = new object();
        private const int MaxHistoryEntries = 32;
        private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
        private string _model = "gemini-2.5-flash";
        private string _currentWindowTitle = "";

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
            var geminiFile = Path.Combine(homeDir, ".gemini.conf");
            try
            {
                DotEnv.Load(new DotEnvOptions(envFilePaths: new[] { geminiFile }));
            }
            catch (Exception ex)
            {
                Logger.Log($"Error loading .gemini.conf file: {ex.Message}");
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

        private string GetInitialPrompt()
        {
            return $@"Current Date: {DateTime.Now:yyyy-MM-dd}
Current Application: {_currentWindowTitle}

You are an AI assistant with the following guidelines:

## Core Principles
- Provide accurate, helpful, and contextually relevant responses.
- Use available tools (such as Google Search) when appropriate to enhance response quality.
- Maintain user engagement and immersion, especially in creative or gaming contexts.

## Response Guidelines
- For games and puzzles: Avoid direct spoilers. Instead, provide subtle hints and background information to guide users toward solutions while preserving enjoyment.
- Only provide exact solutions, codes, or walkthroughs when explicitly requested after initial guidance attempts.
- Support multimodal interactions: Process images, generate images when requested, and handle mixed text/image inputs.

## Capabilities
- Access to real-time information via Google Search when needed.
- Context-aware responses based on current date and active application.";
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

        public void UpdateCurrentWindow(string windowTitle)
        {
            _currentWindowTitle = windowTitle;
            lock (_historyLock)
            {
                _conversationHistory[0]["content"] = GetInitialPrompt();
            }
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

        public async Task ProcessLLMRequest(string prompt, string? imageBase64 = null, string? activeWindowTitle = null, bool useTools = true)
        {
            try
            {
                Logger.Log($"Processing LLM request: {prompt}");
                UpdateStatus(Status.Sending);

                var messages = new List<Dictionary<string, string>>(GetHistorySnapshot());
                var userMessage = new Dictionary<string, string> { { "role", "user" } };
                if (!string.IsNullOrEmpty(prompt))
                {
                    if (!string.IsNullOrEmpty(imageBase64) && !string.IsNullOrEmpty(activeWindowTitle))
                    {
                        UpdateStatus(Status.Analyzing);
                        var describeMessage = new Dictionary<string, string>
                        {
                            { "role", "user" },
                            { "content", $"Describe this screenshot from '{activeWindowTitle}' with excruciating detail, including all components. In a first section, describe the graphic details, persons and any other element you can see. In a second section, please return any text that can be read in the image, in full and without modification, in separated numbered paragraphs, one for each block of text you can read." },
                            { "image", imageBase64 }
                        };
                        string? description = await ApiFunctions.SendToLLM(this, new List<Dictionary<string, string>> { describeMessage }, imageBase64, false);
                        userMessage["content"] = $"{prompt}\nScreenshot Description: {description ?? "Image description unavailable"}";
                    }
                    else
                    {
                        userMessage["content"] = prompt;
                    }
                }
                if (!string.IsNullOrEmpty(imageBase64)) userMessage["image"] = imageBase64;

                messages.Add(userMessage);
                TrimHistoryIfNeeded(messages);
                string response = await ApiFunctions.SendToLLM(this, messages, imageBase64, useTools);

                lock (_historyLock)
                {
                    if (userMessage.ContainsKey("image")) userMessage.Remove("image");
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

        public string ResizeImageBase64(string base64)
        {
            try
            {
                byte[] imageBytes = Convert.FromBase64String(base64);
                using var ms = new MemoryStream(imageBytes);
                using var image = new Bitmap(ms);
                using var resizedImage = image.Resize(640, (int)(image.Height * 640.0 / image.Width));
                using var outputMs = new MemoryStream();
                resizedImage.Save(outputMs, ImageFormat.Png);
                return Convert.ToBase64String(outputMs.ToArray());
            }
            catch (Exception ex)
            {
                Logger.Log($"Error resizing image: {ex.Message}");
                return base64; // Fallback to original if resizing fails
            }
        }

    }
}