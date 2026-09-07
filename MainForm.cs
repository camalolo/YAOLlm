using System.Reflection;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace YAOLlm;

public partial class MainForm : Form
{
    private readonly PresetManager _presetManager;
    private readonly StatusManager _statusManager;
    private readonly Logger _logger;
    private readonly ConversationManager _conversationManager;
    private readonly TrayIconManager _trayIconManager;
    private readonly WebView2 _webView;
    private WebViewBridge? _bridge;
    private ILLMProvider _currentProvider;
    private Action<ProviderStatus>? _providerStatusHandler;
    private Action? _onSearchComplete;
    private string? _preToolResponse;
    private string? _lastTtsText;
    private bool _pendingPresetSwitch;
    private IntPtr _previousWindowHandle = IntPtr.Zero;
    private readonly Queue<(string? message, string? imageBase64, string? title)> _messageQueue = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly TtsService _ttsService;

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 1;
    private const int MOD_WIN = 0x0008;
    private const int VK_F12 = 0x7B;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public MainForm(PresetManager presetManager, StatusManager statusManager, Logger logger)
    {
        _presetManager = presetManager ?? throw new ArgumentNullException(nameof(presetManager));
        _statusManager = statusManager ?? throw new ArgumentNullException(nameof(statusManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _currentProvider = _presetManager.CreateProvider();
        _conversationManager = new ConversationManager(_logger);
        _conversationManager.Initialize(_conversationManager.BuildSystemPrompt());

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            Visible = false
        };

        FormLayout.ConfigureForm(this);
        this.Controls.Add(_webView);

        _trayIconManager = new TrayIconManager(ToggleVisibility);

        var ttsVoice = Environment.GetEnvironmentVariable("TTS_VOICE");
        _ttsService = new TtsService(ttsVoice, _logger);

        this.FormClosing += MainForm_FormClosing;

        // The overlay starts hidden in the tray, so the Form Load event can
        // stay unfired for the whole session (it waits for first visibility) —
        // never hang startup work off it. The hotkey is plain Win32 on the
        // form handle (no WebView2 dependency), and WebView2 initializes in
        // the background while the app sits in the tray.
        RegisterGlobalHotkey();
        _ = InitializeOverlayAsync();
    }

    private async Task InitializeOverlayAsync()
    {
            try
            {
            var userDataFolder = Path.Combine(Path.GetTempPath(), "YAOLlm", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);
            _webView.DefaultBackgroundColor = Color.FromArgb(0, 0, 0, 0);

            // Inject console log forwarder
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
    (function() {
        var _posting = false;
        var _postMessage = function(msg) {
            if (_posting || !window.chrome || !window.chrome.webview) return;
            _posting = true;
            try { window.chrome.webview.postMessage(msg); }
            catch(e) {}
            finally { _posting = false; }
        };
        var _origLog = console.log.bind(console);
        var _origWarn = console.warn.bind(console);
        var _origError = console.error.bind(console);
        var _origInfo = console.info.bind(console);
        var _handlers = { log: _origLog, warn: _origWarn, error: _origError, info: _origInfo };
        Object.keys(_handlers).forEach(function(method) {
            console[method] = function() {
                var args = Array.from(arguments).map(function(a) {
                    try { return typeof a === 'object' ? JSON.stringify(a) : String(a); }
                    catch(e) { return String(a); }
                }).join(' ');
                _postMessage(JSON.stringify({type: '_console', level: method, message: args}));
                _handlers[method].apply(console, arguments);
            };
        });
        window.addEventListener('error', function(e) {
            _postMessage(JSON.stringify({type: '_console', level: 'error', message: 'Uncaught: ' + (e.message || e) + ' at ' + (e.filename || '') + ':' + (e.lineno || '')}));
        });
    })();
");

            // Create bridge
            _bridge = new WebViewBridge(_webView.CoreWebView2!, _logger);

            // Surface a deferred hotkey-failure warning now that the UI exists
            if (_hotkeyRegistrationFailed)
                _bridge.Warning("Warning: hotkey registration failed.");

            _bridge.SendMessage += OnSendMessage;
            _bridge.CaptureSend += CaptureAndSend;
            _bridge.LoadImage += LoadAndSendImage;
            _bridge.Proceed += () => SendMessage("Please proceed");
            _bridge.Clear += ClearChat;
            _bridge.Hide += HideOverlay;
            _bridge.Exit += Application.Exit;
            _bridge.CycleProvider += CyclePreset;
            _bridge.Stop += StopStreaming;

            // Check if any provider is configured
            if (!_presetManager.HasProvider)
            {
                _bridge.SendNoProvider();
            }

            // Load HTML from temp file (file:// gives proper origin for CDN resources)
            var htmlPath = WriteHtmlToTempFile();

            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                if (e.IsSuccess)
                {
                    if (_webView.InvokeRequired)
                        _webView.Invoke(() => _webView.Visible = true);
                    else
                        _webView.Visible = true;
                    _logger.Log("WebView2: Navigation completed, sending initial state.");
                    _bridge?.Provider(_presetManager.ActivePreset.DisplayName ?? _presetManager.ActivePreset.ToString());
                    _bridge?.Status("Idle");
                }
            };

            _webView.CoreWebView2.Navigate(htmlPath);

            _statusManager.StatusChanged += status =>
            {
                _logger.Log($"StatusChanged event fired: {status}");
                _bridge?.Status(status.ToString());
            };

            SubscribeToProviderStatus();

            _presetManager.PresetChanged += preset =>
            {
                _bridge?.Provider(preset.DisplayName ?? preset.ToString());
            };
            }
            catch (Exception ex)
            {
                _logger.Log($"WebView2 init failed: {ex}");
            }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Tab && this.Visible)
        {
            ApplyPendingPresetSwitch();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SubscribeToProviderStatus()
    {
        _providerStatusHandler = status =>
        {
            switch (status.Kind)
            {
                case ProviderStatusKind.Tts:
                    if (!string.IsNullOrEmpty(status.Detail))
                    {
                        _lastTtsText = status.Detail;
                        _ = _ttsService.SpeakAsync(status.Detail, CancellationToken.None);
                    }
                    break;

                case ProviderStatusKind.Searching:
                    _statusManager.SetStatus(Status.Searching);
                    _onSearchComplete?.Invoke();
                    var searchLabel = status.ServiceName != null
                        ? $"🔍 Searching {status.ServiceName} for: {status.Detail}"
                        : $"🔍 Searching for: {status.Detail}";
                    _bridge?.ChatMessage("system", $"<em>{searchLabel}</em>");
                    break;

                case ProviderStatusKind.Fetching:
                    _statusManager.SetStatus(Status.Fetching);
                    _onSearchComplete?.Invoke();
                    _bridge?.ChatMessage("system", $"<em>📥 Fetching: {status.Detail}</em>");
                    break;

                case ProviderStatusKind.Sending:
                    _statusManager.SetStatus(Status.Sending);
                    break;
            }
        };
        _currentProvider.OnStatusChange += _providerStatusHandler;
    }

    private bool _hotkeyRegistrationFailed;

    private void RegisterGlobalHotkey()
    {
        if (RegisterHotKey(this.Handle, HOTKEY_ID, MOD_WIN, VK_F12))
            _logger.Log("Global hotkey registered.");
        else
        {
            _hotkeyRegistrationFailed = true;
            _logger.Log("Failed to register hotkey.");
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            ToggleVisibility();
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        UnregisterHotKey(this.Handle, HOTKEY_ID);
        _trayIconManager.Dispose();
        _ttsService.Dispose();
        _currentProvider?.Dispose();
        _presetManager.Dispose();
        _sendLock.Dispose();
        _logger.Log("Hotkey unregistered, tray icon disposed, and provider disposed.");
        _logger.Dispose();
    }

    private void OnSendMessage(string text)
    {
        if (!_presetManager.HasProvider)
        {
            _bridge?.Warning("No provider configured. Add presets to ~/.yaollm.conf");
            return;
        }
        SendMessage(text);
    }

    private void CyclePreset()
    {
        ApplyPendingPresetSwitch();
    }

    private void ApplyPendingPresetSwitch()
    {
        if (_sendLock.CurrentCount == 0)
        {
            _presetManager.CycleNext();
            _bridge?.Provider(_presetManager.ActivePreset.DisplayName ?? _presetManager.ActivePreset.ToString());
            _presetManager.SaveConfig();
            _pendingPresetSwitch = true;
            return;
        }

        var oldProvider = _currentProvider;
        if (oldProvider != null && _providerStatusHandler != null)
            oldProvider.OnStatusChange -= _providerStatusHandler;

        _presetManager.CycleNext();
        _currentProvider = _presetManager.CreateProvider();
        SubscribeToProviderStatus();
        _bridge?.Provider(_presetManager.ActivePreset.DisplayName ?? _presetManager.ActivePreset.ToString());
        _presetManager.SaveConfig();

        oldProvider?.Dispose();
    }

    private void StopStreaming()
    {
        _logger.Log("Stop requested by user");
        _ttsService.Stop();
        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Request already finished and disposed between our read and cancel — nothing to stop.
        }
    }

    private void SendMessage(string? message = null, string? imageBase64 = null, string? title = null, bool alreadyShown = false)
    {
        message = (message ?? "").Trim();
        if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(imageBase64)) return;

        if (!_sendLock.Wait(0))
        {
            // Only show the queued bubble the first time; re-queued messages
            // (e.g. lost a race re-acquiring the lock) are already displayed.
            if (!alreadyShown)
                _bridge?.ChatQueued(MarkdownHelper.ToHtml(message));
            _messageQueue.Enqueue((message, imageBase64, title));
            return;
        }

        if (_pendingPresetSwitch)
        {
            _pendingPresetSwitch = false;
            var oldProvider = _currentProvider;
            if (oldProvider != null && _providerStatusHandler != null)
                oldProvider.OnStatusChange -= _providerStatusHandler;
            _currentProvider = _presetManager.CreateProvider();
            SubscribeToProviderStatus();
            _bridge?.Provider(_presetManager.ActivePreset.DisplayName ?? _presetManager.ActivePreset.ToString());
            oldProvider?.Dispose();
        }

        if (!alreadyShown)
            _bridge?.ChatMessageFromMarkdown("user", message);

        _statusManager.SetStatus(Status.Sending);

        if (string.IsNullOrEmpty(title) && _previousWindowHandle != IntPtr.Zero && IsWindow(_previousWindowHandle) && _previousWindowHandle != this.Handle)
        {
            const int nChars = 256;
            var buff = new StringBuilder(nChars);
            if (GetWindowText(_previousWindowHandle, buff, nChars) > 0)
            {
                title = buff.ToString();
                if (title == "YAOLlm")
                    title = string.Empty;
            }
        }

        _ = Task.Run(async () =>
        {
            try { await ProcessLLMRequestAsync(message, imageBase64, title); }
            finally
            {
                _sendLock.Release();
                SendNextQueuedMessage();
            }
        });
    }

    private void SendNextQueuedMessage()
    {
        if (_messageQueue.TryDequeue(out var queued))
            SendMessage(queued.message, queued.imageBase64, queued.title, alreadyShown: true);
    }

    private async Task ProcessLLMRequestAsync(string prompt, string? imageBase64 = null, string? activeWindowTitle = null)
    {
        var userMessage = new ChatMessage(ChatRole.User);
        var fullResponse = new StringBuilder();

        try
        {
            _logger.Log($"Processing LLM request: {prompt}");

            if (!string.IsNullOrEmpty(activeWindowTitle))
                _conversationManager.CurrentWindowTitle = activeWindowTitle;

            var provider = _currentProvider;

            var messages = _conversationManager.GetSnapshot();
            var systemPrompt = messages.Count > 0 ? messages[0].Content ?? "" : "";
            _logger.Log($"[PROMPT]\n{systemPrompt}\n[/PROMPT]");

            byte[]? imageBytes = null;
            if (!string.IsNullOrEmpty(imageBase64))
            {
                imageBytes = Convert.FromBase64String(imageBase64);
            }

            if (!string.IsNullOrEmpty(prompt))
            {
                userMessage.Content = prompt;
            }

            messages.Add(userMessage);

            var tools = provider.SupportsWebSearch
                ? ToolDefinitions.GetAllWithTts()
                : new List<ToolDefinition> { ToolDefinitions.TtsSummary };

            _preToolResponse = null;
            _lastTtsText = null;
            var lastStreamUpdate = DateTime.MinValue;
            _onSearchComplete = () =>
            {
                var preToolText = fullResponse.ToString();
                if (!string.IsNullOrEmpty(preToolText))
                {
                    _bridge?.ChatMessageFromMarkdown("model", preToolText);
                }
                _preToolResponse = preToolText;
                fullResponse.Clear();
            };
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            await foreach (var chunk in provider.StreamAsync(messages, imageBytes, tools).WithCancellation(token))
            {
                if (!string.IsNullOrEmpty(chunk))
                {
                    fullResponse.Append(chunk);

                    if (fullResponse.Length == chunk.Length)
                    {
                        _statusManager.SetStatus(Status.Receiving);
                    }

                    // Back off the re-render cadence as the accumulated response grows,
                    // so we don't re-render the whole transcript through Markdig every 50ms.
                    var streamThrottleMs = fullResponse.Length > 20_000 ? 250
                        : fullResponse.Length > 5_000 ? 100
                        : 50;
                    var now = DateTime.UtcNow;
                    if ((now - lastStreamUpdate).TotalMilliseconds >= streamThrottleMs)
                    {
                        _bridge?.ChatStream(MarkdownHelper.ToHtml(fullResponse.ToString()));
                        lastStreamUpdate = now;
                    }
                }
            }

            var response = fullResponse.ToString();
            var combinedResponse = (_preToolResponse ?? "") + response;

            if (!string.IsNullOrEmpty(combinedResponse))
            {
                _conversationManager.AddExchange(userMessage, combinedResponse);
                UpdateHistoryCounter();
                // Only send a chat_message for the post-tool portion;
                // pre-tool text was already committed as a chat_message in _onSearchComplete
                if (!string.IsNullOrEmpty(response))
                {
                    _bridge?.ChatMessageFromMarkdown("model", response);
                }
            }
            else if (!string.IsNullOrEmpty(_lastTtsText))
            {
                var ttsMessage = $"🔊 The model used TTS to say: {_lastTtsText}";
                _bridge?.ChatMessageFromMarkdown("model", ttsMessage);
                _conversationManager.AddExchange(userMessage, ttsMessage);
                UpdateHistoryCounter();
            }
            else
            {
                _bridge?.Warning("The model returned no response.");
                // Empty exchanges are not persisted — keeps history clean for a retry.
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Log("LLM request cancelled by user");
            var partial = (_preToolResponse ?? "") + fullResponse.ToString();
            var searchSummaries = _currentProvider?.CompletedSearchSummaries;
            var searchCount = _currentProvider?.CompletedSearchCount ?? 0;

            if (!string.IsNullOrEmpty(partial) || searchCount > 0)
            {
                var historyText = partial;
                if (!string.IsNullOrEmpty(searchSummaries))
                    historyText += "\n\n---\n\n*Search results received before stop:*\n\n" + searchSummaries;
                historyText += "\n\n⏹ *[Response stopped by user]*";
                _conversationManager.AddExchange(userMessage, historyText);
                UpdateHistoryCounter();

                if (!string.IsNullOrEmpty(_preToolResponse))
                {
                    if (searchCount > 0)
                        _bridge?.Warning($"⏹ Stopped — {searchCount} search(es) completed.");
                    else
                        _bridge?.Warning("⏹ Stopped.");
                }
                else
                {
                    var displayText = partial;
                    if (searchCount > 0)
                        displayText += $"\n\n⏹ *Stopped — {searchCount} search(es) completed.*";
                    else
                        displayText += "\n\n⏹ *[Response stopped by user]*";
                    _bridge?.ChatMessageFromMarkdown("model", displayText);
                }
            }
            else
            {
                _bridge?.Warning("⏹ Stopped.");
            }
        }
        catch (LLMException ex)
        {
            _logger.Log($"LLM Error: {ex.Message} (StatusCode={ex.StatusCode}, Details={ex.Details})");
            _bridge?.Error($"{ex.UserMessage}");
            // Failed exchanges are not persisted — keeps history clean for a retry.
        }
        catch (Exception ex)
        {
            _logger.Log($"Error in ProcessLLMRequestAsync: {ex.Message}");
            _bridge?.Error($"{ex.Message}");
        }
        finally
        {
            _onSearchComplete = null;
            _preToolResponse = null;
            var cts = _cancellationTokenSource;
            _cancellationTokenSource = null;
            cts?.Dispose();
            _statusManager.SetStatus(Status.Idle);
        }
    }

    private async void CaptureAndSend(string text)
    {
        if (!_presetManager.HasProvider)
        {
            _bridge?.Warning("No provider configured. Add presets to ~/.yaollm.conf");
            return;
        }

        string message = string.IsNullOrEmpty(text.Trim()) ? "[Screenshot Taken]" : text.Trim();

        // Hide the overlay, wait for it to disappear from screen, then capture
        // on a background thread so encoding/resizing doesn't freeze the UI.
        bool wasVisible = this.Visible;
        this.Visible = false;
        try
        {
            await Task.Delay(100);
            string title = ImageService.GetActiveWindowTitle();
            string imageBase64 = await Task.Run(() => ImageService.CapturePrimaryScreen(_logger));

            if (!string.IsNullOrEmpty(imageBase64))
                SendMessage(message, imageBase64, title);
            else
                _bridge?.Error("Error: Screen capture failed.");
        }
        finally
        {
            this.Visible = wasVisible;
            if (wasVisible)
            {
                this.Activate();
                _bridge?.FocusInput();
            }
        }
    }

    private void ClearChat()
    {
        _bridge?.Reset();
        _conversationManager.Initialize(_conversationManager.BuildSystemPrompt());
        UpdateHistoryCounter();
    }

    private void UpdateHistoryCounter()
    {
        int length = _conversationManager.GetTotalCharacterCount();
        _bridge?.History(length);
    }

    private void LoadAndSendImage()
    {
        using var openFileDialog = new OpenFileDialog
        {
            Filter = "Image Files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            Title = "Select an Image"
        };
        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            try
            {
                using var image = new Bitmap(openFileDialog.FileName);
                using var ms = new MemoryStream();
                image.Save(ms, ImageFormat.Png);
                string base64 = Convert.ToBase64String(ms.ToArray());
                string resizedBase64 = ImageService.ResizeImageBase64(_logger, base64);
                SendMessage("[Image Loaded]", resizedBase64, Path.GetFileName(openFileDialog.FileName));
            }
            catch (Exception ex)
            {
                _logger.Log($"Error loading image: {ex.Message}");
                _bridge?.Error($"Error: Failed to load image - {ex.Message}");
            }
        }
    }

    private void ToggleVisibility()
    {
        if (!this.Visible)
            _previousWindowHandle = GetForegroundWindow();
        this.Visible = !this.Visible;

        if (this.Visible)
        {
            this.Activate();
            _bridge?.FocusInput();
        }
    }

    private void HideOverlay() => this.Visible = false;

    private static string WriteHtmlToTempFile()
    {
        var assembly = typeof(MainForm).Assembly;
        var tempDir = Path.Combine(Path.GetTempPath(), "YAOLlm");
        Directory.CreateDirectory(tempDir);

        ExtractResource(assembly, "YAOLlm.ui.index.html", Path.Combine(tempDir, "index.html"));

        var vendorDir = Path.Combine(tempDir, "vendor");
        Directory.CreateDirectory(vendorDir);
        ExtractResource(assembly, "YAOLlm.ui.vendor.tailwind.min.js", Path.Combine(vendorDir, "tailwind.min.js"));
        ExtractResource(assembly, "YAOLlm.ui.vendor.alpine.min.js", Path.Combine(vendorDir, "alpine.min.js"));
        ExtractResource(assembly, "YAOLlm.ui.vendor.highlight.min.js", Path.Combine(vendorDir, "highlight.min.js"));
        ExtractResource(assembly, "YAOLlm.ui.vendor.atom-one-dark.min.css", Path.Combine(vendorDir, "atom-one-dark.min.css"));

        return new Uri(Path.Combine(tempDir, "index.html")).AbsoluteUri;
    }

    private static void ExtractResource(Assembly assembly, string resourceName, string outputPath)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();

        // Skip the write when the file on disk is already identical — avoids
        // pointless disk churn (and WebView2 cache invalidation) on every start.
        try
        {
            if (File.Exists(outputPath))
            {
                var existing = File.ReadAllBytes(outputPath);
                if (existing.Length == bytes.Length && existing.AsSpan().SequenceEqual(bytes))
                    return;
            }
        }
        catch
        {
            // Fall through and rewrite
        }

        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        fileStream.Write(bytes, 0, bytes.Length);
    }
}
