using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Reflection;

namespace Gemini
{
    public partial class MainForm : Form
    {
        private readonly GeminiClient _geminiClient;
        private readonly StatusManager _statusManager;
        private NotifyIcon _trayIcon;
        private bool _isVisible = false;
        private readonly Logger _logger;

        // Windows API constants and imports for global hotkey
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 1; // Unique ID for the hotkey
        private const int MOD_CONTROL = 0x0002; // Modifier for Ctrl
        private const int VK_F12 = 0x7B; // Virtual key code for F12

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        public MainForm(GeminiClient geminiClient, StatusManager statusManager)
        {
            _geminiClient = geminiClient;
            _statusManager = statusManager;
            _logger = new Logger($"llm_ui_log_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            _geminiClient.SetUICallbacks(UpdateChat, UpdateHistoryCounter, statusManager.SetStatus);

            _chatBox = new RichTextBox();
            _inputField = new System.Windows.Forms.TextBox();
            _statusLabel = new Label();
            _historyLabel = new Label();
            _trayIcon = new NotifyIcon();

            SetupUI();
            SetupTrayIcon();
            this.FormClosing += MainForm_FormClosing;

            // Register the global hotkey (Ctrl+F12)
            if (!RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CONTROL, VK_F12))
            {
                _logger.Log("Failed to register global hotkey Ctrl+F12. It may be in use by another application.");
            }
            else
            {
                _logger.Log("Global hotkey Ctrl+F12 registered.");
            }

            Task.Run(() => MonitorStatusQueue());

        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ToggleVisibility();
            }
        }

        private void SetupUI()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.Opacity = 0.8;
            this.TopMost = true;
            this.BackColor = Color.Black;
            this.Size = new Size(Screen.PrimaryScreen?.Bounds.Width ?? 0, Screen.PrimaryScreen?.Bounds.Height ?? 0);
            this.Location = new Point(0, 0);

            var topPanel = new Panel { Dock = DockStyle.Top, BackColor = Color.Black, Height = 40 };
            var closeButton = new System.Windows.Forms.Button
            {
                Text = "X",
                Font = new Font("Consolas", 12),
                BackColor = Color.Black,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Right,
                Width = 40
            };
            closeButton.Click += (s, e) => Application.Exit();

            topPanel.Controls.Add(closeButton);

            var chatBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Consolas", 18),
                BorderStyle = BorderStyle.None
            };
            _chatBox = chatBox;

            var inputField = new System.Windows.Forms.TextBox
            {
                Dock = DockStyle.Bottom,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Consolas", 12),
                BorderStyle = BorderStyle.FixedSingle,
                Height = 35
            };
            inputField.KeyDown += InputField_KeyDown;
            _inputField = inputField;

            var buttonPanel = new Panel { Dock = DockStyle.Bottom, BackColor = Color.Black, Height = 48 };
            var buttonData = new Dictionary<string, string>
            {
                { "send", "Send" },
                { "capture_send", "Capture && Send" },
                { "proceed", "Proceed" },
                { "search_online", "Search Online" },
                { "playing", "Playing" },
                { "clear", "Clear" }
            };

            var buttons = new System.Windows.Forms.Button[buttonData.Count];
            int i = 0;
            foreach (var data in buttonData)
            {
                buttons[i] = new System.Windows.Forms.Button
                {
                    Text = data.Value,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.Black,
                    ForeColor = Color.White,
                    Font = new Font("Consolas", 12),
                    Height = 38,
                    Width = TextRenderer.MeasureText(data.Value, new Font("Consolas", 12)).Width + 20,
                    Tag = data.Key // Use Tag property to store the button ID
                };
                i++;
            }

            int x = 10;
            for (int k = 0; k < buttons.Length; k++)
            {
                var btn = buttons[k];
                btn.Location = new Point(x, 5);
                btn.Click += Button_Click;
                buttonPanel.Controls.Add(btn);
                x += btn.Width + 10;
            }

            var statusLabel = new Label { Text = "Idle", Font = new Font("Consolas", 12), ForeColor = Color.White, BackColor = Color.Black, Location = new Point(x, 10), Height = 38, Width = TextRenderer.MeasureText("Idle", new Font("Consolas", 12)).Width * 2 };
            _statusLabel = statusLabel;
            buttonPanel.Controls.Add(statusLabel);

            var historyLabel = new Label { Text = "[0]", Font = new Font("Consolas", 12), ForeColor = Color.White, BackColor = Color.Black, Dock = DockStyle.Right, Height = 38 };
            _historyLabel = historyLabel;
            buttonPanel.Controls.Add(historyLabel);

            this.Controls.Add(chatBox);
            this.Controls.Add(inputField);
            this.Controls.Add(buttonPanel);
            this.Controls.Add(topPanel);

            this.Visible = false;
            _isVisible = false;

            this.Shown += (s, e) => FocusInputField();

            Task.Run(() => MonitorStatusQueue());
        }

        private RichTextBox _chatBox;
        private System.Windows.Forms.TextBox _inputField;
        private Label _statusLabel;
        private Label _historyLabel;

        private void SetupTrayIcon()
        {
            _trayIcon = new NotifyIcon
            {
                Text = "Gemini Overlay",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };

            try
            {
                // Load the embedded icon.png resource
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Gemini.icon.png"))
                {
                    if (stream != null)
                    {
                        using (var bitmap = new Bitmap(stream))
                        {
                            IntPtr hIcon = bitmap.GetHicon();
                            _trayIcon.Icon = Icon.FromHandle(hIcon);
                            _logger.Log("Embedded tray icon loaded from icon.png.");
                        }
                    }
                    else
                    {
                        _logger.Log("Embedded resource 'icon.png' not found. Using default icon.");
                        _trayIcon.Icon = SystemIcons.Application; // Fallback to default
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error loading embedded tray icon: {ex.Message}");
                _trayIcon.Icon = SystemIcons.Application; // Fallback to default on error
            }

            if (_trayIcon.ContextMenuStrip != null)
            {
                _trayIcon.ContextMenuStrip.Items.Add("Show/Hide", null, (s, e) => ToggleVisibility());
                _trayIcon.ContextMenuStrip.Items.Add("Quit", null, (s, e) => Application.Exit());
            }
            _trayIcon.DoubleClick += (s, e) => ToggleVisibility();
        }
        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            // Unregister the hotkey when the form closes
            UnregisterHotKey(this.Handle, HOTKEY_ID);
            _logger.Log("Global hotkey Ctrl+F12 unregistered.");
            _trayIcon.Dispose();
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

        private void Button_Click(object? sender, EventArgs e)
        {
            if (sender is System.Windows.Forms.Button btn)
            {
                string? buttonId = btn.Tag as string;
                switch (buttonId)
                {
                    case null:
                        _logger.Log("Error: Button ID is null.");
                        return;
                    case "send": SendMessage(); break;
                    case "capture_send": CaptureAndSend(); break;
                    case "proceed": SendPresetMessage("[Proceed]"); break;
                    case "search_online": SendPresetMessage("[Search online]"); break;
                    case "playing": SendPlayingMessage(); break;
                    case "clear": ClearChat(); break;
                }
            }
        }

        private void SendMessage()
        {
            string userInput = _inputField.Text.Trim();
            if (!string.IsNullOrEmpty(userInput))
            {
                UpdateChat($"You: {userInput}\r\n", "white");
                _geminiClient.OriginalUserQuery = userInput;
                _inputField.Text = "";
                Task.Run(() => _geminiClient.ProcessLLMRequest(userInput, string.Empty, string.Empty));
            }
        }

        private void CaptureAndSend()
        {
            string userInput = _inputField.Text.Trim();
            if (!string.IsNullOrEmpty(userInput))
            {
                UpdateChat($"You: {userInput}\r\n", "white");
                _inputField.Text = "";
            }
            var (imageBase64, activeWindowTitle) = CaptureScreenAndEncode();
            if (imageBase64 != null)
            {
                UpdateChat("Uploading image...\r\n", "grey");
                Task.Run(() => _geminiClient.ProcessLLMRequest(userInput, imageBase64, activeWindowTitle));
            }
            else
            {
                UpdateChat("Error: Could not capture screen.\r\n", "grey");
            }
        }

        private void SendPresetMessage(string message)
        {
            UpdateChat($"You: {message}\r\n", "white");
            Task.Run(() => _geminiClient.ProcessLLMRequest(message, string.Empty, string.Empty));
        }

        private void SendPlayingMessage()
        {
            this.Visible = false;
            Thread.Sleep(500);
            string activeWindowTitle = GetActiveWindowTitle();
            this.Visible = true;
            FocusInputField();
            if (!string.IsNullOrEmpty(activeWindowTitle) && activeWindowTitle != "Gemini Overlay")
            {
                string message = $"[I am now playing: {activeWindowTitle}]";
                UpdateChat($"You: {message}\r\n", "white");
                Task.Run(() => _geminiClient.ProcessLLMRequest(message, string.Empty, string.Empty));
            }
            else
            {
                UpdateChat("Error: Could not get active window title.\r\n", "grey");
            }
        }

        private void ClearChat()
        {
            _chatBox.Clear();
            _geminiClient.ClearConversationHistory();
            UpdateHistoryCounter();
        }

        private void UpdateChat(string message, string role)
        {
            Color color = role == "model" ? Color.Yellow : role == "system" ? Color.Gray : Color.White;
            _chatBox?.Invoke((System.Windows.Forms.MethodInvoker)(() =>
            {
                _chatBox.SelectionColor = color;
                _chatBox.AppendText(message);
                _chatBox.ScrollToCaret();
            }));
        }

        private void UpdateHistoryCounter()
        {
            int totalChars = _geminiClient.GetConversationHistoryLength();
            _historyLabel?.Invoke((System.Windows.Forms.MethodInvoker)(() => _historyLabel.Text = $"[{totalChars}]"));
        }

        private (string, string) CaptureScreenAndEncode()
        {
            try
            {
                this.Visible = false;
                Thread.Sleep(500);
                string activeWindowTitle = GetActiveWindowTitle();
                Bitmap screenshot = new Bitmap(Screen.PrimaryScreen?.Bounds.Width ?? 0, Screen.PrimaryScreen?.Bounds.Height ?? 0);
                using (Graphics g = Graphics.FromImage(screenshot))
                {
                    g.CopyFromScreen(0, 0, 0, 0, screenshot.Size);
                }
                this.Visible = true;
                FocusInputField();

                using (var ms = new MemoryStream())
                {
                    screenshot.Resize(640, 360).Save(ms, ImageFormat.Png);
                    return (Convert.ToBase64String(ms.ToArray()), activeWindowTitle);
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error capturing screen: {ex.Message}");
                UpdateChat($"Error capturing screen: {ex.Message}\r\n", "grey");
                return (string.Empty, string.Empty);
            }
        }

        private void FocusInputField()
        {
            this.Activate();
            _inputField.Focus();
        }

        private void ToggleVisibility()
        {
            _isVisible = !_isVisible;
            this.Visible = _isVisible;
            if (_isVisible) FocusInputField();
        }

        private void HideOverlay()
        {
            _isVisible = false;
            this.Visible = false;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        private string GetActiveWindowTitle()
        {
            const int nChars = 256;
            System.Text.StringBuilder buff = new System.Text.StringBuilder(nChars);
            IntPtr handle = GetForegroundWindow();
            if (GetWindowText(handle, buff, nChars) > 0)
            {
                string title = buff.ToString();
                return title != "Gemini Overlay" ? title : string.Empty;
            }
            return string.Empty;
        }

        private void MonitorStatusQueue()
        {
            while (true)
            {
                try
                {
                    Status status = _statusManager.GetQueue().Dequeue();
                    _statusLabel.Invoke((System.Windows.Forms.MethodInvoker)(() => _statusLabel.Text = status.ToString()));
                }
                catch (InvalidOperationException) { Thread.Sleep(100); }
            }
        }

    }

    public static class BitmapExtensions
    {
        public static Bitmap Resize(this Bitmap image, int width, int height)
        {
            var destRect = new Rectangle(0, 0, width, height);
            var destImage = new Bitmap(width, height);
            destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);
            using (var g = Graphics.FromImage(destImage))
            {
                g.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
            }
            return destImage;
        }
    }
}