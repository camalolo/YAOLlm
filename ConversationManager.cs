using System.Linq;

namespace YAOLlm;

public class ConversationManager
{
    private readonly List<ChatMessage> _conversationHistory = new();
    private readonly object _historyLock = new();
    private const int MaxHistoryEntries = 32;
    private string _currentWindowTitle = "";
    private readonly Logger _logger;

    /// <summary>
    /// Optional sink invoked with a full-history snapshot after every structural
    /// mutation (initialize, exchange, compact, restore) — used to persist the
    /// session across restarts. Invoked outside the history lock; assign it only
    /// after startup restore has run, or it will overwrite the session file
    /// with the pre-restore state.
    /// </summary>
    public Action<IReadOnlyList<ChatMessage>>? OnHistoryChanged;

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
        NotifyHistoryChanged();
    }

    /// <summary>
    /// Appends previously persisted conversation turns after Initialize
    /// (session resume). Enforces the normal history cap and fires
    /// OnHistoryChanged once with the merged history.
    /// </summary>
    public void RestoreTurns(IEnumerable<ChatMessage> turns)
    {
        if (turns == null) throw new ArgumentNullException(nameof(turns));
        lock (_historyLock)
        {
            _conversationHistory.AddRange(turns);
            TrimHistoryIfNeeded();
        }
        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        var handler = OnHistoryChanged;
        if (handler == null) return;
        List<ChatMessage> snapshot;
        lock (_historyLock)
        {
            snapshot = new List<ChatMessage>(_conversationHistory);
        }
        handler(snapshot);
    }

    public List<ChatMessage> GetSnapshot()
    {
        lock (_historyLock)
        {
            return new List<ChatMessage>(_conversationHistory);
        }
    }

    /// <summary>
    /// Snapshot of the conversation turns only — everything after the system
    /// prompt. This is the raw material for context compaction.
    /// </summary>
    public List<ChatMessage> GetConversationTurns()
    {
        lock (_historyLock)
        {
            if (_conversationHistory.Count <= 1)
                return new List<ChatMessage>();
            return _conversationHistory.GetRange(1, _conversationHistory.Count - 1);
        }
    }

    /// <summary>
    /// Replaces the entire conversation with the current system prompt followed
    /// by a single user message containing the compaction summary (opencode-style
    /// compaction). The summary must carry every detail needed to continue the
    /// conversation seamlessly. Subsequent exchanges append after it normally.
    /// </summary>
    public void Compact(string summary)
    {
        if (summary == null) throw new ArgumentNullException(nameof(summary));
        lock (_historyLock)
        {
            var systemPrompt = _conversationHistory.Count > 0
                ? _conversationHistory[0].Content ?? BuildSystemPrompt()
                : BuildSystemPrompt();
            _conversationHistory.Clear();
            _conversationHistory.Add(new ChatMessage(ChatRole.System, systemPrompt));
            _conversationHistory.Add(new ChatMessage(ChatRole.User, summary));
        }
        NotifyHistoryChanged();
    }

    public void AddExchange(ChatMessage userMessage, string modelResponse)
    {
        lock (_historyLock)
        {
            _conversationHistory.Add(userMessage);
            _conversationHistory.Add(new ChatMessage(ChatRole.Model, modelResponse));
            TrimHistoryIfNeeded();
        }
        NotifyHistoryChanged();
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
            RebuildSystemPrompt();
        }
    }

    private IReadOnlyList<string> _allowedPaths = Array.Empty<string>();

    /// <summary>
    /// User-approved files/directories the file tools may access. Kept in sync
    /// with the UI allowlist so the system prompt lists exactly what the model
    /// can touch (and says nothing about file access while the list is empty).
    /// Stores a snapshot; rebuilds the system prompt in place.
    /// </summary>
    public IReadOnlyList<string> AllowedPaths
    {
        get => _allowedPaths;
        set
        {
            _allowedPaths = value?.ToArray() ?? Array.Empty<string>();
            RebuildSystemPrompt();
        }
    }

    private string? _memoryFilePath;

    /// <summary>
    /// The memory file (file_write/memory_write feature). Always present in
    /// the system prompt while set, with a note that this file is for writing
    /// memories. Null = memory feature off.
    /// </summary>
    public string? MemoryFilePath
    {
        get => _memoryFilePath;
        set
        {
            _memoryFilePath = value;
            RebuildSystemPrompt();
        }
    }

    private string? _writableRoot;

    /// <summary>The only directory file_write may create files in (prompt context).</summary>
    public string? WritableRoot
    {
        get => _writableRoot;
        set
        {
            _writableRoot = value;
            RebuildSystemPrompt();
        }
    }

    private void RebuildSystemPrompt()
    {
        lock (_historyLock)
        {
            if (_conversationHistory.Count > 0)
            {
                _conversationHistory[0].Content = BuildSystemPrompt();
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

        // Only mention file access while the allowlist is non-empty — a
        // dangling instruction makes the model hallucinate tool calls that
        // can never succeed.
        var fileContext = AllowedPaths.Count == 0 ? "" :
            "- Local file access: the user approved exactly these paths. file_read reads files under them, list_files lists an approved directory; everything else is denied:\n"
            + string.Join("\n", AllowedPaths.Select(p => $"  - {p}")) + "\n";

        // Memory file + writable area — present whenever the memory feature is
        // on, independent of the user allowlist (the model must always be able
        // to reach its own notes).
        var memoryContext = "";
        if (!string.IsNullOrEmpty(MemoryFilePath))
        {
            memoryContext = $"- Memory: \"{MemoryFilePath}\" is your notes file — for writing memories (item locations, solutions, mechanics, strategies). Read it before searching for anything you may already know, and append what you learn so you never search twice.\n";
        }
        if (!string.IsNullOrEmpty(WritableRoot))
            memoryContext += $"- Writable area: file_write may only create files under \"{WritableRoot}\" — everything else on disk is read-only.\n";

        // Only instruct the model about tts_summary when the tool is actually
        // advertised (see MainForm.ProcessLLMRequestAsync) — a dangling
        // instruction makes the model hallucinate tool calls it can't make.
        var ttsInstruction = TtsEnabled
            ? "\n- Always include a tts_summary tool call in your final response, after all search/fetch tool results have been processed. Do not call it in the same turn as other tools. Provide a concise, conversational summary suitable for text-to-speech. Omit tables, code, lists, URLs, and detailed data — just the key takeaway in 1-3 sentences."
            : "";

        return $@"You are the user's in-game assistant. Be terse — answer first, no filler.
- Today: {DateTime.Now:yyyy-MM-dd}
{windowContext}{fileContext}{memoryContext}
- Gaming help: hints before spoilers; exact solutions only when explicitly asked.
- Correctness: when unsure of a fact, search the web — never guess specifics. If sources conflict or come up empty, say so plainly.{ttsInstruction}";
    }

    public int GetTotalCharacterCount()
    {
        lock (_historyLock)
        {
            return _conversationHistory.Sum(turn => turn.Content?.Length ?? 0);
        }
    }
}
