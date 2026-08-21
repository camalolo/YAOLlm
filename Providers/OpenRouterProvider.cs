using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace YAOLlm.Providers;

public class OpenRouterProvider : OpenAIStyleProvider
{
    private const string ApiUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string DefaultReferer = "https://github.com/camalolo/YAOLlm";
    private const string DefaultTitle = "YAOLlm";

    private readonly string? _apiKey;

    public override string Name => "openrouter";
    public override string Model { get; protected set; }
    public override bool SupportsWebSearch => true;

    protected override string StreamUrl => ApiUrl;

    public OpenRouterProvider(string model, string? apiKey = null, HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null)
        : base(httpClient, searchService, webFetchService, logger)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("OpenRouter API key not provided. Set OPENROUTER_API_KEY environment variable or pass apiKey parameter.");
    }

    protected override void CustomizeRequest(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Add("HTTP-Referer", DefaultReferer);
        request.Headers.Add("X-Title", DefaultTitle);
    }
}
