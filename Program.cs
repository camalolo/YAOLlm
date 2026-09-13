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

        var proxyKey = Environment.GetEnvironmentVariable("PROXY_API_KEY") ?? "";
        var proxySearchUrl = Environment.GetEnvironmentVariable("PROXY_SEARCH_URL") ?? "";

        if (wantsProxy)
        {
            if (proxyKey.Length > 0 && proxySearchUrl.Length > 0)
            {
                searchServices.Add(new ProxySearchService(proxyKey, proxySearchUrl, logger));
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

        // web_scrape goes through the proxy scrape endpoint the same way search
        // does (server-side bot-block handling). PROXY_SCRAPE_URL wins; if
        // unset, it is derived from PROXY_SEARCH_URL (/search → /scrape).
        // Without proxy config, fetch falls back to the direct fetcher.
        IWebFetchService webFetchService;
        var proxyScrapeUrl = Environment.GetEnvironmentVariable("PROXY_SCRAPE_URL") ?? "";
        if (string.IsNullOrWhiteSpace(proxyScrapeUrl))
            proxyScrapeUrl = ProxyScrapeService.DeriveScrapeUrl(proxySearchUrl) ?? "";

        if (proxyKey.Length > 0 && proxyScrapeUrl.Length > 0)
        {
            webFetchService = new ProxyScrapeService(proxyKey, proxyScrapeUrl, logger);
            logger.Log($"[Startup] web_scrape via proxy scrape: {proxyScrapeUrl}");
        }
        else
        {
            webFetchService = new WebFetchService(logger: logger);
            logger.Log("[Startup] proxy scrape not configured, web_scrape uses direct fetch");
        }

        // file_read is a read-only local file access tool. On by default;
        // FILE_READ=off (also no/false/0) keeps the tool unadvertised and the
        // service unwired.
        IFileReadService? fileReadService = null;
        var fileReadConfig = Environment.GetEnvironmentVariable("FILE_READ");
        var fileReadOff = fileReadConfig?.Trim().ToLowerInvariant() is "off" or "no" or "false" or "0";
        if (!fileReadOff)
        {
            fileReadService = new FileReadService(logger);
            logger.Log("[Startup] file_read tool enabled");
        }
        else
        {
            logger.Log($"[Startup] FILE_READ={fileReadConfig}, file_read tool disabled");
        }

        var presetManager = new PresetManager(searchAggregator, webFetchService, logger, fileReadService);
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
