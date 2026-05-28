using System.Net.Http;
using System.Net.Http.Headers;

namespace YAOLlm.Providers;

public class ZaiProvider : OpenAICompatibleProvider
{
    private const string BaseUrl = "https://api.z.ai/api/coding/paas/v4";

    public override string Name => "zai";

    protected override string ChatCompletionsPath => "/chat/completions";

    public ZaiProvider(string model, string apiKey, HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null)
        : base(model, BaseUrl, CreateHttpClient(apiKey, httpClient), searchService, webFetchService, logger)
    {
    }

    private static HttpClient CreateHttpClient(string apiKey, HttpClient? existing)
    {
        var client = existing ?? new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
