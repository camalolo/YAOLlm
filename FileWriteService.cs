using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace YAOLlm;

/// <summary>
/// Write access for the file_write / memory_write tools. Writes are restricted
/// to the YAOLlm-owned subtree under the system temp dir (%TEMP%\YAOLlm\):
/// a general scratch area (files\) and the memory file (memory\memory.md).
/// Nothing outside that subtree can ever be created or modified, and this
/// service is the only thing that may delete the memory file — user-added
/// allowlist entries are never touched by deletion.
/// </summary>
public interface IFileWriteService
{
    bool IsEnabled { get; }

    /// <summary>Whether the memory file feature is on.</summary>
    bool MemoryEnabled { get; }

    /// <summary>Directories file_read/list_files may touch without allowlisting (write root + memory dir).</summary>
    IReadOnlyList<string> ImplicitReadRoots { get; }

    /// <summary>The memory file (prompt + UI).</summary>
    string CurrentMemoryPath { get; }

    /// <summary>file_write — writes inside the write root only. With
    /// <paramref name="find"/>, replaces one unique exact snippet instead of
    /// rewriting the whole file (token-saving edits).</summary>
    Task<string> WriteAsync(string path, string content, bool append, string? find = null,
        CancellationToken cancellationToken = default);

    /// <summary>memory_write — append / replace (unique find) / overwrite on the memory file.</summary>
    Task<string> WriteMemoryAsync(string content, string op, string? find, CancellationToken cancellationToken = default);

    /// <summary>The writable root (file_write constraint, also shown in the system prompt).</summary>
    string WriteRoot { get; }

    /// <summary>Deletes the memory file (never anything else). Returns a user-facing message.</summary>
    string DeleteCurrentMemoryFile();

    /// <summary>Raised after any successful write or delete (possibly off the UI thread).</summary>
    event Action? Changed;
}

public class FileWriteService : IFileWriteService
{
    /// <summary>Per-write content cap — a model response can't exceed this anyway.</summary>
    internal const int MaxWriteChars = 200_000;
    /// <summary>memory_write starts nudging compaction past this size.</summary>
    internal const int MemoryWarnChars = 50_000;
    /// <summary>The single, fixed memory file name.</summary>
    internal const string MemoryFileName = "memory.md";

    private readonly Logger _logger;
    private readonly object _ioLock = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool IsEnabled { get; }
    public bool MemoryEnabled { get; }
    public string WriteRoot { get; }
    public string MemoryDir { get; }

    public event Action? Changed;

    public FileWriteService(Logger? logger = null, bool memoryEnabled = true,
        string? filesRoot = null, string? memoryDir = null)
    {
        _logger = logger ?? new Logger();
        MemoryEnabled = memoryEnabled;

        var yaollmTemp = NormalizeRoot(Path.Combine(Path.GetTempPath(), "YAOLlm"));
        WriteRoot = filesRoot != null ? NormalizeRoot(filesRoot) : NormalizeRoot(Path.Combine(yaollmTemp, "files"));
        MemoryDir = memoryDir != null ? NormalizeRoot(memoryDir) : NormalizeRoot(Path.Combine(yaollmTemp, "memory"));

        IsEnabled = true;
        Directory.CreateDirectory(WriteRoot);
        if (MemoryEnabled)
            Directory.CreateDirectory(MemoryDir);

        _logger.Log($"[Startup] file_write enabled (root: {WriteRoot}"
            + (MemoryEnabled ? $", memory: {MemoryDir}" : ", memory disabled") + ")");
    }

    public IReadOnlyList<string> ImplicitReadRoots
    {
        get
        {
            var roots = new List<string> { WriteRoot };
            if (MemoryEnabled)
                roots.Add(MemoryDir);
            return roots;
        }
    }

    // ─── Memory path ─────────────────────────────────────────────────

    /// <summary>The memory file: a single fixed path, independent of the active app.</summary>
    public string CurrentMemoryPath => Path.Combine(MemoryDir, MemoryFileName);

    // ─── file_write ───────────────────────────────────────────────────

    public async Task<string> WriteAsync(string path, string content, bool append, string? find = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = FileReadService.ResolvePath(path);
        if (resolved.Error != null)
            return resolved.Error;
        var fullPath = resolved.Path!;

        // The root itself is a directory, not a writable file.
        if (!FileWriteService.IsUnderRoot(fullPath, WriteRoot))
            return $"Error: '{fullPath}' is outside the writable area — file_write may only create files under {WriteRoot}";

        if (content.Length > MaxWriteChars)
            return $"Error: content too large ({content.Length:N0} chars, max {MaxWriteChars:N0}).";

        if (!string.IsNullOrEmpty(find) && append)
            return "Error: use either find (replace a snippet) or append, not both.";

        var linkError = CheckReparsePoint(fullPath);
        if (linkError != null)
            return linkError;

        var isReplace = !string.IsNullOrEmpty(find);
        try
        {
            if (isReplace)
            {
                // Snippet edit: swap one unique exact occurrence of 'find' for
                // content — the model edits a large file without resending it.
                if (!File.Exists(fullPath))
                    return $"Error: File not found: {fullPath} — find/replace edits an existing file (use a plain write to create one).";

                string existingText;
                lock (_ioLock)
                {
                    existingText = File.ReadAllText(fullPath);
                }

                var count = CountOccurrences(existingText, find!);
                if (count == 0)
                    return "Error: 'find' text not found — read the file first (file_read) and copy the exact text (whitespace matters).";
                if (count > 1)
                    return $"Error: 'find' matched {count} places — include more surrounding text so the match is unique.";

                content = existingText.Replace(find!, content, StringComparison.Ordinal);
            }

            lock (_ioLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            }

            var encoding = Utf8NoBom;
            if (append)
            {
                await File.AppendAllTextAsync(fullPath, content, encoding, cancellationToken);
                var total = new FileInfo(fullPath).Length;
                Changed?.Invoke();
                return $"Appended {content.Length:N0} chars to {fullPath} (file now {total:N0} bytes)";
            }

            await File.WriteAllTextAsync(fullPath, content, encoding, cancellationToken);
            Changed?.Invoke();
            return isReplace
                ? $"Replaced 1 occurrence in {fullPath} (file now {new FileInfo(fullPath).Length:N0} bytes)"
                : $"Wrote {content.Length:N0} chars to {fullPath}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Log($"[file_write] {fullPath}: {ex.Message}");
            return $"Error: Could not write {fullPath}: {ex.Message}";
        }
    }

    // ─── memory_write ─────────────────────────────────────────────────

    public async Task<string> WriteMemoryAsync(string content, string op, string? find, CancellationToken cancellationToken = default)
    {
        if (!MemoryEnabled)
            return "Error: The memory feature is disabled.";

        if (content.Length > MaxWriteChars)
            return $"Error: content too large ({content.Length:N0} chars, max {MaxWriteChars:N0}).";

        // One fixed memory file — resolved here so the model can never write
        // memories anywhere else.
        var path = CurrentMemoryPath;
        var operation = (op ?? "append").Trim().ToLowerInvariant();

        try
        {
            lock (_ioLock)
            {
                Directory.CreateDirectory(MemoryDir);
            }

            switch (operation)
            {
                case "append":
                {
                    if (content.Length == 0)
                        return "Error: Nothing to append (content is empty).";
                    var prefix = "";
                    if (File.Exists(path))
                    {
                        var existing = await File.ReadAllTextAsync(path, cancellationToken);
                        if (existing.Length > 0 && !existing.EndsWith('\n'))
                            prefix = "\n";
                    }
                    await File.AppendAllTextAsync(path, prefix + content, Utf8NoBom, cancellationToken);
                    break;
                }

                case "replace":
                {
                    if (string.IsNullOrEmpty(find))
                        return "Error: op=replace needs the exact text to replace in the 'find' parameter.";
                    if (!File.Exists(path))
                        return "Error: The memory file does not exist yet — use op=append to create it first.";

                    var existingText = await File.ReadAllTextAsync(path, cancellationToken);
                    var count = CountOccurrences(existingText, find);
                    if (count == 0)
                        return "Error: 'find' text not found in the memory file — read it first (file_read) and copy the exact text (whitespace matters).";
                    if (count > 1)
                        return $"Error: 'find' matched {count} places — include more surrounding text so the match is unique.";

                    await File.WriteAllTextAsync(path, existingText.Replace(find, content, StringComparison.Ordinal), Utf8NoBom, cancellationToken);
                    break;
                }

                case "overwrite":
                    await File.WriteAllTextAsync(path, content, Utf8NoBom, cancellationToken);
                    break;

                default:
                    return $"Error: Unknown op '{op}' — use append, replace, or overwrite.";
            }

            Changed?.Invoke();
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            var result = operation switch
            {
                "append" => $"Memory appended to {path} (file now {size:N0} bytes)",
                "replace" => $"Memory edited: replaced 1 occurrence in {path} (file now {size:N0} bytes)",
                _ => $"Memory file rewritten: {path} ({size:N0} bytes)",
            };
            if (size > MemoryWarnChars)
                result += $"\n[Note: the memory file is getting large ({size:N0} bytes) — consider reorganizing it with op=overwrite, keeping only what is still useful.]";
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Log($"[memory_write] {path}: {ex.Message}");
            return $"Error: Could not update the memory file: {ex.Message}";
        }
    }

    // ─── Deletion ─────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the memory file. The path is resolved internally and must live
    /// inside the memory dir — a user-added allowlist entry can never reach
    /// this code path, so deletion can never apply to it.
    /// </summary>
    public string DeleteCurrentMemoryFile()
    {
        var path = CurrentMemoryPath;
        if (!IsUnderRoot(path, MemoryDir))
            return $"Error: Refusing to delete {path} — not a memory file.";

        try
        {
            if (!File.Exists(path))
                return $"🧠 Memory file does not exist ({path})";

            File.Delete(path);
            Changed?.Invoke();
            _logger.Log($"[memory] deleted {path}");
            return $"🧠 Memory file deleted: {path}";
        }
        catch (Exception ex)
        {
            _logger.Log($"[memory] delete failed: {ex.Message}");
            return $"Error: Could not delete the memory file: {ex.Message}";
        }
    }

    // ─── Path helpers ─────────────────────────────────────────────────

    /// <summary>True when fullPath is strictly inside root (separator-bounded, case-insensitive).</summary>
    internal static bool IsUnderRoot(string fullPath, string root)
    {
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            return false; // the root itself is a directory, not a writable file
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeRoot(string root) =>
        FileAllowlist.NormalizeRoot(Path.GetFullPath(root)) ?? root;

    /// <summary>Refuses to write through symlink/junction targets that leave the temp tree.</summary>
    private static string? CheckReparsePoint(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath) && File.ResolveLinkTarget(fullPath, returnFinalTarget: true) != null)
                return $"Error: {fullPath} is a link — refusing to write through it.";
        }
        catch
        {
            // resolution unsupported/failed — proceed with the normal write
        }
        return null;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
