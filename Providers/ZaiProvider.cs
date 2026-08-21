using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace YAOLlm.Providers;

public class ZaiProvider : OpenAICompatibleProvider
{
    private const string BaseUrl = "https://api.z.ai/api/coding/paas/v4";

    private readonly string _apiKey;

    public override string Name => "zai";

    protected override string ChatCompletionsPath => "/chat/completions";

    public ZaiProvider(string model, string apiKey, HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null)
        : base(model, BaseUrl, httpClient, searchService, webFetchService, logger)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
    }

    protected override void CustomizeRequest(HttpRequestMessage request)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
}
