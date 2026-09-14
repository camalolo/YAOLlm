using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace YAOLlm.Providers;

/// <summary>
/// OpenAI-style provider for OpenRouter-shaped endpoints. The endpoint root
/// comes from config (PRESET_N_BASE_URL, e.g. https://openrouter.ai/api/v1) —
/// only the protocol headers are hardcoded.
/// </summary>
public class OpenRouterProvider : OpenAIStyleProvider
{
    private const string DefaultReferer = "https://github.com/camalolo/YAOLlm";
    private const string DefaultTitle = "YAOLlm";

    private readonly string? _apiKey;
    private readonly string _baseUrl;

    public override string Name => "openrouter";
    public override string Model { get; protected set; }
    public override bool SupportsWebSearch => true;

    protected override string StreamUrl => $"{_baseUrl}/chat/completions";

    /// <param name="baseUrl">Endpoint root, e.g. https://openrouter.ai/api/v1 (from PRESET_N_BASE_URL).</param>
    public OpenRouterProvider(string model, string? apiKey, string baseUrl, HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null, IFileReadService? fileReadService = null)
        : base(httpClient, searchService, webFetchService, logger, fileReadService)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');

        if (string.IsNullOrEmpty(_apiKey))
            throw new InvalidOperationException("OpenRouter API key not provided. Set PRESET_N_API_KEY or OPENROUTER_API_KEY, or pass apiKey parameter.");
    }

    protected override void CustomizeRequest(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Add("HTTP-Referer", DefaultReferer);
        request.Headers.Add("X-Title", DefaultTitle);
    }

    /// <summary>
    /// OpenRouter's unified reasoning config: {"reasoning": {"enabled": false}}
    /// disables reasoning across all routed models; {"effort": ...} requests a
    /// reasoning level (translated per-provider server-side; OpenRouter's scale
    /// includes minimal/low/medium/high/max).
    /// </summary>
    protected override void ApplyReasoningOptions(Dictionary<string, object> body)
    {
        if (Reasoning == ReasoningMode.Default)
            return;

        if (Reasoning == ReasoningMode.Off)
        {
            body["reasoning"] = new Dictionary<string, object> { ["enabled"] = false };
            return;
        }

        var effort = Reasoning switch
        {
            ReasoningMode.Low => "low",
            ReasoningMode.Medium => "medium",
            ReasoningMode.High => "high",
            _ => null,
        };
        if (effort != null)
            body["reasoning"] = new Dictionary<string, object> { ["effort"] = effort };
    }
}
