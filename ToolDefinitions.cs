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
        "Search the web for current information. Use ONE well-targeted query per response. Do not issue multiple searches for the same topic.",
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
    /// Get all available tools
    /// </summary>
    public static List<ToolDefinition> GetAll() => new() { WebSearch };
}
