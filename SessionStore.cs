using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YAOLlm;

/// <summary>
/// Persists the conversation history to <c>~/.yaollm.session.json</c> (next to
/// <c>~/.yaollm.conf</c>) so a session survives an app restart. Saves are
/// atomic (temp file + move) and never throw — persistence must not be able
/// to take the chat down.
/// </summary>
public class SessionStore
{
    private readonly string _path;
    private readonly Logger _logger;
    private readonly object _ioLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public SessionStore(Logger logger, string? filePath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".yaollm.session.json");
    }

    private sealed class SessionEntry
    {
        public ChatRole Role { get; set; }
        public string? Content { get; set; }
        public string? ImageBase64 { get; set; }
    }

    private sealed class SessionFile
    {
        public string SavedAt { get; set; } = "";
        public List<SessionEntry> Messages { get; set; } = new();
        public List<string> AllowedPaths { get; set; } = new();
    }

    /// <summary>Full persisted session state: conversation turns + file allowlist.</summary>
    public sealed record SessionSnapshot(List<ChatMessage> Messages, List<string> AllowedPaths);

    /// <summary>
    /// Writes the full history snapshot (allowlist preserved as passed).
    /// The single-argument overload writes an empty allowlist — production
    /// callers always pass both. Any IO failure is logged and ignored.
    /// </summary>
    public void Save(IReadOnlyList<ChatMessage>? history) => Save(history, null);

    public void Save(IReadOnlyList<ChatMessage>? history, IReadOnlyList<string>? allowedPaths)
    {
        if (history == null) return;
        try
        {
            lock (_ioLock)
            {
                var file = new SessionFile
                {
                    SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Messages = history.Select(m => new SessionEntry
                    {
                        Role = m.Role,
                        Content = m.Content,
                        ImageBase64 = m.Image is { Length: > 0 } img ? Convert.ToBase64String(img) : null,
                    }).ToList(),
                    AllowedPaths = allowedPaths?.ToList() ?? new List<string>(),
                };

                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Session save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the full persisted state. Returns null when no usable session
    /// exists (missing file, corrupt content, or nothing worth restoring) —
    /// the caller starts fresh in that case. Messages may be empty while an
    /// allowlist exists (and vice versa).
    /// </summary>
    public SessionSnapshot? LoadSession()
    {
        try
        {
            lock (_ioLock)
            {
                if (!File.Exists(_path)) return null;
                var file = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(_path), JsonOptions);
                if (file == null) return null;

                var messages = file.Messages.Select(e => new ChatMessage(
                    e.Role,
                    e.Content,
                    string.IsNullOrEmpty(e.ImageBase64) ? null : Convert.FromBase64String(e.ImageBase64)
                )).ToList();

                var paths = file.AllowedPaths ?? new List<string>();
                if (messages.Count == 0 && paths.Count == 0) return null;
                return new SessionSnapshot(messages, paths);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Session load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Loads the persisted conversation history. Returns null when no usable
    /// session exists (missing file, empty conversation, or corrupt content) —
    /// the caller starts fresh in that case.
    /// </summary>
    public List<ChatMessage>? Load()
    {
        return LoadSession() is { Messages.Count: > 0 } snapshot ? snapshot.Messages : null;
    }
}
