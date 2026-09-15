using System.Text;

namespace YAOLlm;

public interface IFileReadService
{
    /// <summary>
    /// Reads a text file and returns its content, or an "Error: ..." string
    /// for expected failures (missing file, binary content, bad arguments).
    /// </summary>
    Task<string> ReadFileAsync(string path, int startLine = 0, int endLine = 0,
        int maxLength = 15000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the immediate contents (subdirectories and files) of a directory
    /// and returns them as text, or an "Error: ..." string for expected
    /// failures (missing directory, path outside the allowlist).
    /// </summary>
    Task<string> ListFilesAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only local file access for the file_read/list_files tools. Files are
/// opened with FileAccess.Read only — no modification is possible by
/// construction. Content is truncated at maxLength (with a marker) so a huge
/// log file can't blow up the context window.
///
/// When an allowlist is supplied, access is restricted to user-approved
/// files/directories (see FileAllowlist); a null allowlist means unrestricted
/// access (used by tests).
/// </summary>
public class FileReadService : IFileReadService
{
    /// <summary>Bytes inspected when sniffing for binary content (NUL byte heuristic).</summary>
    internal const int BinarySniffLength = 1024;

    /// <summary>Maximum entries returned by one list_files call (protects the context window).</summary>
    internal const int MaxListEntries = 500;

    private readonly Logger _logger;
    private readonly FileAllowlist? _allowlist;
    private readonly IReadOnlyList<string> _implicitRoots;

    /// <summary>
    /// allowlist: user-approved files/dirs (null = unrestricted, tests only).
    /// implicitRoots: directories that are readable without allowlisting —
    /// the memory dir and the write root, so the LLM can read back what the
    /// write tools produce even when the user's allowlist is empty.
    /// </summary>
    public FileReadService(Logger? logger = null, FileAllowlist? allowlist = null, IEnumerable<string>? implicitRoots = null)
    {
        _logger = logger ?? new Logger();
        _allowlist = allowlist;
        _implicitRoots = implicitRoots?
            .Select(FileWriteService.NormalizeRoot)
            .Where(r => r != null)
            .Cast<string>()
            .ToList() ?? new List<string>();
    }

    public async Task<string> ReadFileAsync(string path, int startLine = 0, int endLine = 0,
        int maxLength = 15000, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        if (resolved.Error != null)
            return resolved.Error;

        var fullPath = resolved.Path!;

        var denial = CheckReadAccess(fullPath);
        if (denial != null)
            return denial;

        if (Directory.Exists(fullPath) && !File.Exists(fullPath))
            return $"Error: {fullPath} is a directory, not a file — file_read reads file contents only.";

        if (!File.Exists(fullPath))
            return $"Error: File not found: {fullPath}";

        if (startLine < 0 || endLine < 0)
            return "Error: start_line/end_line must be >= 0";
        if (startLine > 0 && endLine > 0 && startLine > endLine)
            return $"Error: start_line ({startLine}) is after end_line ({endLine})";

        try
        {
            // Binary sniff — reading an .exe/.png as text yields useless token soup.
            if (await IsProbablyBinaryAsync(fullPath, cancellationToken))
                return $"Error: {fullPath} looks like a binary file — file_read only returns text content.";

            return await ReadTextAsync(fullPath, startLine, endLine, maxLength, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate to the provider's tool loop
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log($"FileReadService error for {fullPath}: {ex.Message}");
            return $"Error: Could not read {fullPath}: {ex.Message}";
        }
    }

    public Task<string> ListFilesAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        if (resolved.Error != null)
            return Task.FromResult(resolved.Error);

        var fullPath = resolved.Path!;

        if (_allowlist != null && !_allowlist.IsDirectoryAllowed(fullPath)
            && !_implicitRoots.Any(root => IsInsideOrEqual(fullPath, root)))
            return Task.FromResult(DenialMessage(fullPath));

        if (File.Exists(fullPath) && !Directory.Exists(fullPath))
            return Task.FromResult($"Error: {fullPath} is a file, not a directory — list_files lists directories; use file_read for file contents.");

        if (!Directory.Exists(fullPath))
            return Task.FromResult($"Error: Directory not found: {fullPath}");

        try
        {
            return Task.FromResult(FormatDirectoryListing(fullPath));
        }
        catch (Exception ex)
        {
            _logger.Log($"FileReadService list error for {fullPath}: {ex.Message}");
            return Task.FromResult($"Error: Could not list {fullPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Allowlist gate for file reads. Returns an "Error: ..." string when
    /// access must be denied, null when the path is approved (allowlist entry,
    /// an implicit root such as the memory dir / write root, or unrestricted).
    /// </summary>
    private string? CheckReadAccess(string fullPath)
    {
        if (_allowlist == null)
            return null;
        if (_allowlist.IsAllowed(fullPath))
            return null;
        return _implicitRoots.Any(root => IsInsideOrEqual(fullPath, root)) ? null : DenialMessage(fullPath);
    }

    /// <summary>
    /// True when fullPath is root itself or strictly inside it
    /// (separator-bounded, case-insensitive). Directory listing of the root
    /// must pass, unlike the write-side check which refuses the root.
    /// </summary>
    internal static bool IsInsideOrEqual(string fullPath, string root)
    {
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            return true;
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private string DenialMessage(string fullPath)
    {
        if (_implicitRoots.Count > 0)
            return $"Error: Access denied — {fullPath} is not within the user-approved paths or the writable area ({string.Join(", ", _implicitRoots)}).";
        return _allowlist!.Count == 0
            ? "Error: Access denied — the user has not approved any files or folders yet. Ask them to share one via the file picker."
            : $"Error: Access denied — {fullPath} is not within the user-approved paths. Only files under the paths listed as approved can be accessed.";
    }

    /// <summary>
    /// Normalizes model-supplied paths: strips wrapping quotes, expands ~ and
    /// environment variables, and requires an absolute path (a relative path
    /// would resolve against whatever directory the exe was started from).
    /// </summary>
    internal static (string? Path, string? Error) ResolvePath(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return (null, "Error: Missing path parameter");

        var path = input.Trim();
        if (path.Length >= 2 && (path[0] == '"' && path[^1] == '"' || path[0] == '\'' && path[^1] == '\''))
            path = path[1..^1].Trim();

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[1..].TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));

        path = Environment.ExpandEnvironmentVariables(path);

        if (!Path.IsPathRooted(path))
            return (null, $"Error: '{path}' is not an absolute path — the file tools need the full path (e.g. C:\\Users\\...\\file.txt)");

        // Collapse ".." segments NOW — prefix/allowlist checks compare string
        // prefixes, and an un-normalized "approved\..\..\secret" would sail
        // through them. This closes traversal on both the read and write sides.
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return (null, $"Error: Invalid path '{path}': {ex.Message}");
        }

        return (path, null);
    }

    /// <summary>
    /// One line per entry — directories with a trailing slash, files with a
    /// byte size — directories first, both alphabetically. Capped at
    /// MaxListEntries with a pointer toward subdirectories.
    /// </summary>
    private string FormatDirectoryListing(string fullPath)
    {
        var entries = Directory.EnumerateFileSystemEntries(fullPath)
            .Select(p => new
            {
                Name = Path.GetFileName(p),
                IsDir = Directory.Exists(p) && !File.Exists(p),
                Size = File.Exists(p) ? new FileInfo(p).Length : 0,
            })
            .OrderBy(e => e.IsDir ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count == 0)
            return "(empty directory)";

        var lines = entries.Take(MaxListEntries)
            .Select(e => e.IsDir ? e.Name + "/" : $"{e.Name} ({e.Size:N0} bytes)")
            .ToList();

        if (entries.Count > MaxListEntries)
            lines.Add($"[{entries.Count - MaxListEntries:N0} more entries not shown — list a subdirectory instead]");

        return string.Join("\n", lines);
    }

    private static async Task<bool> IsProbablyBinaryAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[BinarySniffLength];
        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    private static async Task<string> ReadTextAsync(string path, int startLine, int endLine, int maxLength, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var truncated = false;
        int lineNumber = 0;   // 1-based once incremented
        int linesRead = 0;    // lines emitted into sb

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (startLine > 0 && lineNumber < startLine)
                continue;
            if (endLine > 0 && lineNumber > endLine)
                break;

            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line);
            linesRead++;

            if (sb.Length >= maxLength)
            {
                truncated = true;
                break;
            }
        }

        if (linesRead == 0)
        {
            if (startLine > 0)
            {
                var rangeEnd = endLine > 0 ? endLine.ToString() : "end";
                return $"(no content in lines {startLine}-{rangeEnd} — file may be shorter)";
            }
            return "(empty file)";
        }

        if (truncated)
        {
            sb.Append($"\n\n[Truncated at {maxLength} characters after {linesRead} lines"
                + $"; file continues past line {lineNumber}"
                + ". Call file_read again with start_line to read further sections.]");
        }

        return sb.ToString();
    }
}
