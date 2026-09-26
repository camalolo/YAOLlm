using Xunit;

namespace YAOLlm.Tests;

public class FileWriteServiceTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _filesRoot;
    private readonly string _memoryDir;
    private readonly FileWriteService _service;

    public FileWriteServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "YAOLlmTests", Guid.NewGuid().ToString("N"));
        _filesRoot = Path.Combine(_baseDir, "files");
        _memoryDir = Path.Combine(_baseDir, "memory");
        Directory.CreateDirectory(_filesRoot);
        Directory.CreateDirectory(_memoryDir);
        _service = new FileWriteService(filesRoot: _filesRoot, memoryDir: _memoryDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
    }

    // ─── file_write: containment ──────────────────────────────────────

    [Fact]
    public async Task WriteAsync_InsideRoot_Succeeds_AndCreatesSubdirs()
    {
        var path = Path.Combine(_filesRoot, "sub", "notes.md");

        var result = await _service.WriteAsync(path, "hello", append: false);

        Assert.StartsWith("Wrote 5 chars", result);
        Assert.Equal("hello", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAsync_OutsideRoot_IsRejected()
    {
        var outside = Path.Combine(_baseDir, "outside.txt");

        var result = await _service.WriteAsync(outside, "x", append: false);

        Assert.StartsWith("Error:", result);
        Assert.Contains("outside the writable area", result);
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task WriteAsync_TraversalEscape_IsRejected()
    {
        var sneaky = Path.Combine(_filesRoot, "..", "escape.txt");

        var result = await _service.WriteAsync(sneaky, "x", append: false);

        Assert.StartsWith("Error:", result);
        Assert.False(File.Exists(Path.Combine(_baseDir, "escape.txt")));
    }

    [Fact]
    public async Task WriteAsync_RootItself_IsRejected()
    {
        var result = await _service.WriteAsync(_filesRoot, "x", append: false);

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task WriteAsync_Append_CreatesThenExtends()
    {
        var path = Path.Combine(_filesRoot, "log.txt");

        await _service.WriteAsync(path, "one", append: true);
        var result = await _service.WriteAsync(path, "two", append: true);

        Assert.Contains("file now", result);
        Assert.Equal("onetwo", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAsync_WritesUtf8WithoutBom()
    {
        var path = Path.Combine(_filesRoot, "bom.txt");

        await _service.WriteAsync(path, "žluťoučký", append: false);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM must not be written");
        Assert.Equal("žluťoučký", await File.ReadAllTextAsync(path));
    }

    // ─── file_write: find/replace (token-saving snippet edits) ────────

    [Fact]
    public async Task WriteAsync_Find_ReplacesSingleOccurrence()
    {
        var path = Path.Combine(_filesRoot, "notes.md");
        await _service.WriteAsync(path, "sword: cave\nshield: temple\npotion: town", append: false);

        var result = await _service.WriteAsync(path, "shield: tower", append: false,
            find: "shield: temple");

        Assert.StartsWith("Replaced 1 occurrence", result);
        Assert.Equal("sword: cave\nshield: tower\npotion: town", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAsync_Find_ZeroOrMultipleMatches_AreRejected()
    {
        var path = Path.Combine(_filesRoot, "multi.md");
        await _service.WriteAsync(path, "alpha beta gamma alpha beta", append: false);

        var missing = await _service.WriteAsync(path, "X", append: false, find: "not present");
        Assert.StartsWith("Error: 'find' text not found", missing);

        var ambiguous = await _service.WriteAsync(path, "X", append: false, find: "alpha beta");
        Assert.StartsWith("Error: 'find' matched 2 places", ambiguous);

        // File untouched by both failed edits
        Assert.Equal("alpha beta gamma alpha beta", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAsync_Find_WithoutExistingFile_ReturnsError()
    {
        var path = Path.Combine(_filesRoot, "ghost.md");

        var result = await _service.WriteAsync(path, "X", append: false, find: "anything");

        Assert.StartsWith("Error: File not found", result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteAsync_Find_CombinedWithAppend_IsRejected()
    {
        var path = Path.Combine(_filesRoot, "combo.md");
        await _service.WriteAsync(path, "existing", append: false);

        var result = await _service.WriteAsync(path, "X", append: true, find: "existing");

        Assert.StartsWith("Error: use either find", result);
        Assert.Equal("existing", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAsync_Find_OutsideRoot_StillRejected()
    {
        var outside = Path.Combine(_baseDir, "outside.txt");
        await File.WriteAllTextAsync(outside, "target text");

        var result = await _service.WriteAsync(outside, "X", append: false, find: "target");

        Assert.StartsWith("Error:", result);
        Assert.Contains("outside the writable area", result);
        Assert.Equal("target text", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task WriteAsync_Find_EmptyContent_DeletesTheSnippet()
    {
        var path = Path.Combine(_filesRoot, "delete.md");
        await _service.WriteAsync(path, "keep this\nremove me\nkeep that", append: false);

        var result = await _service.WriteAsync(path, "", append: false, find: "remove me\n");

        Assert.StartsWith("Replaced 1 occurrence", result);
        Assert.Equal("keep this\nkeep that", await File.ReadAllTextAsync(path));
    }

    // ─── memory_write ─────────────────────────────────────────────────

    [Fact]
    public async Task WriteMemoryAsync_Append_CreatesAndSeparates()
    {
        var path = _service.CurrentMemoryPath;

        await _service.WriteMemoryAsync("line one", "append", null);
        var result = await _service.WriteMemoryAsync("line two", "append", null);

        Assert.StartsWith("Memory appended", result);
        Assert.Equal("line one\nline two", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteMemoryAsync_Replace_RequiresUniqueFind()
    {
        await _service.WriteMemoryAsync("sword is in the cave. shield is in the cave.", "append", null);

        var missing = await _service.WriteMemoryAsync("X", "replace", "not present anywhere");
        Assert.StartsWith("Error: 'find' text not found", missing);

        var ambiguous = await _service.WriteMemoryAsync("X", "replace", "in the cave");
        Assert.StartsWith("Error: 'find' matched 2 places", ambiguous);

        var ok = await _service.WriteMemoryAsync("shield is in the temple.", "replace", "shield is in the cave.");
        Assert.StartsWith("Memory edited", ok);
        Assert.Contains("sword is in the cave. shield is in the temple.", await File.ReadAllTextAsync(_service.CurrentMemoryPath));
    }

    [Fact]
    public async Task WriteMemoryAsync_Replace_WithoutFile_ReturnsCreateHint()
    {
        var result = await _service.WriteMemoryAsync("X", "replace", "Y");

        Assert.StartsWith("Error: The memory file does not exist", result);
    }

    [Fact]
    public async Task WriteMemoryAsync_Overwrite_RewritesWholeFile()
    {
        await _service.WriteMemoryAsync("old", "append", null);

        var result = await _service.WriteMemoryAsync("fresh start", "overwrite", null);

        Assert.StartsWith("Memory file rewritten", result);
        Assert.Equal("fresh start", await File.ReadAllTextAsync(_service.CurrentMemoryPath));
    }

    [Fact]
    public async Task WriteMemoryAsync_AlwaysTargetsTheFixedMemoryFile()
    {
        await _service.WriteMemoryAsync("notes", "append", null);

        Assert.True(File.Exists(Path.Combine(_memoryDir, "memory.md")));
        // no slug files are created alongside it
        Assert.Equal(1, Directory.GetFiles(_memoryDir, "*.md").Length);
    }

    [Fact]
    public async Task WriteMemoryAsync_Disabled_ReturnsError()
    {
        var disabled = new FileWriteService(filesRoot: _filesRoot, memoryDir: _memoryDir, memoryEnabled: false);

        var result = await disabled.WriteMemoryAsync("x", "append", null);

        Assert.StartsWith("Error: The memory feature is disabled", result);
    }

    // ─── Path resolution ──────────────────────────────────────────────

    [Fact]
    public void CurrentMemoryPath_IsFixedMemoryMdInsideMemoryDir()
    {
        Assert.Equal(Path.Combine(_memoryDir, "memory.md"), _service.CurrentMemoryPath);
    }

    // ─── Deletion (only ever the memory file) ─────────────────────────

    [Fact]
    public async Task DeleteCurrentMemoryFile_DeletesOnlyThatFile()
    {
        await _service.WriteMemoryAsync("remember this", "append", null);
        var scratch = Path.Combine(_filesRoot, "scratch.txt");
        await File.WriteAllTextAsync(scratch, "user file");

        var message = _service.DeleteCurrentMemoryFile();

        Assert.Contains("deleted", message);
        Assert.False(File.Exists(_service.CurrentMemoryPath));
        Assert.True(File.Exists(scratch)); // unrelated files are untouched

        // deleting again is a no-op
        Assert.Contains("does not exist", _service.DeleteCurrentMemoryFile());
    }

    // ─── Read-back via implicit roots ─────────────────────────────────

    [Fact]
    public async Task FileReadService_ImplicitRoots_AllowMemoryAndWriteArea_Only()
    {
        var allowlist = new FileAllowlist(); // empty — nothing user-approved
        var reader = new YAOLlm.FileReadService(allowlist: allowlist,
            implicitRoots: new[] { _filesRoot, _memoryDir });

        await _service.WriteMemoryAsync("stored knowledge", "append", null);
        var memoryFile = _service.CurrentMemoryPath;
        var scratch = Path.Combine(_filesRoot, "self-written.txt");
        await _service.WriteAsync(scratch, "model output", append: false);

        // memory file + self-written file are readable with an empty allowlist
        Assert.Equal("stored knowledge", await reader.ReadFileAsync(memoryFile));
        Assert.Equal("model output", await reader.ReadFileAsync(scratch));
        Assert.Contains("memory.md", await reader.ListFilesAsync(_memoryDir));

        // everything else is still denied
        Assert.StartsWith("Error: Access denied", await reader.ReadFileAsync(_baseDir + "\\secret.txt"));
    }
}
