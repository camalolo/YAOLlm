using System.IO;
using dotenv.net;

namespace YAOLlm
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".yaollm.conf"
            );
            DotEnv.Load(options: new DotEnvOptions(
                envFilePaths: new[] { configPath },
                ignoreExceptions: true
            ));

            var logger = new Logger();
            var statusManager = new StatusManager();

            var tavilyService = new TavilySearchService(
                Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "",
                logger
            );

            var presetManager = new PresetManager(tavilyService, logger);
            presetManager.LoadConfig();

            var mainForm = new MainForm(presetManager, statusManager, logger);

            var context = new TrayApplicationContext(mainForm);
            Application.Run(context);
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private readonly MainForm _mainForm;

        public TrayApplicationContext(MainForm form)
        {
            _mainForm = form ?? throw new ArgumentNullException(nameof(form));
            _mainForm.Visible = false;
            _mainForm.FormClosed += (s, e) => Application.Exit();
        }
    }
}