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
            var mainForm = new MainForm(geminiClient, statusManager, logger);
            
            // Use a custom ApplicationContext
            var context = new TrayApplicationContext(mainForm);
            Application.Run(context);
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private readonly MainForm _mainForm;

        public TrayApplicationContext(MainForm form)
        {
            _mainForm = form;
            _mainForm.Visible = false; // Ensure it starts hidden
            _mainForm.FormClosed += (s, e) => Application.Exit(); // Exit when form closes
        }
    }
}