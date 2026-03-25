using System.Linq;

namespace YAOLlm
{
    public class ConversationManager
    {
        private readonly List<ChatMessage> _conversationHistory = new();
        private readonly object _historyLock = new();
        private const int MaxHistoryEntries = 32;
        private string _currentWindowTitle = "";
        private readonly Logger _logger;

        public ConversationManager(Logger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Initialize(string systemPrompt)
        {
            lock (_historyLock)
            {
                _conversationHistory.Clear();
                _conversationHistory.Add(new ChatMessage("system", systemPrompt));
            }
        }

        public List<ChatMessage> GetSnapshot()
        {
            lock (_historyLock)
            {
                return new List<ChatMessage>(_conversationHistory);
            }
        }

        public void AddExchange(ChatMessage userMessage, string modelResponse)
        {
            lock (_historyLock)
            {
                _conversationHistory.Add(userMessage);
                _conversationHistory.Add(new ChatMessage("model", modelResponse));
                TrimHistoryIfNeeded();
            }
        }

        public string CurrentWindowTitle
        {
            get => _currentWindowTitle;
            set
            {
                _currentWindowTitle = value;
                if (!string.IsNullOrEmpty(value))
                {
                    lock (_historyLock)
                    {
                        if (_conversationHistory.Count > 0)
                        {
                            _conversationHistory[0].Content = BuildSystemPrompt();
                        }
                    }
                }
            }
        }

        private void TrimHistoryIfNeeded()
        {
            bool shouldTrim;
            int count;
            lock (_historyLock)
            {
                count = _conversationHistory.Count;
                shouldTrim = count > MaxHistoryEntries;
                if (shouldTrim)
                    _conversationHistory.RemoveRange(1, count - MaxHistoryEntries);
            }
            if (shouldTrim)
                _logger.Log($"Trimming history from {count} to {MaxHistoryEntries}");
        }

        public string BuildSystemPrompt()
        {
            return $@"
You are an AI assistant with the following guidelines:

## Core Principles
- Provide accurate, helpful, and contextually relevant responses. The current application the user is running has this title : {CurrentWindowTitle}
- Use available tools (such as Web Search) when appropriate to enhance response quality.
- Confirm online any information that might have changed since your training cutoff date. Today is {DateTime.Now:yyyy-MM-dd}.
- Maintain user engagement and immersion, especially in creative or gaming contexts.

## Response Guidelines
- For games and puzzles: Avoid direct spoilers. Instead, provide subtle hints and background information to guide users toward solutions while preserving enjoyment.
- Only provide exact solutions, codes, or walkthroughs when explicitly requested after initial guidance attempts.
- Support multimodal interactions: Process images and handle mixed text/image inputs.

## Capabilities
- Access to real-time information via Web Search as often as needed, do not hesitate getting more data sources.
- Context-aware responses based on current date and active application.";
        }

        public int GetTotalCharacterCount()
        {
            lock (_historyLock)
            {
                return _conversationHistory.Sum(turn => turn.Content?.Length ?? 0);
            }
        }
    }
}
