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

        // Web search goes through the user's proxy endpoint exclusively
        // (server-side provider failover). SEARCH_SERVICES acts as an on/off
        // switch: when set, it must contain "proxy" to enable search.
        var searchServicesConfig = Environment.GetEnvironmentVariable("SEARCH_SERVICES");
        var wantsProxy = string.IsNullOrWhiteSpace(searchServicesConfig) ||
            searchServicesConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(name => name.Equals("proxy", StringComparison.OrdinalIgnoreCase));

        if (wantsProxy)
        {
            var proxyKey = Environment.GetEnvironmentVariable("PROXY_API_KEY") ?? "";
            var proxyUrl = Environment.GetEnvironmentVariable("PROXY_SEARCH_URL") ?? "";

            if (proxyKey.Length > 0 && proxyUrl.Length > 0)
            {
                searchServices.Add(new ProxySearchService(proxyKey, proxyUrl, logger));
                enabledNames.Add("proxy");
            }
            else
            {
                logger.Log("[Startup] PROXY_API_KEY / PROXY_SEARCH_URL not set, web search disabled");
            }
        }
        else
        {
            logger.Log($"[Startup] SEARCH_SERVICES={searchServicesConfig} does not include 'proxy', web search disabled");
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
