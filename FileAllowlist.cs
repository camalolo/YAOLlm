using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YAOLlm;

/// <summary>
/// Thread-safe allowlist of user-approved files and directories. The LLM file
/// tools (<c>file_read</c>, <c>list_files</c>) may only touch paths covered by
/// this list: an entry that is a file grants access to exactly that file; an
/// entry that is a directory grants access to everything inside it
/// (recursively). Mutations come from the UI thread (file pickers, [x] clicks);
/// reads happen on provider threads mid-request — hence the lock.
/// </summary>
public class FileAllowlist
{
    private readonly object _lock = new();
    private readonly List<string> _entries = new();

    /// <summary>
    /// Raised after any successful user-driven mutation. Handlers must be
    /// null-tolerant about ordering (the bridge may not exist yet).
    /// </summary>
    public event Action? Changed;

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Snapshot of the current entries as full paths.</summary>
    public IReadOnlyList<string> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    /// <summary>
    /// Adds a path (file or directory). Duplicates are ignored
    /// (case-insensitive, Windows-style). Returns true when the list changed.
    /// </summary>
    public bool Add(string path)
    {
        var full = Normalize(path);
        if (full == null) return false;

        bool added;
        lock (_lock)
        {
            added = !_entries.Contains(full, StringComparer.OrdinalIgnoreCase);
            if (added)
                _entries.Add(full);
        }
        if (added)
            Changed?.Invoke();
        return added;
    }

    /// <summary>
    /// Removes an entry. Returns true when the list changed.
    /// </summary>
    public bool Remove(string path)
    {
        var full = Normalize(path);
        if (full == null) return false;

        bool removed;
        lock (_lock)
        {
            removed = _entries.RemoveAll(e => string.Equals(e, full, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Bulk-fills the list without firing <see cref="Changed"/> — used at
    /// startup to restore the persisted allowlist before any handler (UI
    /// persistence, bridge push) is attached.
    /// </summary>
    public void Restore(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            _entries.Clear();
            foreach (var path in paths)
            {
                var full = Normalize(path);
                if (full != null && !_entries.Contains(full, StringComparer.OrdinalIgnoreCase))
                    _entries.Add(full);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is an allowlisted entry itself or
    /// lives inside an allowlisted directory. Used by <c>file_read</c>.
    /// </summary>
    public bool IsAllowed(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        lock (_lock)
        {
            return _entries.Any(entry => Covers(entry, fullPath));
        }
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is an allowlisted entry itself
    /// or lies inside an allowlisted directory. Pure path logic — the caller
    /// (FileReadService) enforces that the target actually is a directory,
    /// so an exactly-matching file entry ends as a polite "is a file" error
    /// rather than an access denial. Used by <c>list_files</c>.
    /// </summary>
    public bool IsDirectoryAllowed(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        lock (_lock)
        {
            return _entries.Any(entry =>
                string.Equals(entry, fullPath, StringComparison.OrdinalIgnoreCase) ||
                Covers(entry, fullPath));
        }
    }

    /// <summary>
    /// entry covers path when they are equal, or when entry is a path prefix
    /// followed by a separator (so allowlisting E:\data never leaks
    /// E:\database — the separator guard makes the boundary explicit).
    /// </summary>
    private static bool Covers(string entry, string path)
    {
        if (string.Equals(entry, path, StringComparison.OrdinalIgnoreCase))
            return true;
        return path.StartsWith(entry + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(entry + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trims quotes/whitespace and resolves to a full path; trailing
    /// separators are removed (so <c>C:\dir\</c> and <c>C:\dir</c> dedupe)
    /// except for root paths like <c>C:\</c>. Null on garbage.</summary>
    internal static string? NormalizeRoot(string root)
    {
        return Normalize(root);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0 || trimmed.EndsWith(':'))
                return full; // root form ("C:\") — keep as-is
            return trimmed;
        }
        catch
        {
            return null;
        }
    }
}
