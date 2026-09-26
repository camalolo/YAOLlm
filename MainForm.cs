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
    private readonly SessionStore _sessionStore;
    private readonly TrayIconManager _trayIconManager;
    private readonly WebView2 _webView;
    private WebViewBridge? _bridge;
    private ILLMProvider _currentProvider;
    private Action<ProviderStatus>? _providerStatusHandler;
    private Action? _onSearchComplete;
    private string? _preToolResponse;
    private string? _lastTtsText;

    /// <summary>TTS output toggle — disabled on startup; the UI button turns it on.</summary>
    private bool _ttsEnabled;
    /// <summary>file tools availability — from FILE_READ at startup (default on).</summary>
    private readonly bool _fileReadEnabled;
    /// <summary>User-approved files/directories the file tools may access.</summary>
    private readonly FileAllowlist _fileAllowlist;
    /// <summary>Playwright MCP bridge for the browse_* tools (null = disabled).</summary>
    private readonly IBrowserService? _browserService;
    /// <summary>YouTube transcript tool (null or !IsEnabled = disabled).</summary>
    private readonly IYouTubeCaptionService? _captionService;
    /// <summary>file_write / memory_write service (null or !IsEnabled = disabled).</summary>
    private readonly IFileWriteService? _fileWriteService;
    private bool _pendingPresetSwitch;
    private IntPtr _previousWindowHandle = IntPtr.Zero;
    private readonly Queue<(string? message, string? imageBase64, string? title, string? queuedHtml)> _messageQueue = new();
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern long GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern long SetWindowLong(IntPtr hWnd, int nIndex, long dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    // --- WebView2 process-priority boost (see BoostWebViewProcessPriority) ---
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint HIGH_PRIORITY_CLASS = 0x00000080;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    /// <summary>False until the first user-initiated show removes WS_EX_NOACTIVATE.</summary>
    private bool _activationUnlocked;

    /// <summary>
    /// The form starts with WS_EX_NOACTIVATE: WebView2 initialization (controller
    /// creation + first child show) activates the still-hidden form and yanks
    /// focus from whatever the user was doing (e.g. a fullscreen game). With the
    /// style set, the hidden form can't take foreground at all. It is removed on
    /// the first user-initiated show (see ToggleVisibility) so the overlay
    /// behaves completely normally afterwards.
    /// Note: exonerated during the 2026-09-15 WebView2 0x80070578 outage — the
    /// failure reproduced identically with this style removed (minimal app too).
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (!_activationUnlocked)
                cp.ExStyle |= (int)WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public MainForm(PresetManager presetManager, StatusManager statusManager, Logger logger, FileAllowlist? fileAllowlist = null, IBrowserService? browserService = null, IYouTubeCaptionService? captionService = null, IFileWriteService? fileWriteService = null)
    {
        _presetManager = presetManager ?? throw new ArgumentNullException(nameof(presetManager));
        _statusManager = statusManager ?? throw new ArgumentNullException(nameof(statusManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _currentProvider = _presetManager.CreateProvider();
        _sessionStore = new SessionStore(_logger);
        _conversationManager = new ConversationManager(_logger);
        _conversationManager.TtsEnabled = _ttsEnabled;
        _conversationManager.Initialize(_conversationManager.BuildSystemPrompt());
        _fileReadEnabled = !IsFileReadDisabledByEnv();
        _fileAllowlist = fileAllowlist ?? new FileAllowlist();
        _browserService = browserService;
        _captionService = captionService;
        _fileWriteService = fileWriteService;
        if (_fileWriteService is { IsEnabled: true } writeService)
        {
            // Prompt context exists from the very first message (single fixed
            // memory file, independent of the active app).
            _conversationManager.MemoryFilePath = writeService.CurrentMemoryPath;
            _conversationManager.WritableRoot = writeService.WriteRoot;
            _fileWriteService.Changed += OnFileWriteChanged;
        }
        // Restore before hooking persistence: Initialize() fires the hook and
        // would otherwise overwrite the session file with the empty startup
        // state before the previous session is read.
        RestoreSession();
        _conversationManager.OnHistoryChanged = snapshot => _sessionStore.Save(snapshot, _fileAllowlist.Entries);
        _fileAllowlist.Changed += OnAllowlistChanged;

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
            // Disable the Evergreen runtime's startup phone-home (component
            // updates, variations/field-trials, sync, SafeBrowsing). With
            // runtime 153 + the user's PPPoE/IPv6 change (2026-09-18) those
            // network stalls delayed first navigation of the fully-local UI
            // by 112s — the overlay sat empty for minutes (harness measured:
            // default 112.5s vs flags 9.1s cold). The page and all scripts
            // are vendored; the runtime itself needs no network.
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-component-update --disable-background-networking " +
                    "--disable-sync --no-first-run --no-default-browser-check",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, envOptions);

            // WebView2 controller creation and the first child-window show both
            // briefly ACTIVATE the (still hidden) form — stealing focus from
            // whatever the user was doing (e.g. a fullscreen game). Watch the
            // foreground for a few seconds after init and hand focus straight
            // back whenever the hidden form grabs it.
            var foregroundBeforeInit = GetForegroundWindow();

            // Force the control's handle on the UI thread — controller creation
            // needs a valid, UI-thread-owned parent HWND.
            var webviewHandle = _webView.Handle;
            if (!IsWindow(webviewHandle))
                _logger.Log("WebView2 init: warning — control handle is not a valid window");

            // A relaunch right after a hard kill can race the previous
            // instance's teardown (hotkey, WebView2 browser process,
            // user-data-folder) — controller creation then fails with odd
            // HRESULTs (e.g. 0x80070578 Invalid window handle). Teardown can
            // take a while, so retry patiently (up to ~30s) before giving up,
            // so the tray app never ends up UI-less after a restart.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _webView.EnsureCoreWebView2Async(env);
                    break;
                }
                catch (Exception ex) when (attempt < 10)
                {
                    _logger.Log($"WebView2 init attempt {attempt} failed: {ex.Message} — retrying in 3s");
                    await Task.Delay(3000);
                }
            }

            _ = Task.Run(() => WatchStartupForeground(foregroundBeforeInit, seconds: 5));
            // Opaque page background — do NOT use WebView2 transparency here.
            // DefaultBackgroundColor=Transparent + transparent html/body hits a
            // compositing regression in the Evergreen runtime (~152, Sep 2026):
            // partial repaints (text selection, scrolling) composite regions
            // against white, flashing white boxes / a white chat background.
            // The overlay's translucency comes from FormLayout's form Opacity,
            // so in-page transparency buys nothing.
            _webView.DefaultBackgroundColor = Color.Black;

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
            _bridge.Compact += () => _ = CompactConversationAsync();
            _bridge.AddFiles += AddFilesToAllowlist;
            _bridge.AddFolder += AddFolderToAllowlist;
            _bridge.RemoveAllowedPath += RemoveAllowedPath;
            _bridge.DeleteMemoryFile += DeleteMemoryFile;
            _bridge.ToggleTts += () =>
            {
                _ttsEnabled = !_ttsEnabled;
                // Keeps the system prompt in sync: with TTS off, the model is
                // no longer told about tts_summary and the tool is no longer
                // advertised — no tokens are spent on a summary never played.
                _conversationManager.TtsEnabled = _ttsEnabled;
                _logger.Log($"TTS toggled {(_ttsEnabled ? "ON" : "OFF")} via UI button.");
                _bridge.TtsState(_ttsEnabled);
                if (!_ttsEnabled)
                    _ttsService.Stop();
            };

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
                    // Children (renderer/GPU/utility) all exist by now — raise
                    // the tree so the initial load isn't starved on a busy CPU.
                    BoostWebViewProcessPriority();
                    _bridge?.Provider(_presetManager.ActivePreset.DisplayName ?? _presetManager.ActivePreset.ToString());
                    _bridge?.Status("Idle");
                    _bridge?.TtsState(_ttsEnabled);
                    _bridge?.FileAccessState(_fileReadEnabled);
                    _bridge?.AllowedPaths(_fileAllowlist.Entries);
                    PushMemoryFileState();
                    ReplayRestoredSession();
                    UpdateHistoryCounter();
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

    /// <summary>
    /// Watches the foreground for a few seconds after WebView2 init: whenever
    /// the still-hidden form is foreground (WebView2 stole it), focus goes
    /// straight back to <paramref name="target"/>. Bails out permanently once
    /// the user summons the overlay — from then on focus belongs here.
    /// Safety net next to WS_EX_NOACTIVATE (which already prevents the steal).
    /// </summary>
    private void WatchStartupForeground(IntPtr target, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (Visible) return; // user-initiated show — focus belongs here now
            if (GetForegroundWindow() == Handle)
            {
                // SetForegroundWindow can return true without the switch
                // sticking — verify and retry a few times.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (Visible || GetForegroundWindow() != Handle) return;
                    if (target != IntPtr.Zero && IsWindow(target) && SetForegroundWindow(target)
                        && GetForegroundWindow() != Handle)
                    {
                        _logger.Log("Startup: returned foreground focus stolen by WebView2 init.");
                        return;
                    }
                    Thread.Sleep(100);
                }
                _logger.Log("Startup: WebView2 stole foreground but focus restore failed.");
                return;
            }
            Thread.Sleep(50);
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
                        if (_ttsEnabled)
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

                case ProviderStatusKind.ReadingFile:
                    _statusManager.SetStatus(Status.Reading);
                    _onSearchComplete?.Invoke();
                    _bridge?.ChatMessage("system", $"<em>📄 Reading: {status.Detail}</em>");
                    break;

                case ProviderStatusKind.Browsing:
                    _statusManager.SetStatus(Status.Browsing);
                    _onSearchComplete?.Invoke();
                    // Only navigations get an in-chat line — per-click/per-type
                    // messages would flood the transcript during a browsing run.
                    // A navigation surfaces either as the raw URL (contains "://")
                    // or, when the model set a spoiler-free "info" label, as that
                    // label (ServiceName marks the call as browse_navigate).
                    var isNavigation = !string.IsNullOrEmpty(status.Detail) &&
                        (status.Detail.Contains("://") || status.ServiceName == "browse_navigate");
                    if (isNavigation)
                        _bridge?.ChatMessage("system", $"<em>🌐 Browsing: {status.Detail}</em>");
                    break;

                case ProviderStatusKind.Captions:
                    _statusManager.SetStatus(Status.Captions);
                    _onSearchComplete?.Invoke();
                    _bridge?.ChatMessage("system", $"<em>💬 Extracting captions: {status.Detail}</em>");
                    break;

                case ProviderStatusKind.WritingFile:
                    _statusManager.SetStatus(Status.Writing);
                    _onSearchComplete?.Invoke();
                    _bridge?.ChatMessage("system", $"<em>✍️ Writing: {status.Detail}</em>");
                    break;

                case ProviderStatusKind.Sending:
                    _statusManager.SetStatus(Status.Sending);
                    break;
            }
        };
        _currentProvider.OnStatusChange += _providerStatusHandler;
    }

    private bool _hotkeyRegistrationFailed;

    /// <summary>FILE_READ=off (also no/false/0) disables the file tools.</summary>
    private static bool IsFileReadDisabledByEnv()
    {
        var config = Environment.GetEnvironmentVariable("FILE_READ");
        return config?.Trim().ToLowerInvariant() is "off" or "no" or "false" or "0";
    }

    // ─── File allowlist (user-approved paths for file_read / list_files) ──

    /// <summary>Applies allowlist mutations: system prompt, UI panel, session file.</summary>
    private void OnAllowlistChanged()
    {
        // Rebuilds the system prompt so the model always sees the exact
        // approved set (empty list → prompt says nothing about file access).
        _conversationManager.AllowedPaths = _fileAllowlist.Entries;
        _bridge?.AllowedPaths(_fileAllowlist.Entries);
        _sessionStore.Save(_conversationManager.GetSnapshot(), _fileAllowlist.Entries);
    }

    private void AddFilesToAllowlist()
    {
        if (!_fileReadEnabled) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Add files the assistant may read",
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            foreach (var file in dialog.FileNames)
                _fileAllowlist.Add(file);
        }
    }

    private void AddFolderToAllowlist()
    {
        if (!_fileReadEnabled) return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "Add a folder the assistant may read",
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _fileAllowlist.Add(dialog.SelectedPath);
    }

    private void RemoveAllowedPath(string path) => _fileAllowlist.Remove(path);

    // ─── Memory file (file_write / memory_write) ──────────────────────

    /// <summary>File writes/deletes happen on provider threads — refresh the UI.</summary>
    private void OnFileWriteChanged()
    {
        if (_fileWriteService is not { IsEnabled: true } writeService) return;
        PushMemoryFileState();
    }

    private void PushMemoryFileState()
    {
        if (_fileWriteService is not { IsEnabled: true } writeService) return;
        var path = writeService.CurrentMemoryPath;
        _bridge?.MemoryFile(writeService.MemoryEnabled, path, File.Exists(path));
    }

    /// <summary>
    /// The [x]-twice confirmation landed. Only the fixed memory file can be
    /// reached here — the service re-validates the path against the memory
    /// dir, so a user-added allowlist entry can never be deleted.
    /// </summary>
    private void DeleteMemoryFile()
    {
        if (_fileWriteService is not { IsEnabled: true } writeService) return;
        var message = writeService.DeleteCurrentMemoryFile();
        _bridge?.ChatMessage("system", $"<em>{System.Net.WebUtility.HtmlEncode(message)}</em>");
    }

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
        _browserService?.Dispose();
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
            var queuedHtml = MarkdownHelper.ToHtml(message);
            // Only show the queued bubble the first time; re-queued messages
            // (e.g. lost a race re-acquiring the lock) are already displayed.
            if (!alreadyShown)
                _bridge?.ChatQueued(queuedHtml);
            _messageQueue.Enqueue((message, imageBase64, title, queuedHtml));
            return;
        }

        if (_pendingPresetSwitch)
            ApplyPendingPresetSwitchIfQueued();

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
        {
            // Promote the pending bubble into the main transcript now that
            // this message is actually being answered — keeps the displayed
            // order correct when messages were typed mid-stream.
            _bridge?.ChatDequeued(queued.queuedHtml ?? MarkdownHelper.ToHtml(queued.message));
            SendMessage(queued.message, queued.imageBase64, queued.title, alreadyShown: true);
        }
    }

    /// <summary>
    /// Applies a preset cycle that was deferred because a request was in flight.
    /// Called from both SendMessage and the compaction flow once the send lock
    /// is held, so the new provider is actually used by the next request.
    /// </summary>
    private void ApplyPendingPresetSwitchIfQueued()
    {
        if (!_pendingPresetSwitch) return;
        _pendingPresetSwitch = false;
        var oldProvider = _currentProvider;
        if (oldProvider != null && _providerStatusHandler != null)
            oldProvider.OnStatusChange -= _providerStatusHandler;
        _currentProvider = _presetManager.CreateProvider();
        SubscribeToProviderStatus();
        _bridge?.Provider(_presetManager.ActivePreset.DisplayName ?? _presetManager.ActivePreset.ToString());
        oldProvider?.Dispose();
    }

    // ─── Conversation compaction (opencode-style) ───────────────────────

    private async Task CompactConversationAsync()
    {
        if (!_presetManager.HasProvider)
        {
            _bridge?.Warning("No provider configured. Add presets to ~/.yaollm.conf");
            return;
        }

        if (!_sendLock.Wait(0))
        {
            _bridge?.Warning("Busy — compaction is available once the current request finishes.");
            return;
        }

        // Snapshot under the lock so exchanges completing between the take-over
        // and the summary write can't be dropped by Compact().
        var turns = _conversationManager.GetConversationTurns();
        if (turns.Count == 0)
        {
            _sendLock.Release();
            _bridge?.Warning("Nothing to compact yet — the conversation is empty.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try { await ProcessCompactionAsync(turns); }
            finally
            {
                _sendLock.Release();
                SendNextQueuedMessage();
            }
        });
    }

    private async Task ProcessCompactionAsync(List<ChatMessage> turns)
    {
        var oldCharCount = _conversationManager.GetTotalCharacterCount();
        try
        {
            ApplyPendingPresetSwitchIfQueued();
            var provider = _currentProvider;
            _logger.Log($"Compacting conversation: {turns.Count} turns, {oldCharCount} chars");
            _bridge?.ChatMessage("system", "<em>🗜 Compacting conversation…</em>");

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, BuildCompactionSystemPrompt()),
                new(ChatRole.User, BuildCompactionTranscript(turns)),
            };

            var summary = new StringBuilder();
            var lastStreamUpdate = DateTime.UtcNow;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;
            _statusManager.SetStatus(Status.Sending);

            await foreach (var chunk in provider.StreamAsync(messages, null, null, token).WithCancellation(token))
            {
                if (string.IsNullOrEmpty(chunk)) continue;
                summary.Append(chunk);
                if (summary.Length == chunk.Length)
                    _statusManager.SetStatus(Status.Receiving);

                // Slower render cadence than chat — the compaction output is
                // progress feedback, not content to read while streaming.
                var now = DateTime.UtcNow;
                if ((now - lastStreamUpdate).TotalMilliseconds >= 200)
                {
                    _bridge?.ChatStream(MarkdownHelper.ToHtml(summary.ToString()));
                    lastStreamUpdate = now;
                }
            }

            var result = summary.ToString().Trim();
            if (string.IsNullOrEmpty(result))
            {
                _bridge?.Warning("Compaction failed — the model returned no summary. History unchanged.");
                return;
            }

            _conversationManager.Compact("[Compacted conversation]\n\n" + result);
            var newCharCount = _conversationManager.GetTotalCharacterCount();
            _logger.Log($"Compaction complete: {oldCharCount} → {newCharCount} chars");

            // Fresh visual slate — the live history is now just system + summary.
            _bridge?.Reset();
            _bridge?.ChatMessage("system", $"<em>🗜 Context compacted: {oldCharCount:N0} → {newCharCount:N0} chars</em>");
            _bridge?.ChatMessageFromMarkdown("system", result);
            UpdateHistoryCounter();
        }
        catch (OperationCanceledException)
        {
            _logger.Log("Compaction cancelled by user");
            _bridge?.Warning("⏹ Compaction cancelled — history unchanged.");
        }
        catch (LLMException ex)
        {
            _logger.Log($"Compaction LLM error: {ex.Message}");
            _bridge?.Error($"Compaction failed: {ex.UserMessage}");
        }
        catch (Exception ex)
        {
            _logger.Log($"Compaction error: {ex.Message}");
            _bridge?.Error($"Compaction failed: {ex.Message}");
        }
        finally
        {
            var cts = _cancellationTokenSource;
            _cancellationTokenSource = null;
            cts?.Dispose();
            _statusManager.SetStatus(Status.Idle);
        }
    }

    /// <summary>
    /// Instructions for the summarization pass — the summary must let the
    /// conversation continue as if the full history were still present.
    /// </summary>
    internal static string BuildCompactionSystemPrompt() => """
        You are a conversation summarizer. Compress the conversation below into a compact summary that preserves every detail needed to continue it seamlessly.

        Cover, in order of importance:
        1. The user's goal(s), constraints, and preferences.
        2. All key facts, decisions, and results established so far.
        3. All technical specifics — code, commands, file paths, URLs, names, IDs, numbers — preserved verbatim where they matter.
        4. Open questions, unresolved problems, and agreed next steps.

        Rules:
        - Preserve all details; omit only pleasantries and repetition.
        - Do not answer the conversation or add commentary of your own — output only the summary.
        - Write in the same language as the conversation.
        """;

    /// <summary>
    /// Renders the conversation turns as a labelled transcript for the
    /// summarization pass. Attached images can't be embedded — they are noted
    /// so the summary still accounts for them.
    /// </summary>
    internal static string BuildCompactionTranscript(List<ChatMessage> turns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Conversation to summarize:");
        sb.AppendLine();
        foreach (var turn in turns)
        {
            var speaker = turn.Role switch
            {
                ChatRole.User => "User",
                ChatRole.Model => "Assistant",
                ChatRole.System => "System",
                _ => "Message",
            };
            sb.AppendLine($"[{speaker}]");
            if (turn.Image != null)
                sb.AppendLine("(a screenshot was attached to this message)");
            sb.AppendLine(turn.Content ?? "");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private async Task ProcessLLMRequestAsync(string prompt, string? imageBase64 = null, string? activeWindowTitle = null)
    {
        var userMessage = new ChatMessage(ChatRole.User);
        var fullResponse = new StringBuilder();

        try
        {
            _logger.Log($"Processing LLM request: {prompt}");

            if (!string.IsNullOrEmpty(activeWindowTitle))
            {
                _conversationManager.CurrentWindowTitle = activeWindowTitle;
            }

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

            // tts_summary is advertised only while TTS is enabled — otherwise
            // the model still generates (and pays for) a spoken summary that
            // is never played. The file tools are advertised only while the
            // allowlist is non-empty — an empty list can't serve any read, so
            // advertising would just burn tokens. With everything off and web
            // search unsupported the list is empty and no tools field is sent.
            var fileToolsEnabled = _fileReadEnabled && (_fileAllowlist.Count > 0 || _fileWriteService is { IsEnabled: true });
            var tools = ToolDefinitions.BuildTools(provider.SupportsWebSearch, _ttsEnabled, fileToolsEnabled,
                browseEnabled: _browserService is { IsEnabled: true },
                captionsEnabled: _captionService is { IsEnabled: true },
                fileWriteEnabled: _fileWriteService is { IsEnabled: true },
                memoryEnabled: _fileWriteService is { IsEnabled: true, MemoryEnabled: true });

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

                // Surface silent truncation: finish_reason "length" = output
                // token cap; a missing [DONE]/finish_reason = the connection
                // closed mid-generation (proxy drop, upstream reset).
                if (provider.LastFinishReason == "length")
                {
                    _bridge?.Warning("⚠️ Response was cut off — the model hit its output token limit. Say \"continue\" for the rest.");
                }
                else if (!provider.LastStreamEndedCleanly && !string.IsNullOrEmpty(combinedResponse))
                {
                    _bridge?.Warning("⚠️ Connection closed mid-response — the answer may be incomplete.");
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

    /// <summary>
    /// Rehydrates the previous session (session resume): conversation turns
    /// plus the persisted file allowlist. The persisted system prompt is
    /// discarded — the new session rebuilds it fresh (current date, TTS
    /// state, window title handling, current allowlist); only real
    /// conversation turns resume. The allowlist is restored even when the
    /// previous chat was empty — it is session state, not chat history.
    /// </summary>
    private void RestoreSession()
    {
        try
        {
            var saved = _sessionStore.LoadSession();
            if (saved == null)
            {
                _logger.Log("Session: no previous session to restore.");
                return;
            }

            if (saved.AllowedPaths.Count > 0)
            {
                // Silent bulk restore — the Changed handler isn't attached
                // yet, and the prompt is updated directly.
                _fileAllowlist.Restore(saved.AllowedPaths);
                _conversationManager.AllowedPaths = _fileAllowlist.Entries;
                _logger.Log($"Session: restored {saved.AllowedPaths.Count} allowed path(s).");
            }

            var turns = saved.Messages.Where(m => m.Role != ChatRole.System).ToList();
            if (turns.Count == 0)
            {
                _logger.Log("Session: previous session was empty.");
                return;
            }

            _conversationManager.RestoreTurns(turns);
            _logger.Log($"Session restored: {turns.Count} turns");
        }
        catch (Exception ex)
        {
            _logger.Log($"Session restore failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Replays restored conversation turns into the freshly-loaded UI. Can only
    /// run after NavigationCompleted — web messages posted before the page's
    /// bridge listener attaches are dropped.
    /// </summary>
    private void ReplayRestoredSession()
    {
        if (_bridge == null) return;
        var turns = _conversationManager.GetConversationTurns();
        if (turns.Count == 0) return;

        foreach (var msg in turns)
        {
            // History never contains Error messages; anything but Model renders as user
            var role = msg.Role == ChatRole.Model ? "model" : "user";
            _bridge.ChatMessageFromMarkdown(role, msg.Content ?? "");
        }
        _bridge.ChatMessage("system", "<em>Previous session restored.</em>");
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
        {
            _previousWindowHandle = GetForegroundWindow();
            UnlockActivation();
        }
        this.Visible = !this.Visible;

        if (this.Visible)
        {
            // Re-boost on every show: under a 100%-CPU machine (heavy user
            // computations) the multi-process WebView2 tree gets starved and
            // the overlay hangs for minutes (2026-09-18 incident). Chromium
            // also re-manages renderer priorities when windows hide, so the
            // boost is re-applied at exactly the moment responsiveness matters.
            BoostWebViewProcessPriority();
            this.Activate();
            _bridge?.FocusInput();
        }
    }

    /// <summary>
    /// Raises all msedgewebview2 processes belonging to this app's WebView2
    /// tree to HIGH priority class. Called on overlay show and after the first
    /// navigation. The tree is found by ancestry (browser main process is a
    /// direct child of this process; renderer/GPU/utility spawn under it) —
    /// no command-line parsing, no WMI dependency. Best-effort: failures are
    /// logged and ignored.
    /// </summary>
    private void BoostWebViewProcessPriority()
    {
        try
        {
            var entries = new List<(uint pid, uint ppid, string name)>();
            var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE)
                return;
            try
            {
                var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (Process32FirstW(snap, ref pe))
                {
                    do
                        entries.Add((pe.th32ProcessID, (uint)pe.th32ParentProcessID, pe.szExeFile));
                    while (Process32NextW(snap, ref pe));
                }
            }
            finally
            {
                CloseHandle(snap);
            }

            var ours = (uint)Environment.ProcessId;
            var isWebview = entries.Where(e => e.name == "msedgewebview2.exe")
                .Select(e => e.pid).ToHashSet();
            var childrenByParent = entries.GroupBy(e => e.ppid)
                .ToDictionary(g => g.Key, g => g.Select(e => e.pid).ToList());

            // Descendant walk from the browser main process (direct child).
            var toBoost = new HashSet<uint>();
            var queue = new Queue<uint>(entries
                .Where(e => e.ppid == ours && isWebview.Contains(e.pid))
                .Select(e => e.pid));
            while (queue.Count > 0)
            {
                var pid = queue.Dequeue();
                if (!toBoost.Add(pid))
                    continue;
                if (childrenByParent.TryGetValue(pid, out var kids))
                    foreach (var kid in kids)
                        queue.Enqueue(kid);
            }

            foreach (var pid in toBoost)
            {
                var h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == INVALID_HANDLE_VALUE || h == IntPtr.Zero)
                    continue;
                try
                {
                    SetPriorityClass(h, HIGH_PRIORITY_CLASS);
                }
                finally
                {
                    CloseHandle(h);
                }
            }

            if (toBoost.Count > 0)
                _logger.Log($"WebView2 priority boosted to High for {toBoost.Count} process(es).");
        }
        catch (Exception ex)
        {
            _logger.Log($"WebView2 priority boost failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes WS_EX_NOACTIVATE on the first user-initiated show — from then on
    /// the overlay is a normal activatable window (clicks, Activate() work).
    /// A live ExStyle change doesn't recreate the handle, so WebView2 is safe.
    /// </summary>
    private void UnlockActivation()
    {
        if (_activationUnlocked) return;
        _activationUnlocked = true;
        var exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE, exStyle & ~WS_EX_NOACTIVATE);
        _logger.Log("Overlay activation unlocked for user-initiated show.");
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
