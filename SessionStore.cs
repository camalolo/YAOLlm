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
    }

    /// <summary>
    /// Writes the full history snapshot. Any IO failure is logged and ignored.
    /// </summary>
    public void Save(IReadOnlyList<ChatMessage> history)
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
    /// Loads the persisted history. Returns null when no usable session exists
    /// (missing file, empty conversation, or corrupt content) — the caller
    /// starts fresh in that case.
    /// </summary>
    public List<ChatMessage>? Load()
    {
        try
        {
            lock (_ioLock)
            {
                if (!File.Exists(_path)) return null;
                var file = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(_path), JsonOptions);
                if (file?.Messages is not { Count: > 0 }) return null;
                return file.Messages.Select(e => new ChatMessage(
                    e.Role,
                    e.Content,
                    string.IsNullOrEmpty(e.ImageBase64) ? null : Convert.FromBase64String(e.ImageBase64)
                )).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"Session load failed: {ex.Message}");
            return null;
        }
    }
}
