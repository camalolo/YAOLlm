using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using System.IO;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace Gemini
{
    public partial class MainForm : Form
    {
        private readonly GeminiClient _geminiClient;
        private readonly StatusManager _statusManager;
        private NotifyIcon _trayIcon;
        private bool _isVisible = false;
        private readonly Logger _logger;

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

            this.KeyPreview = true;
            this.KeyDown += MainForm_KeyDown;
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
            var modelComboBox = new System.Windows.Forms.ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Items = { "gemini-2.0-flash" },
                SelectedIndex = 0,
                Font = new Font("Consolas", 10),
                BackColor = Color.Black,
                ForeColor = Color.White,
                Dock = DockStyle.Left,
                Width = 200
            };
            modelComboBox.SelectedIndexChanged += (s, e) => _geminiClient.UpdateModel(modelComboBox.SelectedItem?.ToString() ?? string.Empty);
            var closeButton = new System.Windows.Forms.Button
            {
                Text = "X",
                Font = new Font("Consolas", 10),
                BackColor = Color.Black,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Dock = DockStyle.Right,
                Width = 40
            };
            closeButton.Click += (s, e) => Application.Exit();

            topPanel.Controls.Add(modelComboBox);
            topPanel.Controls.Add(closeButton);

            var chatBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Consolas", 10),
                BorderStyle = BorderStyle.None
            };
            _chatBox = chatBox;

            var inputField = new System.Windows.Forms.TextBox
            {
                Dock = DockStyle.Bottom,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Consolas", 10),
                BorderStyle = BorderStyle.FixedSingle,
                Height = 30
            };
            inputField.KeyDown += InputField_KeyDown;
            _inputField = inputField;

            var buttonPanel = new Panel { Dock = DockStyle.Bottom, BackColor = Color.Black, Height = 40 };
            var buttons = new[]
            {
                new System.Windows.Forms.Button { Text = "Send", FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ForeColor = Color.White, Font = new Font("Consolas", 10) },
                new System.Windows.Forms.Button { Text = "Capture & Send", FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ForeColor = Color.White, Font = new Font("Consolas", 10) },
                new System.Windows.Forms.Button { Text = "Proceed", FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ForeColor = Color.White, Font = new Font("Consolas", 10) },
                new System.Windows.Forms.Button { Text = "Search Online", FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ForeColor = Color.White, Font = new Font("Consolas", 10) },
                new System.Windows.Forms.Button { Text = "Playing", FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ForeColor = Color.White, Font = new Font("Consolas", 10) },
                new System.Windows.Forms.Button { Text = "Clear", FlatStyle = FlatStyle.Flat, BackColor = Color.Black, ForeColor = Color.White, Font = new Font("Consolas", 10) }
            };

            int x = 10;
            foreach (var btn in buttons)
            {
                btn.Location = new Point(x, 5);
                btn.Click += Button_Click;
                buttonPanel.Controls.Add(btn);
                x += btn.Width + 10;
            }

            var statusLabel = new Label { Text = "Idle", Font = new Font("Consolas", 10), ForeColor = Color.White, BackColor = Color.Black, Location = new Point(x, 10) };
            _statusLabel = statusLabel;
            buttonPanel.Controls.Add(statusLabel);

            var historyLabel = new Label { Text = "History Size: 0 characters", Font = new Font("Consolas", 10), ForeColor = Color.White, BackColor = Color.Black, Dock = DockStyle.Right };
            _historyLabel = historyLabel;
            buttonPanel.Controls.Add(historyLabel);

            this.Controls.Add(chatBox);
            this.Controls.Add(inputField);
            this.Controls.Add(buttonPanel);
            this.Controls.Add(topPanel);

            this.Visible = false;
            _isVisible = false;

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
                Icon = SystemIcons.Application,
                Text = "Gemini Overlay",
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            if (_trayIcon.ContextMenuStrip != null)
            {
                _trayIcon.ContextMenuStrip.Items.Add("Show/Hide", null, (s, e) => ToggleVisibility());
                _trayIcon.ContextMenuStrip.Items.Add("Quit", null, (s, e) => Application.Exit());
            }
            _trayIcon.DoubleClick += (s, e) => ToggleVisibility();
        }

        private void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F12)
                ToggleVisibility();
            else if (e.KeyCode == Keys.Escape)
                HideOverlay();
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
                switch (btn.Text)
                {
                    case "Send": SendMessage(); break;
                    case "Capture & Send": CaptureAndSend(); break;
                    case "Proceed": SendPresetMessage("[Proceed]"); break;
                    case "Search Online": SendPresetMessage("[Search online]"); break;
                    case "Playing": SendPlayingMessage(); break;
                    case "Clear": ClearChat(); break;
                }
            }
        }

        private void SendMessage()
        {
            string userInput = _inputField.Text.Trim();
            if (!string.IsNullOrEmpty(userInput))
            {
                UpdateChat($"You: {userInput}\n", "white");
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
                UpdateChat($"You: {userInput}\n", "white");
                _inputField.Text = "";
            }
            var (imageBase64, activeWindowTitle) = CaptureScreenAndEncode();
            if (imageBase64 != null)
            {
                UpdateChat("Uploading image...\n", "grey");
                Task.Run(() => _geminiClient.ProcessLLMRequest(userInput, imageBase64, activeWindowTitle));
            }
            else
            {
                UpdateChat("Error: Could not capture screen.\n", "grey");
            }
        }

        private void SendPresetMessage(string message)
        {
            UpdateChat($"You: {message}\n", "white");
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
                UpdateChat($"You: {message}\n", "white");
                Task.Run(() => _geminiClient.ProcessLLMRequest(message, string.Empty, string.Empty));
            }
            else
            {
                UpdateChat("Error: Could not get active window title.\n", "grey");
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
            _chatBox?.Invoke((MethodInvoker)(() =>
            {
                _chatBox.SelectionColor = color;
                _chatBox.AppendText(message);
                _chatBox.ScrollToCaret();
            }));
        }

        private void UpdateHistoryCounter()
        {
            int totalChars = _geminiClient.GetConversationHistoryLength();
            _historyLabel?.Invoke((MethodInvoker)(() => _historyLabel.Text = $"History Size: {totalChars} characters"));
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
                UpdateChat($"Error capturing screen: {ex.Message}\n", "grey");
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
                    _statusLabel.Invoke((MethodInvoker)(() => _statusLabel.Text = status.ToString()));
                }
                catch (InvalidOperationException) { Thread.Sleep(100); }
            }
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _trayIcon.Dispose();
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