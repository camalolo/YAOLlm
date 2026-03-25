using Markdig;
using Microsoft.Web.WebView2.WinForms;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YAOLlm
{
    public class ChatRenderer
    {
        private readonly WebView2 _chatBox;
        private readonly Logger _logger;
        private string _chatHtml = "<html></html>";

        private const string DefaultHtmlTemplate = "<html><head><style>html, body { background-color: black; margin: 0; padding: 10px; } body { color: white; font-family: Consolas; font-size: 18px; overflow-y: scroll; scrollbar-width: thin; scrollbar-color: rgba(255, 255, 255, 0.1) transparent; }</style><script>function scrollToBottom() { window.scrollTo(0, document.body.scrollHeight); }</script></head><body></body></html>";

        public ChatRenderer(WebView2 chatBox, Logger logger)
        {
            _chatBox = chatBox ?? throw new ArgumentNullException(nameof(chatBox));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void Reset()
        {
            _chatHtml = DefaultHtmlTemplate;
            _chatBox.InvokeIfRequired(() =>
            {
                if (_chatBox.CoreWebView2 != null)
                    _chatBox.CoreWebView2.NavigateToString(_chatHtml);
            });
        }

        public async Task UpdateChat(string message, string role)
        {
            try
            {
                await _chatBox.InvokeIfRequiredAsync(async () =>
                {
                    if (_chatBox.CoreWebView2 == null) return;

                    string color = role switch
                    {
                        "model" => "yellow",
                        "system" => "lightgray",
                        "error" => "#ff6b6b",
                        _ => "white"
                    };
                    string htmlContent = FormatMarkdownToHtml(message);
                    _chatHtml = _chatHtml.Insert(_chatHtml.IndexOf("</body>"), $"<div style='color:{color};'>{htmlContent}</div>");
                    _chatBox.CoreWebView2.NavigateToString(_chatHtml);
                    await _chatBox.CoreWebView2.ExecuteScriptAsync("scrollToBottom()");
                });
            }
            catch (Exception ex)
            {
                _logger.Log($"Error updating chat: {ex.Message}");
            }
        }

        public async Task UpdateChatStreaming(string content)
        {
            await _chatBox.InvokeIfRequiredAsync(async () =>
            {
                if (_chatBox.CoreWebView2 == null) return;

                var htmlContent = FormatMarkdownToHtml(content);
                var escapedHtml = htmlContent
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "")
                    .Replace("\t", "\\t");

                var script = $@"
                    (function() {{
                        var body = document.body;
                        var lastDiv = body.lastElementChild;
                        if (lastDiv && lastDiv.classList.contains('model')) {{
                            lastDiv.innerHTML = ""{escapedHtml}"";
                        }} else {{
                            var div = document.createElement('div');
                            div.className = 'model';
                            div.style.color = 'yellow';
                            div.style.margin = '10px 0';
                            div.innerHTML = ""{escapedHtml}"";
                            body.appendChild(div);
                        }}
                        scrollToBottom();
                    }})();
";

                try
                {
                    await _chatBox.CoreWebView2.ExecuteScriptAsync(script);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error updating streaming chat: {ex.Message}");
                }
            });
        }

        private string FormatMarkdownToHtml(string text)
        {
            var pipeline = new Markdig.MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .Build();
            return Markdig.Markdown.ToHtml(text, pipeline);
        }
    }
}
