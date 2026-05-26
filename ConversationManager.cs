using System.Linq;

namespace YAOLlm;

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
            _conversationHistory.Add(new ChatMessage(ChatRole.System, systemPrompt));
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
            _conversationHistory.Add(new ChatMessage(ChatRole.Model, modelResponse));
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
        var windowContext = string.IsNullOrEmpty(CurrentWindowTitle) ? "" :
            $"- Active application: \"{CurrentWindowTitle}\" (provided by the system — do not search for it)\n";

        return $@"You are a gaming assistant.
- Today's date: {DateTime.Now:yyyy-MM-dd}
{windowContext}
- Avoid spoilers. Give hints first. Only provide exact solutions when the user explicitly asks.
- You may search the web once per response. Use the results to answer — do not search again with a refined query.";
    }

    public int GetTotalCharacterCount()
    {
        lock (_historyLock)
        {
            return _conversationHistory.Sum(turn => turn.Content?.Length ?? 0);
        }
    }
}
