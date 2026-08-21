using Markdig;

namespace YAOLlm;

/// <summary>
/// Shared Markdig pipeline (single instance for the whole app).
/// </summary>
public static class MarkdownHelper
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string ToHtml(string markdown) => Markdig.Markdown.ToHtml(markdown, Pipeline);
}
