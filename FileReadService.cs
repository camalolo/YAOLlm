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
}

/// <summary>
/// Read-only local file access for the file_read tool. Files are opened with
/// FileAccess.Read only — no modification is possible by construction.
/// Content is truncated at maxLength (with a marker) so a huge log file can't
/// blow up the context window.
/// </summary>
public class FileReadService : IFileReadService
{
    /// <summary>Bytes inspected when sniffing for binary content (NUL byte heuristic).</summary>
    internal const int BinarySniffLength = 1024;

    private readonly Logger _logger;

    public FileReadService(Logger? logger = null)
    {
        _logger = logger ?? new Logger();
    }

    public async Task<string> ReadFileAsync(string path, int startLine = 0, int endLine = 0,
        int maxLength = 15000, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        if (resolved.Error != null)
            return resolved.Error;

        var fullPath = resolved.Path!;

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
            return (null, $"Error: '{path}' is not an absolute path — file_read needs the full path (e.g. C:\\Users\\...\\file.txt)");

        return (path, null);
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
