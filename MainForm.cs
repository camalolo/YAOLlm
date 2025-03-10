using System.Runtime.InteropServices;
using System.Reflection;
using System.Drawing.Imaging;
using System.Text;

namespace Gemini
{
    public partial class MainForm : Form
    {
        private readonly GeminiClient _geminiClient;
        private readonly StatusManager _statusManager;
        private readonly Logger _logger;
        private readonly NotifyIcon _trayIcon = new NotifyIcon();
        private readonly RichTextBox _chatBox;
        private readonly TextBox _inputField;
        private readonly Label _statusLabel;
        private readonly Label _historyLabel;

        // Global hotkey constants
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1;
        private const int MOD_CONTROL = 0x0002;
        private const int VK_F12 = 0x7B;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainForm(GeminiClient geminiClient, StatusManager statusManager, Logger logger)
        {
            _geminiClient = geminiClient ?? throw new ArgumentNullException(nameof(geminiClient));
            _statusManager = statusManager ?? throw new ArgumentNullException(nameof(statusManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _chatBox = CreateChatBox();
            _inputField = CreateInputField();
            _statusLabel = CreateLabel("Idle", DockStyle.None, new Point(0, 0));
            _historyLabel = CreateLabel("[0]", DockStyle.Right);

            ConfigureForm();
            SetupTrayIcon();
            RegisterGlobalHotkey();

            _geminiClient.SetUICallbacks(UpdateChat, UpdateHistoryCounter, _statusManager.SetStatus);
            _statusManager.StatusChanged += status => _statusLabel.InvokeIfRequired(() => _statusLabel.Text = status.ToString());
            this.FormClosing += MainForm_FormClosing;
        }

        private void ConfigureForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.Opacity = 0.8;
            this.TopMost = true;
            this.BackColor = Color.Black;
            this.Size = Screen.PrimaryScreen?.Bounds.Size ?? new Size(0, 0);
            this.Location = new Point(0, 0);
            this.Padding = new Padding(32, 0, 32, 0);
            this.Visible = false;

            var topPanel = CreateTopPanel();
            var buttonPanel = CreateButtonPanel();
            var spacerPanel = new Panel { Height = 10, Dock = DockStyle.Bottom, BackColor = Color.Black };

            this.Controls.AddRange(new Control[] { _chatBox, spacerPanel, _inputField, buttonPanel, topPanel });
            _logger.Log($"Form configured: Visible = {this.Visible}");
        }

        private Panel CreateTopPanel()
        {
            var panel = new Panel { Dock = DockStyle.Top, BackColor = Color.Black, Height = 40 };
            panel.Controls.Add(CreateButton("-", DockStyle.Right, HideOverlay, 0));
            panel.Controls.Add(CreateButton("X", DockStyle.Right, Application.Exit, 0));
            return panel;
        }

        private Panel CreateButtonPanel()
        {
            var panel = new Panel { Dock = DockStyle.Bottom, BackColor = Color.Black, Height = 48 };
            var buttons = new Dictionary<string, (string text, Action action)>
            {
                ["send"] = ("Send", SendMessage),
                ["playing"] = ("Playing", SendPlayingMessage),
                ["capture_send"] = ("Capture && Send", CaptureAndSend),
                ["proceed"] = ("Proceed", () => SendPresetMessage("Please proceed")),
                ["clear"] = ("Clear", ClearChat)
            };

            int x = 0;
            foreach (var (id, (text, action)) in buttons)
            {
                var btn = CreateButton(text, DockStyle.None, action, 1, new Point(x, 5));
                btn.Tag = id;
                panel.Controls.Add(btn);
                x += btn.Width + 10;
            }

            _statusLabel.Location = new Point(x, 10);
            panel.Controls.Add(_statusLabel);
            panel.Controls.Add(_historyLabel);

            return panel;
        }

        private RichTextBox CreateChatBox()
        {
            return new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Consolas", 18),
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(10)
            };
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

        private Button CreateButton(string text, DockStyle dock, Action click, int border, Point? location = null)
        {
            var btn = new Button
            {
                Text = text,
                Font = new Font("Consolas", 12),
                BackColor = Color.Black,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Dock = dock,
                Width = dock == DockStyle.None ? TextRenderer.MeasureText(text, new Font("Consolas", 12)).Width + 20 : 20,
                Height = 38,
                FlatAppearance = { BorderSize = border }
            };
            if (location.HasValue) btn.Location = location.Value;
            btn.Click += (s, e) => click();
            return btn;
        }

        private Label CreateLabel(string text, DockStyle dock, Point? location = null)
        {
            var label = new Label
            {
                Text = text,
                Font = new Font("Consolas", 12),
                ForeColor = Color.White,
                BackColor = Color.Black,
                Dock = dock,
                Height = 38
            };
            if (location.HasValue) label.Location = location.Value;
            if (dock == DockStyle.None) label.Width = TextRenderer.MeasureText(text, new Font("Consolas", 12)).Width * 2;
            return label;
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

        private void RegisterGlobalHotkey()
        {
            if (RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL, VK_F12))
                _logger.Log("Global hotkey Ctrl+F12 registered.");
            else
                _logger.Log("Failed to register Ctrl+F12 hotkey.");
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

        private void SendMessage() => SendMessage(_inputField.Text);

        private void CaptureAndSend()
        {
            string message = _inputField.Text.Trim();
            var (imageBase64, title) = CaptureScreen();
            if (!string.IsNullOrEmpty(imageBase64))
                SendMessage(message, imageBase64, title);
            else
                UpdateChat("Error: Screen capture failed.\r\n", "system");
        }

        private void SendPresetMessage(string message) => SendMessage(message);

        private void SendMessage(string message, string? imageBase64 = null, string? title = null)
        {
            message = message.Trim();
            if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(imageBase64)) return;

            UpdateChat($"You: {message}\r\n", "user");
            _geminiClient.OriginalUserQuery = message;
            _inputField.Text = "";
            Task.Run(() => _geminiClient.ProcessLLMRequest(message, imageBase64, title));
        }

        private void SendPlayingMessage()
        {
            this.Visible = false;
            Thread.Sleep(500);
            string title = GetActiveWindowTitle();
            this.Visible = true;
            FocusInputField();
            if (!string.IsNullOrEmpty(title) && title != "Gemini Overlay")
            {
                string message = $"[I am now playing: {title}]";
                SendPresetMessage(message);
            }
            else
                UpdateChat("Error: Could not detect active window.\r\n", "system");
        }

        private void ClearChat()
        {
            _chatBox.Clear();
            _geminiClient.ClearConversationHistory();
            UpdateHistoryCounter();
        }

        private void UpdateChat(string message, string role)
        {
            _chatBox.InvokeIfRequired(() =>
            {
                // Set default font and color for the entire message
                _chatBox.SelectionStart = _chatBox.TextLength;
                Font defaultFont = new Font("Consolas", 18f); // Fallback font
                Color roleColor = role switch
                {
                    "model" => Color.Yellow,
                    "system" => Color.LightGray,
                    _ => Color.White
                };
                _chatBox.SelectionFont = defaultFont;
                _chatBox.SelectionColor = roleColor;

                // Split message into lines
                var lines = message.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
                bool inList = false;

                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine))
                    {
                        if (inList) inList = false;
                        _chatBox.AppendText(Environment.NewLine);
                        continue;
                    }

                    // Ensure color persists for each line
                    _chatBox.SelectionColor = roleColor;

                    // Handle headings
                    if (trimmedLine.StartsWith("# "))
                    {
                        _chatBox.SelectionFont = new Font("Consolas", 24f, FontStyle.Bold); // Larger heading
                        FormatInlineText(trimmedLine.Substring(2));
                        _chatBox.SelectionFont = defaultFont; // Reset
                        _chatBox.AppendText(Environment.NewLine);
                    }
                    else if (trimmedLine.StartsWith("## "))
                    {
                        _chatBox.SelectionFont = new Font("Consolas", 20f, FontStyle.Bold); // Medium heading
                        FormatInlineText(trimmedLine.Substring(3));
                        _chatBox.SelectionFont = defaultFont; // Reset
                        _chatBox.AppendText(Environment.NewLine);
                    }
                    // Handle bulleted lists with * or -
                    else if (trimmedLine.StartsWith("* ") || trimmedLine.StartsWith("- "))
                    {
                        if (!inList)
                        {
                            inList = true;
                            _chatBox.AppendText(" • "); // Bullet character
                        }
                        else
                        {
                            _chatBox.AppendText(Environment.NewLine + " • ");
                        }
                        _chatBox.SelectionIndent = 20; // Indent list items
                        FormatInlineText(trimmedLine.Substring(2));
                        _chatBox.SelectionIndent = 0; // Reset indent
                    }
                    // Regular text
                    else
                    {
                        if (inList) inList = false;
                        FormatInlineText(trimmedLine);
                        _chatBox.AppendText(Environment.NewLine);
                    }
                }

                _chatBox.SelectionStart = _chatBox.TextLength;
                _chatBox.ScrollToCaret();
            });
        }

        // Helper function to handle bold and italic inline formatting
        private void FormatInlineText(string text)
        {
            int start = 0;
            Font defaultFont = new Font("Consolas", 18f); // Fallback font

            while (start < text.Length)
            {
                // Look for bold (**text** or __text__)
                int boldStart = text.IndexOf("**", start);
                if (boldStart == -1) boldStart = text.IndexOf("__", start);
                if (boldStart == -1)
                {
                    _chatBox.AppendText(text.Substring(start));
                    break;
                }

                _chatBox.AppendText(text.Substring(start, boldStart - start));
                int boldEnd = text.IndexOf("**", boldStart + 2);
                if (boldEnd == -1) boldEnd = text.IndexOf("__", boldStart + 2);
                if (boldEnd == -1)
                {
                    _chatBox.AppendText(text.Substring(boldStart));
                    break;
                }

                Font currentFont = _chatBox.SelectionFont ?? defaultFont;
                _chatBox.SelectionFont = new Font(currentFont.FontFamily, currentFont.Size, FontStyle.Bold);
                _chatBox.AppendText(text.Substring(boldStart + 2, boldEnd - boldStart - 2));
                _chatBox.SelectionFont = new Font(currentFont.FontFamily, currentFont.Size, FontStyle.Regular);
                start = boldEnd + 2;
            }

            // Reset position and process italic (*text* or _text_)
            string currentText = _chatBox.Text.Substring(_chatBox.TextLength - text.Length);
            start = 0;
            int offset = _chatBox.TextLength - text.Length;

            while (start < currentText.Length)
            {
                int italicStart = currentText.IndexOf("*", start);
                if (italicStart == -1) italicStart = currentText.IndexOf("_", start);
                if (italicStart == -1 || italicStart + 1 >= currentText.Length)
                    break;

                int italicEnd = currentText.IndexOf("*", italicStart + 1);
                if (italicEnd == -1) italicEnd = currentText.IndexOf("_", italicStart + 1);
                if (italicEnd == -1)
                    break;

                // Apply italic to the range
                _chatBox.Select(offset + italicStart + 1, italicEnd - italicStart - 1);
                Font currentFont = _chatBox.SelectionFont ?? defaultFont;
                _chatBox.SelectionFont = new Font(currentFont.FontFamily, currentFont.Size, currentFont.Style | FontStyle.Italic);
                start = italicEnd + 1;
            }

            // Reset selection
            _chatBox.SelectionStart = _chatBox.TextLength;
            _chatBox.SelectionFont = defaultFont;
        }

        private void UpdateHistoryCounter()
        {
            int length = _geminiClient.GetConversationHistoryLength();
            _historyLabel.InvokeIfRequired(() => _historyLabel.Text = $"[{length}]");
        }

        private (string, string) CaptureScreen()
        {
            try
            {
                this.Visible = false;
                Thread.Sleep(500);
                string title = GetActiveWindowTitle();
                using var screenshot = new Bitmap(Screen.PrimaryScreen?.Bounds.Width ?? 0, Screen.PrimaryScreen?.Bounds.Height ?? 0);
                using (var g = Graphics.FromImage(screenshot))
                    g.CopyFromScreen(0, 0, 0, 0, screenshot.Size);
                this.Visible = true;

                var message = "[Screenshot Taken]";
                UpdateChat($"You: {message}\r\n", "user");

                FocusInputField();

                using var ms = new MemoryStream();
                screenshot.Resize(640, 360).Save(ms, ImageFormat.Png);
                return (Convert.ToBase64String(ms.ToArray()), title);
            }
            catch (Exception ex)
            {
                _logger.Log($"Screen capture error: {ex.Message}");
                return (string.Empty, string.Empty);
            }
        }

        private void FocusInputField()
        {
            _inputField.InvokeIfRequired(() => { this.Activate(); _inputField.Focus(); });
        }

        private void ToggleVisibility()
        {
            this.Visible = !this.Visible;
            if (this.Visible) FocusInputField();
        }

        private void HideOverlay() => this.Visible = false;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        private string GetActiveWindowTitle()
        {
            const int nChars = 256;
            var buff = new System.Text.StringBuilder(nChars);
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
    }
}