namespace YAOLlm;

/// <summary>
/// Shared tool definitions for function calling
/// </summary>
public static class ToolDefinitions
{
    /// <summary>
    /// Web search tool definition
    /// </summary>
    public static ToolDefinition WebSearch => new(
        "web_search",
        "Search the web for current information.",
        new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "The search query to look up"
                }
            },
            required = new[] { "query" }
        }
    );

    /// <summary>
    /// Web fetch tool definition — downloads the full text content of a URL
    /// </summary>
    public static ToolDefinition WebFetch => new(
        "web_fetch",
        "Download the full text content of a web page or API endpoint by URL. Use this to read a specific page after finding it via web_search, or to fetch known URLs (documentation, APIs, etc.).",
        new
        {
            type = "object",
            properties = new
            {
                url = new
                {
                    type = "string",
                    description = "The absolute URL to fetch"
                }
            },
            required = new[] { "url" }
        }
    );

    /// <summary>
    /// Get all available tools
    /// </summary>
    public static List<ToolDefinition> GetAll() => new() { WebSearch, WebFetch };
}
