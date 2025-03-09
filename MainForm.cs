using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Reflection;
using System.IO;
using System.Drawing.Imaging;

namespace Gemini
{
    public partial class MainForm : Form
    {
        private readonly GeminiClient _geminiClient;
        private readonly StatusManager _statusManager;
        private readonly Logger _logger;
        private NotifyIcon? _trayIcon;
        private RichTextBox? _chatBox;
        private TextBox? _inputField;
        private Label? _statusLabel;
        private Label? _historyLabel;

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
            _trayIcon = new NotifyIcon();

            InitializeComponents();
            ConfigureForm();
            SetupTrayIcon();
            RegisterGlobalHotkey();

            _geminiClient.SetUICallbacks(UpdateChat, UpdateHistoryCounter, _statusManager.SetStatus);
            if (_statusLabel != null) _statusManager.StatusChanged += status => _statusLabel.InvokeIfRequired(() => _statusLabel.Text = status.ToString());
            this.FormClosing += MainForm_FormClosing;
        }

        private void InitializeComponents()
        {
            _chatBox = CreateChatBox();
            _inputField = CreateInputField();
            _statusLabel = CreateLabel("Idle", DockStyle.None, new Point(0, 0));
            _historyLabel = CreateLabel("[0]", DockStyle.Right);
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

            if (_chatBox != null && _inputField != null)
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
            var buttons = new (string id, string text, Action action)[]
            {
                ("send", "Send", SendMessage),
                ("capture_send", "Capture && Send", CaptureAndSend),
                ("proceed", "Proceed", () => SendPresetMessage("[Proceed]")),
                ("search_online", "Search Online", () => SendPresetMessage("[Search online]")),
                ("playing", "Playing", SendPlayingMessage),
                ("clear", "Clear", ClearChat)
            };

            int x = 0;
            foreach (var (id, text, action) in buttons)
            {
                var btn = CreateButton(text, DockStyle.None, action, 1, new Point(x, 5));
                btn.Tag = id;
                panel.Controls.Add(btn);
                x += btn.Width + 10;
            }

            if (_statusLabel != null)
            {
                _statusLabel.Location = new Point(x, 10);
                panel.Controls.Add(_statusLabel);
            }
            if (_historyLabel != null)
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
            if (_trayIcon != null)
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
            _trayIcon?.Dispose();
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

        private void SendMessage()
        {
            if (_inputField != null)
            {
                string message = _inputField.Text.Trim();
                if (string.IsNullOrEmpty(message)) return;
                UpdateChat($"You: {message}\r\n", "white");
                _geminiClient.OriginalUserQuery = message;
                _inputField.Text = "";
                Task.Run(() => _geminiClient.ProcessLLMRequest(message));
            }
        }

        private void CaptureAndSend()
        {
            if (_inputField != null)
            {
                string message = _inputField.Text.Trim();
                if (!string.IsNullOrEmpty(message))
                {
                    UpdateChat($"You: {message}\r\n", "white");
                    _inputField.Text = "";
                }
                var (imageBase64, title) = CaptureScreen();
                if (!string.IsNullOrEmpty(imageBase64))
                    Task.Run(() => _geminiClient.ProcessLLMRequest(message, imageBase64, title));
                else
                    UpdateChat("Error: Screen capture failed.\r\n", "grey");
            }
        }

        private void SendPresetMessage(string message)
        {
            UpdateChat($"You: {message}\r\n", "white");
            Task.Run(() => _geminiClient.ProcessLLMRequest(message));
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
                UpdateChat("Error: Could not detect active window.\r\n", "grey");
        }

        private void ClearChat()
        {
            if (_chatBox != null)
            {
                _chatBox.Clear();
                _geminiClient.ClearConversationHistory();
                UpdateHistoryCounter();
            }
        }

        private void UpdateChat(string message, string role)
        {
            if (_chatBox != null)
            {
            Color color = role switch
            {
                "model" => Color.Yellow,
                "system" => Color.Gray,
                _ => Color.White
            };
            _chatBox.InvokeIfRequired(() =>
            {
                _chatBox.SelectionColor = color;
                _chatBox.AppendText(message);
                _chatBox.ScrollToCaret();
            });
            }
        }

        private void UpdateHistoryCounter()
        {
            if (_historyLabel != null)
            {
            int length = _geminiClient.GetConversationHistoryLength();
            _historyLabel.InvokeIfRequired(() => _historyLabel.Text = $"[{length}]");
            }
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
                UpdateChat($"You: {message}\r\n", "white");

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
            if (_inputField != null) _inputField.InvokeIfRequired(() => { this.Activate(); _inputField.Focus(); });
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