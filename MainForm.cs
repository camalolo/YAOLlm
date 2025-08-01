using System.Runtime.InteropServices;
using System.Reflection;
using System.Drawing.Imaging;
using System.Text;
using Microsoft.Web.WebView2.WinForms;
using Markdig;

namespace Gemini
{
    public partial class MainForm : Form
    {
        private readonly GeminiClient _geminiClient;
        private readonly StatusManager _statusManager;
        private readonly Logger _logger;
        private readonly NotifyIcon _trayIcon = new NotifyIcon();
        private readonly WebView2 _chatBox;
        private readonly TextBox _inputField;
        private Button _statusButton;
        private Button _historyButton;
        private string _chatHtml = "<html></html>";

        // Global hotkey constants
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1;
        private const int MOD_CONTROL = 0x0002;
        private const int MOD_WIN = 0x0008;
        private const int VK_F12 = 0x7B;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        // Constructor
        public MainForm(GeminiClient geminiClient, StatusManager statusManager, Logger logger)
        {
            _geminiClient = geminiClient ?? throw new ArgumentNullException(nameof(geminiClient));
            _statusManager = statusManager ?? throw new ArgumentNullException(nameof(statusManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _chatBox = CreateChatBox();
            _inputField = CreateInputField();
            _statusButton = CreateControl<Button>("                  ", DockStyle.None, false, null, null);
            _historyButton = CreateControl<Button>("         ", DockStyle.None, false, null, null);

            ConfigureForm();
            SetupTrayIcon();
            RegisterGlobalHotkey();

            _statusManager.StatusChanged += status =>
            {
                _logger.Log($"StatusChanged event fired: {status}");
                _statusButton.InvokeIfRequired(() => _statusButton.Text = status.ToString());
            };
            _geminiClient.SetUICallbacks(UpdateChat, UpdateHistoryCounter, _statusManager.SetStatus);

            this.FormClosing += MainForm_FormClosing;
            this.Load += async (s, e) => await _chatBox.EnsureCoreWebView2Async(null); // Initialize WebView2
        }

        // UI Setup
        private void ConfigureForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.Opacity = 0.9;
            this.TopMost = true;
            this.BackColor = Color.Black;
            this.Size = Screen.PrimaryScreen?.Bounds.Size ?? new Size(0, 0);
            this.Location = new Point(0, 0);
            this.Padding = new Padding(32, 0, 32, 0);
            this.Visible = false;

            var topPanel = CreateTopPanel();
            var bottomPanel = CreateBottomPanel();
            var spacerPanel = new Panel { Height = 10, Dock = DockStyle.Bottom, BackColor = Color.Black };

            _chatBox.BackColor = Color.Black;

            this.Controls.AddRange(new Control[] { _chatBox, spacerPanel, _inputField, bottomPanel, topPanel });

            ClearChat(); // Initialize webview2 control contents

            _logger.Log($"Form configured: Visible = {this.Visible}");
        }

        private Panel CreateTopPanel()
        {
            var panel = new Panel { Dock = DockStyle.Top, BackColor = Color.Black, Height = 40 };
            panel.Controls.Add(CreateControl<Button>("-", DockStyle.Right, false, HideOverlay));
            panel.Controls.Add(CreateControl<Button>("X", DockStyle.Right, false, Application.Exit));
            return panel;
        }

        private Panel CreateBottomPanel()
        {
            var panel = new Panel { Dock = DockStyle.Bottom, BackColor = Color.Black, Height = 48 };
            var flowPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Location = new Point(0, 5) };
            var rightFlowPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Location = new Point(0, 5) };
            var buttons = new Dictionary<string, (string text, Action action)>
            {
                ["send_tools"] = ("Send (Tools)", () => SendMessage(useTools: true)),
                ["send_mm"] = ("Send (MM)", () => SendMessage(useTools: false)),
                ["capture_send"] = ("Capture && Send", CaptureAndSend),
                ["load_image"] = ("Load Image", LoadAndSendImage),
                ["proceed"] = ("Proceed", () => SendMessage("Please proceed")),
                ["clear"] = ("Clear", ClearChat)
            };

            foreach (var (id, (text, action)) in buttons)
            {
                var btn = CreateControl<Button>(text, DockStyle.None, true, action);
                btn.Tag = id;
                flowPanel.Controls.Add(btn);
            }

            rightFlowPanel.Controls.Add(_statusButton);
            rightFlowPanel.Controls.Add(_historyButton);

            TableLayoutPanel tablePanel = new TableLayoutPanel();

            tablePanel.Dock = DockStyle.Bottom;
            tablePanel.AutoSize = true;
            tablePanel.ColumnCount = 2;
            tablePanel.RowCount = 1;
            tablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tablePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Add controls to the appropriate cells:
            // Column 0: Button panel (flowPanel)
            tablePanel.Controls.Add(flowPanel, 0, 0);
            tablePanel.Controls.Add(rightFlowPanel, 1, 0);

            return tablePanel;
        }

        private WebView2 CreateChatBox()
        {
            var wv = new WebView2
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            wv.CoreWebView2InitializationCompleted += (s, e) =>
            {
                if (e.IsSuccess)
                    wv.CoreWebView2.NavigateToString(_chatHtml);
            };
            return wv;
        }

        private TextBox CreateInputField()
        {
            var input = new TextBox
            {
                Dock = DockStyle.Bottom,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Consolas", 12),
                BorderStyle = BorderStyle.FixedSingle,
                Height = 75,
                Padding = new Padding(10),
                Multiline = true,
                AcceptsReturn = true
            };
            input.KeyDown += InputField_KeyDown;
            return input;
        }

        private T CreateControl<T>(string text, DockStyle dock, bool border = false, Action? click = null, Point? location = null) where T : Control, new()
        {
            var control = new T
            {
                Text = text,
                Font = new Font("Consolas", typeof(T) == typeof(Button) ? 12 : 18),
                BackColor = Color.Black,
                ForeColor = Color.White,
                Dock = dock,
                Height = 38
            };
            if (control is Button btn)
            {
                btn.FlatStyle = FlatStyle.Flat;
                btn.Width = dock == DockStyle.None ? TextRenderer.MeasureText(text, btn.Font).Width + 20 : 20;
                btn.FlatAppearance.BorderSize = border ? 1 : 0;
                if (click != null) btn.Click += (s, e) => click();
            }
            else if (control is Label lbl && dock == DockStyle.None)
            {
                lbl.Width = TextRenderer.MeasureText(text, lbl.Font).Width * 2;
            }
            if (location.HasValue) control.Location = location.Value;
            return control;
        }

        private void SetupTrayIcon()
        {
            _trayIcon.Text = "Gemini Overlay";
            _trayIcon.Visible = true;
            _trayIcon.ContextMenuStrip = new ContextMenuStrip();
            _trayIcon.ContextMenuStrip.Items.Add("Quit", null, (s, e) => Application.Exit());
            _trayIcon.DoubleClick += (s, e) => ToggleVisibility();

            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Gemini.icon.ico");
                _trayIcon.Icon = stream != null ? new Icon(stream) : SystemIcons.Application;
                _logger.Log(stream != null ? "Tray icon loaded from embedded resource." : "Using default tray icon.");
            }
            catch (Exception ex)
            {
                _trayIcon.Icon = SystemIcons.Application;
                _logger.Log($"Error loading tray icon: {ex.Message}");
            }
        }

        // Event Handlers
        private void RegisterGlobalHotkey()
        {
            if (RegisterHotKey(this.Handle, HOTKEY_ID, MOD_WIN, VK_F12))
                _logger.Log("Global hotkey registered.");
            else
            {
                _logger.Log("Failed to register hotkey.");
                UpdateChat("Warning: hotkey registration failed.\r\n", "system");
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
            _trayIcon.Dispose();
            _logger.Log("Hotkey unregistered and tray icon disposed.");
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

        // Core Functionality
        private void SendMessage(string message = "", bool useTools = true)
        {
            message = (message.Length > 0 ? message : _inputField.Text).Trim();
            if (string.IsNullOrEmpty(message)) return;

            UpdateChat($"You: {message}\r\n", "user");
            _inputField.Text = "";
            Task.Run(() => _geminiClient.ProcessLLMRequest(message, null, null, useTools));
        }

        private void SendMessage(string message, string? imageBase64, string? title = null, bool useTools = true)
        {
            message = message.Trim();
            if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(imageBase64)) return;

            UpdateChat($"You: {message}\r\n", "user");
            _inputField.Text = "";
            Task.Run(() => _geminiClient.ProcessLLMRequest(message, imageBase64, title));
        }

        private void CaptureAndSend()
        {
            string message = string.IsNullOrEmpty(_inputField.Text.Trim()) ? "[Screenshot Taken]" : _inputField.Text.Trim();
            var (imageBase64, title) = CaptureScreen();
            if (!string.IsNullOrEmpty(imageBase64))
                SendMessage(message, imageBase64, title, false);
            else
                UpdateChat("Error: Screen capture failed.\r\n", "system");
        }

        private void ClearChat()
        {
            _chatHtml = "<html><head><style>html, body { background-color: black; margin: 0; padding: 10px; } body { color: white; font-family: Consolas; font-size: 18px; overflow-y: scroll; scrollbar-width: thin; scrollbar-color: rgba(255, 255, 255, 0.1) transparent; }</style><script>function scrollToBottom() { window.scrollTo(0, document.body.scrollHeight); }</script></head><body></body></html>";
            _chatBox.InvokeIfRequired(() =>
            {
                if (_chatBox.CoreWebView2 != null)
                    _chatBox.CoreWebView2.NavigateToString(_chatHtml);
            });
            _geminiClient.ClearConversationHistory();
            UpdateHistoryCounter();
            FocusInputField();
        }

        private async void UpdateChat(string message, string role)
        {
            await _chatBox.InvokeIfRequiredAsync(async () =>
            {
                if (_chatBox.CoreWebView2 == null) return;

                string color = role switch
                {
                    "model" => "yellow",
                    "system" => "lightgray",
                    _ => "white"
                };
                string htmlContent = role == "model" && message.Contains("data:image") ? message : FormatMarkdownToHtml(message);
                _chatHtml = _chatHtml.Insert(_chatHtml.IndexOf("</body>"), $"<div style='color:{color};'>{htmlContent}</div>");
                _chatBox.CoreWebView2.NavigateToString(_chatHtml);
                await _chatBox.CoreWebView2.ExecuteScriptAsync("scrollToBottom()");
                FocusInputField();
            });
        }

        private string FormatMarkdownToHtml(string text)
        {
            var pipeline = new Markdig.MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build();
            return Markdig.Markdown.ToHtml(text, pipeline);
        }
        private void UpdateHistoryCounter()
        {
            int length = _geminiClient.GetConversationHistoryLength();
            _historyButton.InvokeIfRequired(() => _historyButton.Text = $"[{length}]");
        }

        private (string, string) CaptureScreen()
        {
            try
            {
                this.Visible = false;
                Thread.Sleep(100);
                string title = GetActiveWindowTitle();
                using var screenshot = new Bitmap(Screen.PrimaryScreen?.Bounds.Width ?? 0, Screen.PrimaryScreen?.Bounds.Height ?? 0);
                using (var g = Graphics.FromImage(screenshot)) g.CopyFromScreen(0, 0, 0, 0, screenshot.Size);
                this.Visible = true;
                using var ms = new MemoryStream();
                screenshot.Save(ms, ImageFormat.Png);
                string base64 = Convert.ToBase64String(ms.ToArray());
                string resizedBase64 = _geminiClient.ResizeImageBase64(base64);
                return (resizedBase64, title);
            }
            catch (Exception ex)
            {
                _logger.Log($"Screen capture error: {ex.Message}");
                return (string.Empty, string.Empty);
            }
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
                    string resizedBase64 = _geminiClient.ResizeImageBase64(base64);
                    string message = string.IsNullOrEmpty(_inputField.Text.Trim()) ? "[Image Loaded]" : _inputField.Text.Trim();
                    SendMessage(message, resizedBase64, Path.GetFileName(openFileDialog.FileName), false);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error loading image: {ex.Message}");
                    UpdateChat($"Error: Failed to load image - {ex.Message}\r\n", "system");
                }
            }
        }

        // Utility Methods
        private void FocusInputField()
        {
            _inputField.InvokeIfRequired(() => { this.Activate(); _inputField.Focus(); });
        }

        private void ToggleVisibility()
        {
            if (!this.Visible) UpdateWindowTitle(); // Update only when showing
            this.Visible = !this.Visible;
            if (this.Visible) FocusInputField();
        }

        private void HideOverlay() => this.Visible = false;


        private void UpdateWindowTitle()
        {
            string title = GetActiveWindowTitle();
            if (!string.IsNullOrEmpty(title))
            {
                _geminiClient.UpdateCurrentWindow(title);
                _logger.Log($"Window title updated to: {title}");
            }
        }

        private string GetActiveWindowTitle()
        {
            const int nChars = 256;
            var buff = new StringBuilder(nChars);
            return GetWindowText(GetForegroundWindow(), buff, nChars) > 0 && buff.ToString() != "Gemini Overlay"
                ? buff.ToString()
                : string.Empty;
        }
    }

    public static class ControlExtensions
    {
        public static void InvokeIfRequired(this Control control, Action action)
        {
            if (control.InvokeRequired)
                control.Invoke(action);
            else
                action();
        }

        public static Bitmap Resize(this Bitmap image, int width, int height)
        {
            var destImage = new Bitmap(width, height);
            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);
            using (var g = Graphics.FromImage(destImage))
                g.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
            return destImage;
        }

        public static async Task InvokeIfRequiredAsync(this Control control, Func<Task> action)
        {
            if (control.InvokeRequired)
                await Task.Run(() => control.Invoke(action));
            else
                await action();
        }

    }
}