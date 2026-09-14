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
    public OpenAICompatibleProvider(string model, string baseUrl, string? apiKey = null, HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null, string name = "openai-compatible", IFileReadService? fileReadService = null)
        : base(httpClient, searchService, webFetchService, logger, fileReadService)
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

    /// <summary>
    /// DeepSeek (V3.2+/V4) and Z.ai GLM hybrid models take an explicit thinking
    /// toggle in the same dialect: {"thinking": {"type": "enabled|disabled"}}.
    /// REASONING=off turns thinking off entirely; the effort levels map onto
    /// DeepSeek's scale (low/high/max — no "medium" exists), so Medium sends
    /// "high". GLM has no effort parameter, so it only gets the toggle.
    /// Generic openai/openai-compatible profiles fall back to the base class's
    /// portable reasoning_effort mapping.
    /// </summary>
    protected override void ApplyReasoningOptions(Dictionary<string, object> body)
    {
        if (Reasoning == ReasoningMode.Default)
            return;

        var thinkingToggleProfile = _name.Equals("deepseek", StringComparison.OrdinalIgnoreCase) ||
                                    _name.Equals("zai", StringComparison.OrdinalIgnoreCase);
        if (!thinkingToggleProfile)
        {
            base.ApplyReasoningOptions(body);
            return;
        }

        if (Reasoning == ReasoningMode.Off)
        {
            body["thinking"] = new Dictionary<string, object> { ["type"] = "disabled" };
            return;
        }

        body["thinking"] = new Dictionary<string, object> { ["type"] = "enabled" };

        if (_name.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            var effort = Reasoning switch
            {
                ReasoningMode.Low => "low",
                ReasoningMode.Medium => "high",
                ReasoningMode.High => "max",
                _ => null,
            };
            if (effort != null)
                body["reasoning_effort"] = effort;
        }
    }
}
