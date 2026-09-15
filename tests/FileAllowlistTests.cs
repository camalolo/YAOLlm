using Xunit;

namespace YAOLlm.Tests;

public class FileAllowlistTests : IDisposable
{
    private readonly string _dir;

    public FileAllowlistTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "YAOLlmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string SubDir(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Add_NormalizesToFullPath_AndDeduplicates()
    {
        var allowlist = new FileAllowlist();
        var sub = SubDir("a");

        Assert.True(allowlist.Add(sub));
        // Same path with different case and a trailing separator is a duplicate
        Assert.False(allowlist.Add(sub.ToUpperInvariant() + Path.DirectorySeparatorChar));

        Assert.Equal(1, allowlist.Count);
        Assert.Equal(sub, allowlist.Entries[0], ignoreCase: true);
    }

    [Fact]
    public void Add_QuotedPath_IsUnwrapped()
    {
        var allowlist = new FileAllowlist();

        Assert.True(allowlist.Add($"\"{_dir}\""));

        Assert.Equal(_dir, allowlist.Entries[0], ignoreCase: true);
    }

    [Fact]
    public void Add_InvalidPath_ReturnsFalse()
    {
        var allowlist = new FileAllowlist();

        Assert.False(allowlist.Add(""));
        Assert.False(allowlist.Add("   "));
        Assert.False(allowlist.Add("bad\0path")); // NUL byte — never a valid path
    }

    [Fact]
    public void Remove_RemovesEntry_AndFiresChangedOnce()
    {
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { _dir });
        var changes = 0;
        allowlist.Changed += () => changes++;

        Assert.True(allowlist.Remove(_dir.ToUpperInvariant()));
        Assert.False(allowlist.Remove(_dir));

        Assert.Equal(0, allowlist.Count);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Add_FiresChangedPerMutation()
    {
        var allowlist = new FileAllowlist();
        var changes = 0;
        allowlist.Changed += () => changes++;

        allowlist.Add(Path.Combine(_dir, "one"));
        allowlist.Add(Path.Combine(_dir, "two"));
        allowlist.Add(Path.Combine(_dir, "two")); // duplicate — no change

        Assert.Equal(2, changes);
    }

    [Fact]
    public void IsAllowed_ExactFileEntry_GrantsThatFileOnly()
    {
        var file = Path.Combine(_dir, "approved.txt");
        File.WriteAllText(file, "x");
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { file });

        Assert.True(allowlist.IsAllowed(file));
        Assert.True(allowlist.IsAllowed(file.ToUpperInvariant()));
        Assert.False(allowlist.IsAllowed(Path.Combine(_dir, "other.txt")));
    }

    [Fact]
    public void IsAllowed_DirectoryEntry_GrantsDescendants()
    {
        var sub = SubDir("proj");
        var deep = Path.Combine(sub, "src", "main.cs");
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { sub });

        Assert.True(allowlist.IsAllowed(sub));
        Assert.True(allowlist.IsAllowed(deep));
    }

    [Fact]
    public void IsAllowed_PrefixBoundaryIsSeparatorNotSubstring()
    {
        // Allowlisting E:\...\data must not leak E:\...\database
        SubDir("data");
        var databaseDir = SubDir("database");
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { Path.Combine(_dir, "data") });

        Assert.True(allowlist.IsAllowed(Path.Combine(_dir, "data", "file.txt")));
        Assert.False(allowlist.IsAllowed(Path.Combine(databaseDir, "secret.txt")));
    }

    [Fact]
    public void IsAllowed_OutsideEntries_IsDenied()
    {
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { Path.Combine(_dir, "a") });

        Assert.False(allowlist.IsAllowed(Path.Combine(_dir, "b", "file.txt")));
    }

    [Fact]
    public void IsAllowed_EmptyList_DeniesEverything()
    {
        var allowlist = new FileAllowlist();

        Assert.False(allowlist.IsAllowed(_dir));
    }

    [Fact]
    public void IsDirectoryAllowed_DirEntryAndDescendants_Yes_Siblings_No()
    {
        var approved = SubDir("approved");
        var sibling = SubDir("sibling");
        var approvedFile = Path.Combine(approved, "readme.txt");
        File.WriteAllText(approvedFile, "x");

        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { approved, approvedFile });

        Assert.True(allowlist.IsDirectoryAllowed(approved));
        // Descendants don't need to exist yet — pure path logic; the service
        // layer rejects non-directories with a clear "is a file" error.
        Assert.True(allowlist.IsDirectoryAllowed(Path.Combine(approved, "not-created-yet")));
        Assert.True(allowlist.IsDirectoryAllowed(approvedFile));
        // A sibling directory is not covered by either entry
        Assert.False(allowlist.IsDirectoryAllowed(sibling));
        Assert.False(allowlist.IsDirectoryAllowed(_dir));
    }

    [Fact]
    public void Restore_SilentBulkFill_NoChangedEvents()
    {
        var allowlist = new FileAllowlist();
        var changes = 0;
        allowlist.Changed += () => changes++;

        allowlist.Restore(new[] { _dir, _dir }); // duplicate ignored

        Assert.Equal(1, allowlist.Count);
        Assert.Equal(0, changes);
    }
}
