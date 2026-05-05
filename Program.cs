using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using dotenv.net;

namespace YAOLlm;

static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AllocConsole();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--console"))
            AllocConsole();

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

        var searchServices = new List<ISearchService>();

        var tinyFishKey = Environment.GetEnvironmentVariable("TINYFISH_API_KEY") ?? "";
        if (!string.IsNullOrEmpty(tinyFishKey))
            searchServices.Add(new TinyFishSearchService(tinyFishKey, logger));
        else
            logger.Log("[Startup] TINYFISH_API_KEY not set, skipping TinyFish search");

        var tavilyKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
        if (!string.IsNullOrEmpty(tavilyKey))
            searchServices.Add(new TavilySearchService(tavilyKey, logger));
        else
            logger.Log("[Startup] TAVILY_API_KEY not set, skipping Tavily search");

        var searchAggregator = new SearchServiceAggregator(searchServices, logger);

        var presetManager = new PresetManager(searchAggregator, logger);
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
