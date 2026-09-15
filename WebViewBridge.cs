using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Web.WebView2.Core;

namespace YAOLlm;

/// <summary>
/// Bidirectional message bridge between C# (WinForms) and JavaScript running inside a WebView2 control.
/// Handles serialization, deserialization, and dispatch of all overlay UI messages.
/// </summary>
public sealed class WebViewBridge
{
    private readonly CoreWebView2 _webView;
    private readonly Logger _logger;
    private readonly SynchronizationContext? _syncContext;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Fired when JavaScript sends a chat message: <c>{"type":"send","text":"..."}</c>.
    /// </summary>
    public event Action<string>? SendMessage;

    /// <summary>
    /// Fired when JavaScript requests a capture-send action: <c>{"type":"capture_send","text":"..."}</c>.
    /// </summary>
    public event Action<string>? CaptureSend;

    /// <summary>
    /// Fired when JavaScript requests an image load: <c>{"type":"load_image"}</c>.
    /// </summary>
    public event Action? LoadImage;

    /// <summary>
    /// Fired when JavaScript signals proceed: <c>{"type":"proceed"}</c>.
    /// </summary>
    public event Action? Proceed;

    /// <summary>
    /// Fired when JavaScript requests clearing: <c>{"type":"clear"}</c>.
    /// </summary>
    public event Action? Clear;

    /// <summary>
    /// Fired when JavaScript requests hiding the overlay: <c>{"type":"hide"}</c>.
    /// </summary>
    public event Action? Hide;

    /// <summary>
    /// Fired when JavaScript requests exiting the application: <c>{"type":"exit"}</c>.
    /// </summary>
    public event Action? Exit;

    /// <summary>
    /// Fired when JavaScript requests cycling to the next provider preset: <c>{"type":"cycle_provider"}</c>.
    /// </summary>
    public event Action? CycleProvider;

    /// <summary>
    /// Fired when JavaScript requests stopping the current LLM stream: <c>{"type":"stop"}</c>.
    /// </summary>
    public event Action? Stop;

    /// <summary>
    /// Fired when JavaScript requests toggling TTS on/off: <c>{"type":"toggle_tts"}</c>.
    /// </summary>
    public event Action? ToggleTts;

    /// <summary>
    /// Fired when JavaScript requests compacting the conversation: <c>{"type":"compact"}</c>.
    /// </summary>
    public event Action? Compact;

    /// <summary>
    /// Fired when JavaScript requests adding files to the allowlist: <c>{"type":"add_files"}</c>.
    /// </summary>
    public event Action? AddFiles;

    /// <summary>
    /// Fired when JavaScript requests adding a folder to the allowlist: <c>{"type":"add_folder"}</c>.
    /// </summary>
    public event Action? AddFolder;

    /// <summary>
    /// Fired when JavaScript removes a path from the allowlist: <c>{"type":"remove_allowed_path","path":"..."}</c>.
    /// </summary>
    public event Action<string>? RemoveAllowedPath;

    /// <summary>
    /// Fired when JavaScript confirms memory-file deletion (second [x] click within 1s):
    /// <c>{"type":"delete_memory_file"}</c>. C# validates the target before deleting.
    /// </summary>
    public event Action? DeleteMemoryFile;

    /// <summary>
    /// Initializes a new instance of <see cref="WebViewBridge"/> and subscribes to
    /// <see cref="CoreWebView2.WebMessageReceived"/> for inbound message handling.
    /// </summary>
    public WebViewBridge(CoreWebView2 webView, Logger logger)
    {
        _webView = webView;
        _logger = logger;
        _syncContext = SynchronizationContext.Current;  // Capture UI thread context
        _webView.WebMessageReceived += OnWebMessageReceived;
    }

    /// <summary>
    /// Sends a reset command to the JavaScript UI: <c>{"type":"reset"}</c>.
    /// </summary>
    public void Reset()
    {
        Post(new { type = "reset" });
    }

    /// <summary>
    /// Sends a fully-rendered chat message to the JavaScript UI.
    /// </summary>
    /// <param name="role">One of: user, model, system, error.</param>
    /// <param name="html">Pre-rendered HTML content for the message.</param>
    public void ChatMessage(string role, string html)
    {
        Post(new { type = "chat_message", role, html });
    }

    /// <summary>
    /// Sends a queued user message to the JavaScript UI.
    /// Unlike ChatMessage, this does not flush the current streaming content.
    /// </summary>
    public void ChatQueued(string html)
    {
        Post(new { type = "chat_queued", html });
    }

    /// <summary>
    /// Tells the JavaScript UI a previously queued message has been dequeued
    /// and is now being answered: its pending bubble moves into the main
    /// transcript, keeping chat order correct (response completes, then the
    /// queued message it was typed after appears below it).
    /// </summary>
    public void ChatDequeued(string html)
    {
        Post(new { type = "chat_dequeued", html });
    }

    /// <summary>
    /// Sends a streaming HTML chunk to the JavaScript UI.
    /// </summary>
    /// <param name="html">HTML fragment to append to the current streaming response.</param>
    public void ChatStream(string html)
    {
        Post(new { type = "chat_stream", html });
    }

    /// <summary>
    /// Sends a status update to the JavaScript UI.
    /// </summary>
    /// <param name="status">Human-readable status string.</param>
    public void Status(string status)
    {
        Post(new { type = "status", status });
    }

    /// <summary>
    /// Sends the current LLM provider name to the JavaScript UI.
    /// </summary>
    /// <param name="name">Provider display name.</param>
    public void Provider(string name)
    {
        Post(new { type = "provider", name });
    }

    /// <summary>
    /// Sends the current conversation history size to the JavaScript UI.
    /// </summary>
    /// <param name="chars">Number of characters in the conversation history.</param>
    public void History(int chars)
    {
        Post(new { type = "history", chars });
    }

    /// <summary>
    /// Sends a warning message to the JavaScript UI.
    /// </summary>
    /// <param name="message">Warning text to display.</param>
    public void Warning(string message)
    {
        Post(new { type = "warning", message });
    }

    public void Error(string message)
    {
        Post(new { type = "error", message });
    }

    /// <summary>
    /// Sends the current TTS enabled state to the JavaScript UI.
    /// </summary>
    public void TtsState(bool enabled)
    {
        Post(new { type = "tts_state", enabled });
    }

    /// <summary>
    /// Sends the current file-access master state (FILE_READ config) to the
    /// JavaScript UI — controls visibility of the picker buttons and panel.
    /// </summary>
    public void FileAccessState(bool enabled)
    {
        Post(new { type = "file_access", enabled });
    }

    /// <summary>
    /// Sends the current allowlist (full paths) to the JavaScript UI.
    /// The full list is replaced each time — add/remove stay in sync for free.
    /// </summary>
    public void AllowedPaths(IReadOnlyList<string> paths)
    {
        Post(new { type = "allowed_paths", paths });
    }

    /// <summary>
    /// Sends the per-game memory file state to the JavaScript UI. The row is
    /// special: its [x] arms a 1s-confirmed deletion of the file (never
    /// applies to user-added allowlist entries).
    /// </summary>
    public void MemoryFile(bool enabled, string path, bool exists)
    {
        Post(new { type = "memory_file", enabled, path, exists });
    }

    /// <summary>
    /// Sends a no-provider signal to disable the chat UI.
    /// </summary>
    public void SendNoProvider()
    {
        Post(new { type = "no_provider" });
    }

    /// <summary>
    /// Requests the JavaScript UI to focus the input field.
    /// </summary>
    public void FocusInput()
    {
        Post(new { type = "focus_input" });
    }

    /// <summary>
    /// Renders Markdown text to HTML using Markdig, then sends it as a chat message.
    /// </summary>
    /// <param name="role">One of: user, model, system, error.</param>
    /// <param name="markdown">Raw Markdown text.</param>
    public void ChatMessageFromMarkdown(string role, string markdown)
    {
        var html = RenderMarkdown(markdown);
        ChatMessage(role, html);
    }

    /// <summary>
    /// Renders Markdown text to HTML using the shared Markdig pipeline.
    /// </summary>
    private static string RenderMarkdown(string text)
    {
        return MarkdownHelper.ToHtml(text);
    }

    /// <summary>
    /// Serializes an anonymous object to JSON and posts it to the WebView2 JavaScript context.
    /// </summary>
    private void Post(object message)
    {
        try
        {
            var json = JsonSerializer.Serialize(message, JsonOptions);
            if (_syncContext != null && SynchronizationContext.Current != _syncContext)
            {
                _syncContext.Post(_ => _webView.PostWebMessageAsJson(json), null);
            }
            else
            {
                _webView.PostWebMessageAsJson(json);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"WebViewBridge.Post failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles inbound messages from the JavaScript UI and dispatches to the appropriate event.
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            var type = element.GetProperty("type").GetString();

            switch (type)
            {
                case "send":
                    var text = element.GetProperty("text").GetString() ?? string.Empty;
                    SendMessage?.Invoke(text);
                    break;
                case "capture_send":
                    var captureText = "";
                    if (element.TryGetProperty("text", out var textProp))
                        captureText = textProp.GetString() ?? "";
                    CaptureSend?.Invoke(captureText);
                    break;
                case "load_image":
                    LoadImage?.Invoke();
                    break;
                case "proceed":
                    Proceed?.Invoke();
                    break;
                case "clear":
                    Clear?.Invoke();
                    break;
                case "hide":
                    Hide?.Invoke();
                    break;
                case "exit":
                    Exit?.Invoke();
                    break;
                case "cycle_provider":
                    CycleProvider?.Invoke();
                    break;
                case "stop":
                    Stop?.Invoke();
                    break;
                case "toggle_tts":
                    ToggleTts?.Invoke();
                    break;
                case "compact":
                    Compact?.Invoke();
                    break;
                case "add_files":
                    AddFiles?.Invoke();
                    break;
                case "add_folder":
                    AddFolder?.Invoke();
                    break;
                case "remove_allowed_path":
                    var removedPath = element.TryGetProperty("path", out var rmPathProp) ? rmPathProp.GetString() ?? "" : "";
                    if (removedPath.Length > 0)
                        RemoveAllowedPath?.Invoke(removedPath);
                    break;
                case "delete_memory_file":
                    DeleteMemoryFile?.Invoke();
                    break;
                case "_console":
                    // JS console forwarder (injected by MainForm) — log and don't dispatch
                    var level = element.TryGetProperty("level", out var levelProp) ? levelProp.GetString() ?? "log" : "log";
                    var consoleMessage = element.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "" : "";
                    _logger.Log($"[JS:{level}] {consoleMessage}");
                    break;
                default:
                    _logger.Log($"WebViewBridge: unknown message type '{type}'");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"WebViewBridge.OnWebMessageReceived failed: {ex.Message}");
        }
    }
}
