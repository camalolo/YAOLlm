using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Text;
using Markdig;
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
    private Action<string?>? _providerStatusHandler;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private static readonly MarkdownPipeline MdPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 1;
    private const int MOD_WIN = 0x0008;
    private const int VK_F12 = 0x7B;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
            BackColor = Color.Black
        };

        FormLayout.ConfigureForm(this);
        this.Controls.Add(_webView);

        _trayIconManager = new TrayIconManager(ToggleVisibility);
        RegisterGlobalHotkey();

        this.FormClosing += MainForm_FormClosing;
        this.Load += async (s, e) =>
        {
            var userDataFolder = Path.Combine(Path.GetTempPath(), "YAOLlm", "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);
            _webView.DefaultBackgroundColor = Color.FromArgb(0, 0, 0, 0);

            // Log JS console messages for debugging
            _webView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                try
                {
                    var json = e.TryGetWebMessageAsString();
                    if (json.Contains("\"_console\""))
                    {
                        var element = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                        var level = element.GetProperty("level").GetString() ?? "log";
                        var message = element.GetProperty("message").GetString() ?? "";
                        _logger.Log($"[JS:{level}] {message}");
                    }
                }
                catch { }
            };

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

            _bridge.SendMessage += OnSendMessage;
            _bridge.CaptureSend += CaptureAndSend;
            _bridge.LoadImage += LoadAndSendImage;
            _bridge.Proceed += () => SendMessage("Please proceed");
            _bridge.Clear += ClearChat;
            _bridge.Hide += HideOverlay;
            _bridge.Exit += Application.Exit;
            _bridge.CycleProvider += CyclePreset;

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
        };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Tab && this.Visible)
        {
            if (_sendLock.CurrentCount == 0)
            {
                _logger.Log("Preset switch blocked: request in progress.");
                _bridge?.Warning("Cannot switch preset while a request is active.");
                return true;
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
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SubscribeToProviderStatus()
    {
        _providerStatusHandler = status =>
        {
            if (status == StatusManager.SearchingStatus)
                _statusManager.SetStatus(Status.Searching);
            else if (status == null)
                _statusManager.SetStatus(Status.Receiving);
        };
        _currentProvider.OnStatusChange += _providerStatusHandler;
    }

    private void RegisterGlobalHotkey()
    {
        if (RegisterHotKey(this.Handle, HOTKEY_ID, MOD_WIN, VK_F12))
            _logger.Log("Global hotkey registered.");
        else
        {
            _logger.Log("Failed to register hotkey.");
            _bridge?.Warning("Warning: hotkey registration failed.");
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
        _currentProvider?.Dispose();
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
        if (_sendLock.CurrentCount == 0)
        {
            _logger.Log("Preset switch blocked: request in progress.");
            _bridge?.Warning("Cannot switch preset while a request is active.");
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

    private void SendMessage(string? message = null, string? imageBase64 = null, string? title = null)
    {
        message = (message ?? "").Trim();
        if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(imageBase64)) return;

        if (!_sendLock.Wait(0))
        {
            _bridge?.Warning("A request is already in progress.");
            return;
        }

        _bridge?.ChatMessageFromMarkdown("user", message);
        _ = Task.Run(async () =>
        {
            try { await ProcessLLMRequestAsync(message, imageBase64, title); }
            finally { _sendLock.Release(); }
        });
    }

    private async Task ProcessLLMRequestAsync(string prompt, string? imageBase64 = null, string? activeWindowTitle = null)
    {
        try
        {
            _logger.Log($"Processing LLM request: {prompt}");
            _statusManager.SetStatus(Status.Sending);

            if (!string.IsNullOrEmpty(activeWindowTitle))
                _conversationManager.CurrentWindowTitle = activeWindowTitle;

            var provider = _currentProvider;

            var messages = _conversationManager.GetSnapshot();
            var systemPrompt = messages.Count > 0 ? messages[0].Content ?? "" : "";
            _logger.Log($"[PROMPT]\n{systemPrompt}\n[/PROMPT]");

            var userMessage = new ChatMessage(ChatRole.User);

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

            var tools = provider.SupportsWebSearch ? ToolDefinitions.GetAll() : null;

            var fullResponse = new StringBuilder();
            var lastStreamUpdate = DateTime.MinValue;
            var streamThrottleMs = 50;

            await foreach (var chunk in provider.StreamAsync(messages, imageBytes, tools))
            {
                if (!string.IsNullOrEmpty(chunk))
                {
                    fullResponse.Append(chunk);

                    if (fullResponse.Length == chunk.Length)
                    {
                        _statusManager.SetStatus(Status.Receiving);
                    }

                    var now = DateTime.UtcNow;
                    if ((now - lastStreamUpdate).TotalMilliseconds >= streamThrottleMs)
                    {
                        _bridge?.ChatStream(Markdig.Markdown.ToHtml(fullResponse.ToString(), MdPipeline));
                        lastStreamUpdate = now;
                    }
                }
            }

            var response = fullResponse.ToString();

            if (!string.IsNullOrEmpty(response))
            {
                _conversationManager.AddExchange(userMessage, response);
                UpdateHistoryCounter();
                _bridge?.ChatMessageFromMarkdown("model", response);
            }
            else
            {
                _bridge?.Warning("The model returned no response.");
            }
        }
        catch (LLMException ex)
        {
            _logger.Log($"LLM Error: {ex.Message}");
            _bridge?.Warning($"{ex.UserMessage}");
        }
        catch (Exception ex)
        {
            _logger.Log($"Error in ProcessLLMRequestAsync: {ex.Message}");
            _bridge?.Warning($"{ex.Message}");
        }
        finally
        {
            _statusManager.SetStatus(Status.Idle);
        }
    }

    private void CaptureAndSend(string text)
    {
        if (!_presetManager.HasProvider)
        {
            _bridge?.Warning("No provider configured. Add presets to ~/.yaollm.conf");
            return;
        }
        string message = string.IsNullOrEmpty(text.Trim()) ? "[Screenshot Taken]" : text.Trim();
        var (imageBase64, title) = ImageService.CaptureScreen(_logger, this);
        if (!string.IsNullOrEmpty(imageBase64))
            SendMessage(message, imageBase64, title);
        else
            _bridge?.Warning("Error: Screen capture failed.");
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
                _bridge?.Warning($"Error: Failed to load image - {ex.Message}");
            }
        }
    }

    private void ToggleVisibility()
    {
        if (!this.Visible)
        {
            string title = ImageService.GetActiveWindowTitle();
            if (!string.IsNullOrEmpty(title))
                _conversationManager.CurrentWindowTitle = title;
        }
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
        var resourceName = "YAOLlm.ui.index.html";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        
        var tempDir = Path.Combine(Path.GetTempPath(), "YAOLlm");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, "index.html");
        
        using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fileStream);
        
        return new Uri(tempPath).AbsoluteUri;
    }
}
