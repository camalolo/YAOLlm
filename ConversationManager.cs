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

    private bool _ttsEnabled;

    /// <summary>
    /// Whether TTS output is enabled. Kept in sync with the UI toggle so the
    /// system prompt only instructs the model to emit tts_summary calls while
    /// TTS is actually on — otherwise the model wastes tokens on a spoken
    /// summary that is never played. Rebuilds the system prompt in place.
    /// </summary>
    public bool TtsEnabled
    {
        get => _ttsEnabled;
        set
        {
            _ttsEnabled = value;
            lock (_historyLock)
            {
                if (_conversationHistory.Count > 0)
                {
                    _conversationHistory[0].Content = BuildSystemPrompt();
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

        // Only instruct the model about tts_summary when the tool is actually
        // advertised (see MainForm.ProcessLLMRequestAsync) — a dangling
        // instruction makes the model hallucinate tool calls it can't make.
        var ttsInstruction = TtsEnabled
            ? "\n- Always include a tts_summary tool call in your final response, after all search/fetch tool results have been processed. Do not call it in the same turn as other tools. Provide a concise, conversational summary suitable for text-to-speech. Omit tables, code, lists, URLs, and detailed data — just the key takeaway in 1-3 sentences."
            : "";

        return $@"You are a gaming assistant.
- Today's date: {DateTime.Now:yyyy-MM-dd}
{windowContext}
- Avoid spoilers. Give hints first. Only provide exact solutions when the user explicitly asks.
- You may search the web once per response. Use the results to answer — do not search again with a refined query.{ttsInstruction}";
    }

    public int GetTotalCharacterCount()
    {
        lock (_historyLock)
        {
            return _conversationHistory.Sum(turn => turn.Content?.Length ?? 0);
        }
    }
}
