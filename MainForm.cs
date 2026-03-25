using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;

namespace YAOLlm
{
    public partial class MainForm : Form
    {
        private readonly PresetManager _presetManager;
        private readonly StatusManager _statusManager;
        private readonly Logger _logger;
        private readonly ChatRenderer _chatRenderer;
        private readonly ConversationManager _conversationManager;
        private readonly TrayIconManager _trayIconManager;
        private readonly WebView2 _chatBox;
        private readonly TextBox _inputField;
        private Button _statusButton;
        private Button _historyButton;
        private Label _providerLabel;
        private ILLMProvider _currentProvider;
        private Action<string?>? _providerStatusHandler;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1;
        private const int MOD_CONTROL = 0x0002;
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

            _presetManager.LoadConfig();
            _currentProvider = _presetManager.CreateProvider();

            _chatBox = FormLayout.CreateChatBox();
            _chatRenderer = new ChatRenderer(_chatBox, _logger);
            _conversationManager = new ConversationManager(_logger);
            _inputField = FormLayout.CreateInputField(InputField_KeyDown);

            var topPanel = FormLayout.CreateTopPanel(HideOverlay, Application.Exit);
            var bottomResult = FormLayout.CreateBottomPanel(
                _presetManager.ActivePreset.ToString(),
                new Dictionary<string, (string, Action)>
                {
                    ["send"] = ("Send", () => SendMessage()),
                    ["capture_send"] = ("Capture && Send", CaptureAndSend),
                    ["load_image"] = ("Load Image", LoadAndSendImage),
                    ["proceed"] = ("Proceed", () => SendMessage("Please proceed")),
                    ["clear"] = ("Clear", ClearChat)
                });
            Panel bottomPanel = bottomResult.panel;
            _statusButton = bottomResult.statusButton;
            _historyButton = bottomResult.historyButton;
            _providerLabel = bottomResult.providerLabel;

            _chatBox.BackColor = Color.Black;
            FormLayout.ConfigureForm(this, new Control[] { _chatBox, _inputField, bottomPanel, topPanel });
            ClearChat();

            _trayIconManager = new TrayIconManager(ToggleVisibility);
            RegisterGlobalHotkey();

            _statusManager.StatusChanged += status =>
            {
                _logger.Log($"StatusChanged event fired: {status}");
                _statusButton.InvokeIfRequired(() => _statusButton.Text = status.ToString());
            };

            SubscribeToProviderStatus();

            _presetManager.PresetChanged += preset =>
            {
                _providerLabel.InvokeIfRequired(() => _providerLabel.Text = $"[{preset}]");
            };

            this.FormClosing += MainForm_FormClosing;
            this.Load += async (s, e) =>
            {
                var userDataFolder = Path.Combine(Path.GetTempPath(), "YAOLlm", "WebView2");
                Directory.CreateDirectory(userDataFolder);
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _chatBox.EnsureCoreWebView2Async(env);
                _chatRenderer.Reset();
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Tab && _inputField.Focused)
            {
                if (_sendLock.CurrentCount == 0)
                {
                    _logger.Log("Preset switch blocked: request in progress.");
                    _ = _chatRenderer.UpdateChat("⚠ Cannot switch preset while a request is active.\r\n", "system");
                    return false;
                }

                var oldProvider = _currentProvider;
                if (oldProvider != null && _providerStatusHandler != null)
                    oldProvider.OnStatusChange -= _providerStatusHandler;

                _presetManager.CycleNext();
                _currentProvider = _presetManager.CreateProvider();
                SubscribeToProviderStatus();
                _providerLabel.Text = $"[{_presetManager.ActivePreset}]";
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
                if (status == "searching")
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
                _ = _chatRenderer.UpdateChat("Warning: hotkey registration failed.\r\n", "system");
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
            _logger.Flush();
            _logger.Log("Hotkey unregistered, tray icon disposed, and provider disposed.");
        }

        private void InputField_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                SendMessage();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
                HideOverlay();
        }

        private void SendMessage(string? message = null, string? imageBase64 = null, string? title = null)
        {
            if (message == null && string.IsNullOrEmpty(imageBase64))
                message = _inputField.Text;

            message = (message ?? "").Trim();
            if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(imageBase64)) return;

            if (!_sendLock.Wait(0))
            {
                _ = _chatRenderer.UpdateChat("⚠ A request is already in progress.\r\n", "system");
                return;
            }

            _ = _chatRenderer.UpdateChat($"You: {message}\r\n", "user");
            _inputField.Text = "";
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

                var provider = _currentProvider;

                var messages = _conversationManager.GetSnapshot();
                var userMessage = new Dictionary<string, object> { { "role", "user" } };

                byte[]? imageBytes = null;
                if (!string.IsNullOrEmpty(imageBase64))
                {
                    imageBytes = Convert.FromBase64String(imageBase64);
                }

                if (!string.IsNullOrEmpty(prompt))
                {
                    userMessage["content"] = prompt;
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
                            _ = _chatRenderer.UpdateChatStreaming(fullResponse.ToString());
                            lastStreamUpdate = now;
                        }
                    }
                }

                var response = fullResponse.ToString();

                _conversationManager.AddExchange(userMessage, response);

                UpdateHistoryCounter();

                if (!string.IsNullOrEmpty(response))
                {
                    _ = _chatRenderer.UpdateChat(response, "model");
                }
            }
            catch (LLMException ex)
            {
                _logger.Log($"LLM Error: {ex.Message}");
                _ = _chatRenderer.UpdateChat($"❌ {ex.UserMessage}\n", "error");
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in ProcessLLMRequestAsync: {ex.Message}");
                _ = _chatRenderer.UpdateChat($"❌ {ex.Message}\n", "error");
            }
            finally
            {
                _statusManager.SetStatus(Status.Idle);
            }
        }

        private void CaptureAndSend()
        {
            string message = string.IsNullOrEmpty(_inputField.Text.Trim()) ? "[Screenshot Taken]" : _inputField.Text.Trim();
            var (imageBase64, title) = ImageService.CaptureScreen(_logger, this);
            if (!string.IsNullOrEmpty(imageBase64))
                SendMessage(message, imageBase64, title);
            else
                _ = _chatRenderer.UpdateChat("Error: Screen capture failed.\r\n", "system");
        }

        private void ClearChat()
        {
            _chatRenderer.Reset();
            _conversationManager.Initialize(_conversationManager.BuildSystemPrompt());
            UpdateHistoryCounter();
            FocusInputField();
        }

        private void UpdateHistoryCounter()
        {
            int length = _conversationManager.GetTotalCharacterCount();
            _historyButton.InvokeIfRequired(() => _historyButton.Text = $"[{length}]");
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
                    string message = string.IsNullOrEmpty(_inputField.Text.Trim()) ? "[Image Loaded]" : _inputField.Text.Trim();
                    SendMessage(message, resizedBase64, Path.GetFileName(openFileDialog.FileName));
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error loading image: {ex.Message}");
                    _ = _chatRenderer.UpdateChat($"Error: Failed to load image - {ex.Message}\r\n", "system");
                }
            }
        }

        private void FocusInputField()
        {
            _inputField.InvokeIfRequired(() => { this.Activate(); _inputField.Focus(); });
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
            if (this.Visible) FocusInputField();
        }

        private void HideOverlay() => this.Visible = false;
    }
}
