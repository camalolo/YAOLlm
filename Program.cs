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

        // The file tools (file_read, list_files) are read-only and gated by a
        // user-curated allowlist: the UI file pickers fill it, the tools can
        // only touch what's in it. On by default; FILE_READ=off (also
        // no/false/0) keeps the tools unadvertised and the service unwired.
        FileAllowlist? fileAllowlist = null;
        IFileReadService? fileReadService = null;
        FileWriteService? fileWriteService = null;
        var fileReadConfig = Environment.GetEnvironmentVariable("FILE_READ");
        var fileReadOff = fileReadConfig?.Trim().ToLowerInvariant() is "off" or "no" or "false" or "0";
        if (!fileReadOff)
        {
            fileAllowlist = new FileAllowlist();

            // file_write + the memory file: writes are restricted to
            // the YAOLlm subtree under the system temp dir. FILE_WRITE=off
            // disables the write tools; MEMORY=off keeps file_write but drops
            // the memory file; MEMORY_DIR moves the memory dir (default:
            // %TEMP%\YAOLlm\memory — note temp cleanup tools can wipe it).
            var fileWriteOff = Environment.GetEnvironmentVariable("FILE_WRITE")?.Trim().ToLowerInvariant() is "off" or "no" or "false" or "0";
            var memoryOff = Environment.GetEnvironmentVariable("MEMORY")?.Trim().ToLowerInvariant() is "off" or "no" or "false" or "0";
            if (!fileWriteOff)
            {
                fileWriteService = new FileWriteService(logger,
                    memoryEnabled: !memoryOff,
                    memoryDir: Environment.GetEnvironmentVariable("MEMORY_DIR"));
            }

            fileReadService = new FileReadService(logger, fileAllowlist, fileWriteService?.ImplicitReadRoots);
            logger.Log("[Startup] file_read/list_files tools enabled (allowlist-gated)");
            if (fileWriteService != null)
                logger.Log("[Startup] file_write tool enabled (temp-area only)"
                    + (fileWriteService.MemoryEnabled ? "" : ", memory disabled"));
        }
        else
        {
            logger.Log($"[Startup] FILE_READ={fileReadConfig}, file tools disabled");
        }

        // browse_* tools (Playwright MCP bridge): opt-in, same switch pattern
        // as SEARCH_SERVICES — BROWSER_SERVICES must contain "playwright" to
        // enable. Spawns the globally-installed @playwright/mcp lazily on
        // first browse tool call; the browser session survives preset switches.
        IBrowserService? browserService = null;
        var browserServicesConfig = Environment.GetEnvironmentVariable("BROWSER_SERVICES");
        var wantsBrowser = browserServicesConfig?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(name => name.Equals("playwright", StringComparison.OrdinalIgnoreCase)) == true;
        if (wantsBrowser)
        {
            var headlessOff = Environment.GetEnvironmentVariable("BROWSER_HEADLESS")?.Trim().ToLowerInvariant() is "off" or "no" or "false" or "0";
            browserService = new PlaywrightBrowserService(
                logger,
                headless: !headlessOff,
                channel: Environment.GetEnvironmentVariable("BROWSER_CHANNEL"),
                cdpEndpoint: Environment.GetEnvironmentVariable("BROWSER_CDP_ENDPOINT"),
                cliPath: Environment.GetEnvironmentVariable("BROWSER_MCP_CLI"));
        }
        else
        {
            logger.Log("[Startup] BROWSER_SERVICES does not include 'playwright', browse_* tools disabled");
        }

        // youtube_captions (transcript extraction via yt-dlp): auto-enabled
        // when yt-dlp is on PATH (or YTDLP_PATH points at it); YOUTUBE_CAPTIONS=off
        // forces it off.
        IYouTubeCaptionService? captionService = null;
        var captionsOff = Environment.GetEnvironmentVariable("YOUTUBE_CAPTIONS")?.Trim().ToLowerInvariant() is "off" or "no" or "false" or "0";
        if (!captionsOff)
        {
            captionService = new YouTubeCaptionService(logger, Environment.GetEnvironmentVariable("YTDLP_PATH"));
            if (captionService.IsEnabled)
                logger.Log("[Startup] youtube_captions tool enabled (yt-dlp found)");
            else
                logger.Log("[Startup] yt-dlp not found on PATH, youtube_captions tool disabled");
        }
        else
        {
            logger.Log("[Startup] YOUTUBE_CAPTIONS=off, youtube_captions tool disabled");
        }

        var presetManager = new PresetManager(searchAggregator, webFetchService, logger, fileReadService, browserService, captionService, fileWriteService);
        presetManager.LoadConfig();

        var mainForm = new MainForm(presetManager, statusManager, logger, fileAllowlist, browserService, captionService, fileWriteService);

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
