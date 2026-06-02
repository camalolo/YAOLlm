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
        var enabledNames = new List<string>();

        var searchServicesConfig = Environment.GetEnvironmentVariable("SEARCH_SERVICES");

        if (!string.IsNullOrEmpty(searchServicesConfig))
        {
            var serviceNames = searchServicesConfig.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var name in serviceNames)
            {
                var trimmed = name.Trim().ToLowerInvariant();
                switch (trimmed)
                {
                    case "exa":
                        var exaKey = Environment.GetEnvironmentVariable("EXA_API_KEY") ?? "";
                        if (!string.IsNullOrEmpty(exaKey))
                        {
                            searchServices.Add(new ExaSearchService(exaKey, logger));
                            enabledNames.Add("exa");
                        }
                        else
                            logger.Log("[Startup] EXA_API_KEY not set, skipping Exa search");
                        break;
                    case "serper":
                        var serperKey = Environment.GetEnvironmentVariable("SERPER_API_KEY") ?? "";
                        if (!string.IsNullOrEmpty(serperKey))
                        {
                            searchServices.Add(new SerperSearchService(serperKey, logger));
                            enabledNames.Add("serper");
                        }
                        else
                            logger.Log("[Startup] SERPER_API_KEY not set, skipping Serper search");
                        break;
                    case "tavily":
                        var tavilyKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
                        if (!string.IsNullOrEmpty(tavilyKey))
                        {
                            searchServices.Add(new TavilySearchService(tavilyKey, logger));
                            enabledNames.Add("tavily");
                        }
                        else
                            logger.Log("[Startup] TAVILY_API_KEY not set, skipping Tavily search");
                        break;
                    case "tinyfish":
                        var tinyFishKey = Environment.GetEnvironmentVariable("TINYFISH_API_KEY") ?? "";
                        if (!string.IsNullOrEmpty(tinyFishKey))
                        {
                            searchServices.Add(new TinyFishSearchService(tinyFishKey, logger));
                            enabledNames.Add("tinyfish");
                        }
                        else
                            logger.Log("[Startup] TINYFISH_API_KEY not set, skipping TinyFish search");
                        break;
                    default:
                        logger.Log($"[Startup] Unknown search service: '{trimmed}'");
                        break;
                }
            }
        }
        else
        {
            // Fallback: TinyFish first, Tavily second
            var tinyFishKey = Environment.GetEnvironmentVariable("TINYFISH_API_KEY") ?? "";
            if (!string.IsNullOrEmpty(tinyFishKey))
            {
                searchServices.Add(new TinyFishSearchService(tinyFishKey, logger));
                enabledNames.Add("tinyfish");
            }
            else
                logger.Log("[Startup] TINYFISH_API_KEY not set, skipping TinyFish search");

            var tavilyKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY") ?? "";
            if (!string.IsNullOrEmpty(tavilyKey))
            {
                searchServices.Add(new TavilySearchService(tavilyKey, logger));
                enabledNames.Add("tavily");
            }
            else
                logger.Log("[Startup] TAVILY_API_KEY not set, skipping Tavily search");
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
