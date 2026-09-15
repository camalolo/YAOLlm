using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace YAOLlm;

/// <summary>
/// Browser automation bridge for the browse_* tools. Wraps a lazily-spawned
/// <c>@playwright/mcp</c> child process (MCP over newline-delimited JSON-RPC
/// on stdio) — the same server model opencode uses for its browser tools.
/// The instance is app-scoped (created in Program.Main, shared across preset
/// switches like ScrapeCache) so the browser session survives provider swaps.
/// </summary>
public interface IBrowserService : IDisposable
{
    /// <summary>False when the config disabled the browser tools or the MCP server was not found.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Invokes a browse_* tool by forwarding it to the MCP server's matching
    /// browser_* tool. Returns the tool's text content, or an "Error: ..."
    /// string for expected failures (per the codebase's tool-result convention).
    /// </summary>
    Task<string> InvokeAsync(string browseToolName, string argumentsJson, CancellationToken cancellationToken = default);
}

public class PlaywrightBrowserService : IBrowserService
{
    /// <summary>Per-call timeout — page loads on slow sites are the long pole.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>browse_* → MCP tool name. Anything not listed is refused.</summary>
    private static readonly Dictionary<string, string> ToolMap = new(StringComparer.Ordinal)
    {
        ["browse_navigate"] = "browser_navigate",
        ["browse_snapshot"] = "browser_snapshot",
        ["browse_find"] = "browser_find",
        ["browse_click"] = "browser_click",
        ["browse_type"] = "browser_type",
        ["browse_press_key"] = "browser_press_key",
        ["browse_tabs"] = "browser_tabs",
        ["browse_wait_for"] = "browser_wait_for",
        ["browse_back"] = "browser_navigate_back",
        ["browse_close"] = "browser_close",
        ["browse_evaluate"] = "browser_evaluate",
        ["browse_network"] = "browser_network_requests",
        ["browse_network_request"] = "browser_network_request",
    };

    private readonly Logger _logger;
    private readonly string _cliPath;
    private readonly bool _headless;
    private readonly string? _channel;
    private readonly string? _cdpEndpoint;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private int _nextId;
    private bool _disposed;

    public bool IsEnabled { get; }

    public PlaywrightBrowserService(Logger? logger = null, bool headless = true,
        string? channel = null, string? cdpEndpoint = null, string? cliPath = null)
    {
        _logger = logger ?? new Logger();
        _headless = headless;
        _channel = channel;
        _cdpEndpoint = cdpEndpoint;
        _cliPath = cliPath ?? DefaultCliPath();

        IsEnabled = File.Exists(_cliPath);
        if (IsEnabled)
        {
            _logger.Log($"[Startup] browse_* tools enabled via {_cliPath} (headless={headless}{(channel != null ? $", channel={channel}" : "")})");
        }
        else
        {
            _logger.Log("[Startup] @playwright/mcp not found, browse_* tools disabled " +
                        $"(looked for {_cliPath}; override with BROWSER_MCP_CLI)");
        }
    }

    /// <summary>Default install location of the global npm package on Windows.</summary>
    internal static string DefaultCliPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "@playwright", "mcp", "cli.js");

    /// <summary>
    /// Maps a browse_* tool name to its MCP counterpart; null when unknown.
    /// </summary>
    internal static string? MapToolName(string browseName) =>
        ToolMap.TryGetValue(browseName, out var mcp) ? mcp : null;

    /// <summary>Serializes a JSON-RPC request as one newline-terminated line.</summary>
    internal static string BuildRequest(int id, string method, object? parameters) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        });

    public async Task<string> InvokeAsync(string browseToolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return "Error: Browser tools are disabled (set BROWSER_SERVICES=playwright in ~/.yaollm.conf).";

        var mcpName = MapToolName(browseToolName);
        if (mcpName == null)
            return $"Error: Unknown browser tool: {browseToolName}";

        try
        {
            await EnsureStartedAsync(cancellationToken);

            var arguments = string.IsNullOrWhiteSpace(argumentsJson)
                ? new Dictionary<string, object?>()
                : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson);

            var response = await RequestAsync("tools/call", new Dictionary<string, object?>
            {
                ["name"] = mcpName,
                ["arguments"] = arguments ?? new Dictionary<string, object?>(),
            }, cancellationToken);

            return FormatToolResponse(response);
        }
        catch (OperationCanceledException)
        {
            throw; // propagate to the provider's tool loop (Stop button)
        }
        catch (Exception ex)
        {
            _logger.Log($"[browser] {browseToolName} failed: {ex.Message}");
            return $"Error: {browseToolName} failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Flattens an MCP CallToolResult into plain text: the text content parts
    /// joined; isError results keep their text (the MCP server's error text is
    /// already model-friendly) so the model can adapt.
    /// </summary>
    internal static string FormatToolResponse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
            return "Error: Unexpected browser tool response.";

        if (response.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            return $"Error: Browser tool failed: {message ?? "unknown error"}";
        }

        if (!response.TryGetProperty("result", out var result))
            return "Error: Browser tool returned no result.";

        var sb = new System.Text.StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) &&
                    type.GetString() == "text" &&
                    part.TryGetProperty("text", out var text))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text.GetString());
                }
            }
        }

        var output = sb.ToString();
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            output = "Error: " + output;

        return output.Length > 0 ? output : "(no content returned)";
    }

    // ─── Process lifecycle + JSON-RPC plumbing ────────────────────────

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process != null)
            return;

        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_process != null)
                return;

            var argumentList = new List<string> { _cliPath };
            if (_headless) argumentList.Add("--headless");
            if (!string.IsNullOrEmpty(_channel)) { argumentList.Add("--browser"); argumentList.Add(_channel); }
            if (!string.IsNullOrEmpty(_cdpEndpoint)) { argumentList.Add("--cdp-endpoint"); argumentList.Add(_cdpEndpoint); }

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in argumentList)
                psi.ArgumentList.Add(arg);

            _logger.Log($"[browser] spawning MCP server: node {string.Join(" ", argumentList)}");
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start node process for @playwright/mcp.");
            _process = process;
            _ = ReadStdoutLoopAsync(process);
            _ = DrainStderrLoopAsync(process);

            // Initialize handshake — the MCP server won't serve tools/call before it.
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCts.CancelAfter(StartupTimeout);
            await RequestAsync("initialize", new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new Dictionary<string, object?>(),
                ["clientInfo"] = new { name = "yaollm", version = "1.0" },
            }, startupCts.Token);
            await WriteLineAsync(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            }));
            _logger.Log("[browser] MCP server initialized");
        }
        catch
        {
            KillProcess();
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task ReadStdoutLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                HandleLine(line);
        }
        catch { /* process killed / pipe closed */ }
        // stdout closed = process exited
        FailAllPending("Browser process exited unexpectedly.");
    }

    private async Task DrainStderrLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line))
                    _logger.Log($"[browser:mcp] {line}");
        }
        catch { /* process killed / pipe closed */ }
    }

    private async Task WriteLineAsync(string json)
    {
        var process = _process ?? throw new InvalidOperationException("Browser process not running.");
        await _writeLock.WaitAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(json);
            await process.StandardInput.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void HandleLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind == JsonValueKind.Null)
                return; // notification (logging etc.) — ignored

            var id = idElement.GetInt32();
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(root.Clone());
        }
        catch (Exception ex)
        {
            _logger.Log($"[browser] bad MCP line: {ex.Message}");
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await WriteLineAsync(BuildRequest(id, method, parameters));

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(CallTimeout, cancellationToken));
            if (completed != tcs.Task)
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException($"MCP request '{method}' timed out after {CallTimeout.TotalSeconds}s.");
            }
            return await tcs.Task;
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private void FailAllPending(string message)
    {
        foreach (var kvp in _pending)
            if (_pending.TryRemove(kvp.Key, out var tcs))
                tcs.TrySetException(new InvalidOperationException(message));
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false } process)
                process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
        _process = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FailAllPending("Browser service disposed.");
        KillProcess();
        _lifecycleLock.Dispose();
        _writeLock.Dispose();
    }
}
