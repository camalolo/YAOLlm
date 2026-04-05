using System.Net.Http;
using System.Net.Http.Headers;

namespace YAOLlm.Providers;

public class DeepSeekProvider : OpenAICompatibleProvider
{
    private const string BaseUrl = "https://api.deepseek.com";

    public override string Name => "deepseek";

    public DeepSeekProvider(string model, string apiKey, HttpClient? httpClient = null, TavilySearchService? searchService = null, Logger? logger = null)
        : base(model, BaseUrl, CreateHttpClient(apiKey, httpClient), searchService, logger)
    {
    }

    private static HttpClient CreateHttpClient(string apiKey, HttpClient? existing)
    {
        var client = existing ?? new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }
}
