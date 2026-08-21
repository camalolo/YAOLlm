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
        // Single instance guard — a second launch would fail the hotkey
        // registration and produce a duplicate tray icon.
        using var mutex = new Mutex(true, "YAOLlm_SingleInstance", out bool createdNew);
        if (!createdNew)
            return;

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
            ignoreExceptions: true,
            probeForEnv: false
        ));

        var logger = new Logger();
        var statusManager = new StatusManager();

        var searchServices = new List<ISearchService>();
        var enabledNames = new List<string>();

        var searchFactories = new (string Name, string EnvVar, Func<string, Logger, ISearchService> Create)[]
        {
            ("exa", "EXA_API_KEY", (key, log) => new ExaSearchService(key, log)),
            ("serper", "SERPER_API_KEY", (key, log) => new SerperSearchService(key, log)),
            ("tavily", "TAVILY_API_KEY", (key, log) => new TavilySearchService(key, log)),
            ("tinyfish", "TINYFISH_API_KEY", (key, log) => new TinyFishSearchService(key, log)),
        };

        void AddSearchService(string name)
        {
            var entry = Array.Find(searchFactories, f => f.Name == name);
            if (entry.Name == null)
            {
                logger.Log($"[Startup] Unknown search service: '{name}'");
                return;
            }

            var apiKey = Environment.GetEnvironmentVariable(entry.EnvVar) ?? "";
            if (string.IsNullOrEmpty(apiKey))
            {
                logger.Log($"[Startup] {entry.EnvVar} not set, skipping {entry.Name} search");
                return;
            }

            searchServices.Add(entry.Create(apiKey, logger));
            enabledNames.Add(entry.Name);
        }

        var searchServicesConfig = Environment.GetEnvironmentVariable("SEARCH_SERVICES");
        if (!string.IsNullOrEmpty(searchServicesConfig))
        {
            foreach (var name in searchServicesConfig.Split(',', StringSplitOptions.RemoveEmptyEntries))
                AddSearchService(name.Trim().ToLowerInvariant());
        }
        else
        {
            // Fallback: TinyFish first, Tavily second
            AddSearchService("tinyfish");
            AddSearchService("tavily");
        }

        logger.Log($"[Startup] Search services: {string.Join(", ", enabledNames)} ({enabledNames.Count} services)");

        var searchAggregator = new SearchServiceAggregator(searchServices, logger);
        var webFetchService = new WebFetchService(logger: logger);

        var presetManager = new PresetManager(searchAggregator, webFetchService, logger);
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
