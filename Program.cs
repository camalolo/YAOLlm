using System;
using System.Windows.Forms;

namespace Gemini
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var logger = new Logger($"llm_log_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            var statusManager = new StatusManager();
            var geminiClient = new GeminiClient(logger);
            Application.Run(new MainForm(geminiClient, statusManager));
        }
    }
}