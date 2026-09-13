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
        "Download the full text content of a web page or API endpoint by URL. Use this to read a specific page after finding it via web_search, or to fetch known URLs (documentation, APIs, etc.). " +
        "If the fetch returns an Error (bot-blocked page, 404, timeout), report that you could not read the page — never invent or guess its contents.",
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
    /// TTS summary tool definition — LLM provides a concise spoken summary of its response
    /// </summary>
    public static ToolDefinition TtsSummary => new(
        "tts_summary",
        "Provide a concise spoken summary of your response for text-to-speech playback. " +
        "Use conversational language, as if briefly telling a friend what you found. " +
        "Omit tables, lists, code blocks, URLs, and detailed data — just the key takeaway in 1-3 sentences. " +
        "Always call this tool once per response, even if the answer is short.",
        new
        {
            type = "object",
            properties = new
            {
                text = new
                {
                    type = "string",
                    description = "The text to speak aloud — concise, conversational summary"
                }
            },
            required = new[] { "text" }
        }
    );

    /// <summary>
    /// Local file read tool definition — strictly read-only access to text files
    /// </summary>
    public static ToolDefinition FileRead => new(
        "file_read",
        "Read a text file from the local system (read-only; files cannot be modified). " +
        "Use this when the user refers to a file — configs, logs, source code, save games, documents. " +
        "Returns plain text, up to ~15000 characters. For large files, call it again with start_line/end_line " +
        "(1-based, inclusive) to read specific sections. Binary files are rejected.",
        new
        {
            type = "object",
            properties = new
            {
                path = new
                {
                    type = "string",
                    description = "Absolute path of the file to read (e.g. C:\\Users\\me\\notes.txt)"
                },
                start_line = new
                {
                    type = "integer",
                    description = "Optional first line to read (1-based). Defaults to the beginning of the file."
                },
                end_line = new
                {
                    type = "integer",
                    description = "Optional last line to read (inclusive). Defaults to the end of the file."
                }
            },
            required = new[] { "path" }
        }
    );

    /// <summary>
    /// Build the tool list for a request. The TTS summary tool is only
    /// advertised when TTS output is enabled — otherwise the model would
    /// still generate (and pay for) a spoken summary that is never played.
    /// The local file_read tool is advertised when file access is enabled.
    /// </summary>
    public static List<ToolDefinition> BuildTools(bool supportsWebSearch, bool ttsEnabled, bool fileReadEnabled = true)
    {
        var tools = new List<ToolDefinition>();
        if (supportsWebSearch)
        {
            tools.Add(WebSearch);
            tools.Add(WebFetch);
        }
        if (fileReadEnabled)
            tools.Add(FileRead);
        if (ttsEnabled)
            tools.Add(TtsSummary);
        return tools;
    }
}
