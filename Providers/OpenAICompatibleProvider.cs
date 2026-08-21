using System;
using System.Net.Http;

namespace YAOLlm.Providers;

/// <summary>
/// Generic OpenAI-compatible provider pointed at a configurable base URL
/// (defaults to local Ollama-style endpoints). Also serves as the base for
/// hosted variants (DeepSeek, Zai).
/// </summary>
public class OpenAICompatibleProvider : OpenAIStyleProvider
{
    private readonly string _baseUrl;
    private string _model;

    public override string Name => "openai-compatible";
    public override string Model { get => _model; protected set => _model = value; }
    public override bool SupportsWebSearch => true;

    protected virtual string ChatCompletionsPath => "/v1/chat/completions";

    protected override string StreamUrl => $"{_baseUrl}{ChatCompletionsPath}";

    public OpenAICompatibleProvider(string model, string baseUrl = "http://localhost:11434", HttpClient? httpClient = null, ISearchService? searchService = null, IWebFetchService? webFetchService = null, Logger? logger = null)
        : base(httpClient, searchService, webFetchService, logger)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _baseUrl = baseUrl.TrimEnd('/');
    }
}
