using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace YAOLlm.Providers;

/// <summary>
/// Universal OpenAI-compatible provider pointed at a fully configurable base
/// URL (from PRESET_N_BASE_URL, e.g. http://127.0.0.1:3003/api/v1). No endpoint
/// is hardcoded — the caller supplies the complete version root and the
/// resource path /chat/completions is appended. Serves every OpenAI-style
/// config profile (openai, openai-compatible, deepseek, zai); the profile only
/// affects the display/log name via the `name` parameter.
/// </summary>
public class OpenAICompatibleProvider : OpenAIStyleProvider
{
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string _name;
    private string _model;

    public override string Name => _name;
    public override string Model { get => _model; protected set => _model = value; }
    public override bool SupportsWebSearch => true;

    protected override string StreamUrl => $"{_baseUrl}/chat/completions";

    /// <param name="baseUrl">Endpoint root including any version segment, e.g. http://127.0.0.1:3003/api/v1.</param>
    /// <param name="apiKey">Optional; when set, sent as a Bearer token.</param>
    /// <param name="name">Profile name used in logs and errors (e.g. "deepseek", "zai").</param>
    public OpenAICompatibleProvider(string model, string baseUrl, string? apiKey = null, HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null, string name = "openai-compatible")
        : base(httpClient, searchService, webFetchService, logger)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
        _apiKey = apiKey;
        _name = name ?? throw new ArgumentNullException(nameof(name));
    }

    protected override void CustomizeRequest(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }
}
