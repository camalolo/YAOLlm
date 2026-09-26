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
                },
                info = new
                {
                    type = "string",
                    description = "Optional short label shown to the user in the chat instead of the raw query. " +
                                  "Use it when the query itself could reveal a spoiler (e.g. a plot twist the user asked to avoid) " +
                                  "or anything private — give a neutral description like 'checking plot details without spoilers'. " +
                                  "The query is still used for the search itself."
                }
            },
            required = new[] { "query" }
        }
    );

    /// <summary>
    /// Web scrape tool definition — scrapes the full text content of a URL
    /// </summary>
    public static ToolDefinition WebScrape => new(
        "web_scrape",
        "Scrape the full text content of a web page by URL. Use this to read a specific page after finding it via web_search, or to read known URLs (documentation, wikis, etc.). " +
        "If the scrape returns an Error (bot-blocked page, 404, timeout), report that you could not read the page — never invent or guess its contents.",
        new
        {
            type = "object",
            properties = new
            {
                url = new
                {
                    type = "string",
                    description = "The absolute URL to fetch"
                },
                info = new
                {
                    type = "string",
                    description = "Optional short label shown to the user in the chat instead of the raw URL. " +
                                  "Use it when the URL itself could reveal a spoiler (e.g. a wiki page named after a plot twist) — " +
                                  "give a neutral description. The URL is still fetched."
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
    /// Local file read tool definition — strictly read-only access to
    /// user-approved text files
    /// </summary>
    public static ToolDefinition FileRead => new(
        "file_read",
        "Read a text file from the local system (read-only; files cannot be modified). " +
        "Access is restricted: only files that the user explicitly approved — files inside the approved paths — can be read; anything else is rejected. " +
        "Use this when the user refers to a file within the approved paths — configs, logs, source code, documents. " +
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
                    description = "Absolute path of the file to read — must be inside a user-approved path (e.g. C:\\Users\\me\\notes.txt)"
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
    /// Local directory listing tool definition — discovery for the approved paths
    /// </summary>
    public static ToolDefinition FileList => new(
        "list_files",
        "List the immediate contents of a user-approved directory: subdirectories (with a trailing /) and files (with byte size), directories first. " +
        "Use it to explore what exists inside the approved paths before reading files. Only directories that the user approved — or that lie inside an approved directory — can be listed.",
        new
        {
            type = "object",
            properties = new
            {
                path = new
                {
                    type = "string",
                    description = "Absolute path of the directory to list — must be a user-approved directory or inside one (e.g. C:\\Users\\me\\projects)"
                }
            },
            required = new[] { "path" }
        }
    );

    /// <summary>
    /// Browser tools — backed by the @playwright/mcp server (see
    /// PlaywrightBrowserService). Arguments mirror the MCP schemas 1:1 so the
    /// service can forward them without renaming. The workflow is
    /// snapshot-first: browse_snapshot returns element refs that the
    /// interaction tools consume via their "target" parameter.
    /// </summary>
    public static ToolDefinition BrowseNavigate => new(
        "browse_navigate",
        "Open a URL in the Playwright browser and return the page's accessibility snapshot (element refs included). " +
        "Use for pages that need JavaScript rendering, logins, or interaction — plain reading is cheaper via web_scrape. " +
        "After navigating, interact with elements using the refs from the snapshot.",
        new
        {
            type = "object",
            properties = new
            {
                url = new { type = "string", description = "The URL to navigate to" },
                info = new
                {
                    type = "string",
                    description = "Optional short label shown to the user in the chat instead of the raw URL. " +
                                  "Use it when the URL itself could reveal a spoiler (e.g. a wiki page named after a plot twist) — " +
                                  "give a neutral description. The URL is still navigated to."
                }
            },
            required = new[] { "url" }
        }
    );

    public static ToolDefinition BrowseSnapshot => new(
        "browse_snapshot",
        "Capture an accessibility snapshot of the current browser page: a structured text tree of interactive elements, " +
        "each with a ref. Pass the ref as 'target' to browse_click/browse_type. Always take a snapshot after navigating " +
        "or acting, before the next interaction.",
        new
        {
            type = "object",
            properties = new
            {
                target = new { type = "string", description = "Optional element ref to snapshot only a subtree" },
                depth = new { type = "number", description = "Optional limit on the snapshot tree depth" }
            }
        }
    );

    public static ToolDefinition BrowseFind => new(
        "browse_find",
        "Search the current page's accessibility snapshot for text or a regular expression and return the matching " +
        "elements with their refs. Cheaper than dumping a full snapshot on large pages.",
        new
        {
            type = "object",
            properties = new
            {
                text = new { type = "string", description = "Plain text to search for (case-insensitive). Provide text or regex, not both." },
                regex = new { type = "string", description = "Regular expression to search for, e.g. \"/error/i\". Provide text or regex, not both." }
            }
        }
    );

    public static ToolDefinition BrowseClick => new(
        "browse_click",
        "Click an element in the browser. Requires the element ref from browse_snapshot/browse_find. Returns the " +
        "post-click page snapshot.",
        new
        {
            type = "object",
            properties = new
            {
                target = new { type = "string", description = "Element ref from the snapshot (or a unique CSS selector)" },
                element = new { type = "string", description = "Short human-readable description of the element" },
                doubleClick = new { type = "boolean", description = "Double click instead of single click" }
            },
            required = new[] { "target" }
        }
    );

    public static ToolDefinition BrowseType => new(
        "browse_type",
        "Type text into an editable element in the browser. Requires the element ref from browse_snapshot/browse_find.",
        new
        {
            type = "object",
            properties = new
            {
                target = new { type = "string", description = "Element ref from the snapshot (or a unique CSS selector)" },
                text = new { type = "string", description = "Text to type into the element" },
                element = new { type = "string", description = "Short human-readable description of the element" },
                submit = new { type = "boolean", description = "Press Enter after typing (submit forms)" },
                slowly = new { type = "boolean", description = "Type one character at a time (for JS key handlers)" }
            },
            required = new[] { "target", "text" }
        }
    );

    public static ToolDefinition BrowsePressKey => new(
        "browse_press_key",
        "Press a key in the browser (e.g. \"Enter\", \"Escape\", \"ArrowDown\", \"PageDown\").",
        new
        {
            type = "object",
            properties = new
            {
                key = new { type = "string", description = "Key name (e.g. ArrowLeft) or a character to generate" }
            },
            required = new[] { "key" }
        }
    );

    public static ToolDefinition BrowseTabs => new(
        "browse_tabs",
        "List, open, select, or close browser tabs. browse_tabs with action=list shows every open tab; use select with " +
        "an index to switch.",
        new
        {
            type = "object",
            properties = new
            {
                action = new { type = "string", description = "One of: list, new, close, select", @enum = new[] { "list", "new", "close", "select" } },
                index = new { type = "number", description = "Tab index for select/close (close: current tab if omitted)" },
                url = new { type = "string", description = "URL to open for action=new" }
            },
            required = new[] { "action" }
        }
    );

    public static ToolDefinition BrowseWaitFor => new(
        "browse_wait_for",
        "Wait in the browser until text appears, text disappears, or a fixed time passes — use after actions that " +
        "trigger loading before taking a fresh snapshot.",
        new
        {
            type = "object",
            properties = new
            {
                time = new { type = "number", description = "Seconds to wait" },
                text = new { type = "string", description = "Wait until this text appears" },
                textGone = new { type = "string", description = "Wait until this text disappears" }
            }
        }
    );

    public static ToolDefinition BrowseBack => new(
        "browse_back",
        "Go back to the previous page in the browser history and return the new page snapshot.",
        new { type = "object", properties = new { } }
    );

    public static ToolDefinition BrowseClose => new(
        "browse_close",
        "Close the current browser page. Use when done with a site to keep the browser tidy.",
        new { type = "object", properties = new { } }
    );

    public static ToolDefinition BrowseEvaluate => new(
        "browse_evaluate",
        "Run a JavaScript function inside the browser page and return its JSON-serializable result. " +
        "Use it when the page content is not in the DOM — canvas maps, JS-rendered widgets — to read the app's own state, " +
        "e.g. \"() => Object.keys(window)\", \"() => map.getZoom()\", or \"() => JSON.stringify(store.state.markers)\". " +
        "The function must be self-contained: \"() => { ... }\".",
        new
        {
            type = "object",
            properties = new
            {
                function = new { type = "string", description = "() => { /* code */ } — self-contained JS function executed in the page" }
            },
            required = new[] { "function" }
        }
    );

    public static ToolDefinition BrowseNetwork => new(
        "browse_network",
        "List the network requests the current browser page has made (numbered). Canvas maps and JS apps usually load " +
        "their data from JSON endpoints — find them here, then use browse_network_request to read a specific response.",
        new
        {
            type = "object",
            properties = new
            {
                filter = new { type = "string", description = "Only include requests whose URL matches this regexp, e.g. \"/api/.*marker\"" },
                @static = new { type = "boolean", description = "Include static resources (images, fonts, scripts); default false" }
            }
        }
    );

    public static ToolDefinition BrowseNetworkRequest => new(
        "browse_network_request",
        "Return the full details (headers, body) of one network request from the browse_network list — the response body " +
        "of a data endpoint often contains exactly the content rendered into canvas.",
        new
        {
            type = "object",
            properties = new
            {
                index = new { type = "integer", description = "1-based index from the browse_network list" },
                part = new { type = "string", description = "Optional: return only this part (request-headers, request-body, response-headers, response-body)", @enum = new[] { "request-headers", "request-body", "response-headers", "response-body" } }
            },
            required = new[] { "index" }
        }
    );

    /// <summary>
    /// YouTube transcript tool definition — captions/subtitles via yt-dlp
    /// </summary>
    public static ToolDefinition YouTubeCaptions => new(
        "youtube_captions",
        "Fetch the spoken transcript (captions/subtitles) of a YouTube video as plain text — use it to summarize a video, " +
        "answer questions about its content, or quote it. Works with manual and auto-generated caption tracks; videos " +
        "without any track return an error. Set timestamps=true to prefix each line with its time (useful to point at a " +
        "moment), language to pick a track (e.g. \"en\", \"pl\").",
        new
        {
            type = "object",
            properties = new
            {
                url = new
                {
                    type = "string",
                    description = "Absolute YouTube video URL (e.g. https://www.youtube.com/watch?v=...)"
                },
                language = new
                {
                    type = "string",
                    description = "Optional caption language code, e.g. \"en\" or \"pl\". Defaults to English, then the video's language."
                },
                timestamps = new
                {
                    type = "boolean",
                    description = "Optional. When true, each transcript line is prefixed with its timestamp."
                },
                info = new
                {
                    type = "string",
                    description = "Optional short label shown to the user in the chat instead of the raw video URL. " +
                                  "Use it when the URL/title could reveal a spoiler — give a neutral description. The video is still fetched."
                }
            },
            required = new[] { "url" }
        }
    );

    /// <summary>
    /// Local file write tool — restricted to the YAOLlm temp area
    /// </summary>
    public static ToolDefinition FileWrite => new(
        "file_write",
        "Write a text file inside the assistant's writable area (the YAOLlm folder under the system temp dir — the exact root is given in the system prompt). " +
        "Nothing outside that area can be written. Use it for scratch files, exports, or notes. " +
        "For game memories use memory_write instead — it always targets the memory file.",
        new
        {
            type = "object",
            properties = new
            {
                path = new
                {
                    type = "string",
                    description = "Absolute path inside the writable area (parent directories are created automatically)"
                },
                content = new
                {
                    type = "string",
                    description = "Full text to write (UTF-8)"
                },
                append = new
                {
                    type = "boolean",
                    description = "Optional. When true, content is appended instead of overwriting."
                }
            },
            required = new[] { "path", "content" }
        }
    );

    /// <summary>
    /// Memory edit tool — one fixed memory file, path resolved server-side
    /// </summary>
    public static ToolDefinition MemoryWrite => new(
        "memory_write",
        "Write to your persistent memory file (the path is in the system prompt; this tool always targets that one file). " +
        "Append durable knowledge — item locations, solutions, mechanics, boss strategies, build notes — so you never search for the same thing twice. " +
        "Read the file (file_read) before appending so you don't duplicate what is already known. " +
        "Ops: append (default) adds content at the end; replace swaps one exact 'find' snippet for content (match must be unique — include enough surrounding text); overwrite rewrites the whole file (use sparingly, to reorganize or compact).",
        new
        {
            type = "object",
            properties = new
            {
                content = new
                {
                    type = "string",
                    description = "append: the text to add. replace: the replacement text. overwrite: the entire new file content."
                },
                op = new
                {
                    type = "string",
                    description = "append (default), replace, or overwrite",
                    @enum = new[] { "append", "replace", "overwrite" }
                },
                find = new
                {
                    type = "string",
                    description = "replace only: the exact existing text to replace (must match exactly once)"
                }
            },
            required = new[] { "content" }
        }
    );

    /// <summary>
    /// Build the tool list for a request. The TTS summary tool is only
    /// advertised when TTS output is enabled — otherwise the model would
    /// still generate (and pay for) a spoken summary that is never played.
    /// The local file tools are advertised when file access is enabled AND
    /// reading is possible (non-empty allowlist, or the write/memory roots
    /// exist — the caller gates on both). The browse_* tools are advertised
    /// when the Playwright MCP bridge is enabled; youtube_captions when
    /// yt-dlp is available.
    /// </summary>
    public static List<ToolDefinition> BuildTools(bool supportsWebSearch, bool ttsEnabled,
        bool fileReadEnabled = true, bool browseEnabled = false, bool captionsEnabled = false,
        bool fileWriteEnabled = false, bool memoryEnabled = false)
    {
        var tools = new List<ToolDefinition>();
        if (supportsWebSearch)
        {
            tools.Add(WebSearch);
            tools.Add(WebScrape);
        }
        if (captionsEnabled)
            tools.Add(YouTubeCaptions);
        if (browseEnabled)
        {
            tools.Add(BrowseNavigate);
            tools.Add(BrowseSnapshot);
            tools.Add(BrowseFind);
            tools.Add(BrowseClick);
            tools.Add(BrowseType);
            tools.Add(BrowsePressKey);
            tools.Add(BrowseTabs);
            tools.Add(BrowseWaitFor);
            tools.Add(BrowseBack);
            tools.Add(BrowseClose);
            tools.Add(BrowseEvaluate);
            tools.Add(BrowseNetwork);
            tools.Add(BrowseNetworkRequest);
        }
        if (fileReadEnabled)
        {
            tools.Add(FileRead);
            tools.Add(FileList);
        }
        if (fileWriteEnabled)
            tools.Add(FileWrite);
        if (memoryEnabled)
            tools.Add(MemoryWrite);
        if (ttsEnabled)
            tools.Add(TtsSummary);
        return tools;
    }
}
