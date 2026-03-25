using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace YAOLlm
{
    public static class FormLayout
    {
        public static void ConfigureForm(Form form, Control[] controls)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            form.Opacity = 0.9;
            form.TopMost = true;
            form.BackColor = Color.Black;
            form.Size = Screen.PrimaryScreen?.Bounds.Size ?? new Size(0, 0);
            form.Location = new Point(0, 0);
            form.Padding = new Padding(32, 0, 32, 0);
            form.Visible = false;

            var spacerPanel = new Panel { Height = 10, Dock = DockStyle.Bottom, BackColor = Color.Black };
            form.Controls.AddRange(controls.Take(1).Concat(new Control[] { spacerPanel }).Concat(controls.Skip(1)).ToArray());
        }

        public static Panel CreateTopPanel(Action hideOverlay, Action exit)
        {
            var panel = new Panel { Dock = DockStyle.Top, BackColor = Color.Black, Height = 40 };
            panel.Controls.Add(CreateControl<Button>("-", DockStyle.Right, false, hideOverlay));
            panel.Controls.Add(CreateControl<Button>("X", DockStyle.Right, false, exit));
            return panel;
        }

        public static (Panel panel, Button statusButton, Button historyButton, Label providerLabel) CreateBottomPanel(
            string activePreset,
            Dictionary<string, (string text, Action action)> buttonActions)
        {
            var panel = new Panel { Dock = DockStyle.Bottom, BackColor = Color.Black, Height = 48 };

            var flowPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Dock = DockStyle.Left
            };

            var rightFlowPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Dock = DockStyle.Right
            };

            foreach (var (id, (text, action)) in buttonActions)
            {
                var btn = CreateControl<Button>(text, DockStyle.None, true, action);
                btn.Tag = id;
                flowPanel.Controls.Add(btn);
            }

            var statusButton = CreateControl<Button>("                  ", DockStyle.None, false, null, null);
            var historyButton = CreateControl<Button>("         ", DockStyle.None, false, null, null);
            var providerLabel = CreateProviderLabel(activePreset);

            rightFlowPanel.Controls.Add(statusButton);
            rightFlowPanel.Controls.Add(historyButton);
            rightFlowPanel.Controls.Add(providerLabel);

            panel.Controls.Add(rightFlowPanel);
            panel.Controls.Add(flowPanel);

            return (panel, statusButton, historyButton, providerLabel);
        }

        public static WebView2 CreateChatBox()
        {
            var wv = new WebView2
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black
            };
            return wv;
        }

        public static TextBox CreateInputField(KeyEventHandler keyDown)
        {
            var input = new TextBox
            {
                Dock = DockStyle.Bottom,
                BackColor = Color.Black,
                ForeColor = Color.White,
                Font = new Font("Google Sans Code", 12),
                BorderStyle = BorderStyle.FixedSingle,
                Height = 75,
                Padding = new Padding(10),
                Multiline = true,
                AcceptsReturn = true
            };
            input.KeyDown += keyDown;
            return input;
        }

        public static Label CreateProviderLabel(string presetName)
        {
            return new Label
            {
                AutoSize = false,
                Width = 200,
                Height = 48,
                BackColor = Color.Transparent,
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8f),
                Text = $"[{presetName}]",
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private static T CreateControl<T>(string text, DockStyle dock, bool border = false, Action? click = null, Point? location = null) where T : Control, new()
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
    }
}
