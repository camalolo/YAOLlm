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

    // ─── memory_write ─────────────────────────────────────────────────

    [Fact]
    public async Task WriteMemoryAsync_Append_CreatesAndSeparates()
    {
        var path = _service.CurrentMemoryPath;

        await _service.WriteMemoryAsync("line one", "append", null, null);
        var result = await _service.WriteMemoryAsync("line two", "append", null, null);

        Assert.StartsWith("Memory appended", result);
        Assert.Equal("line one\nline two", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteMemoryAsync_Replace_RequiresUniqueFind()
    {
        await _service.WriteMemoryAsync("sword is in the cave. shield is in the cave.", "append", null, null);

        var missing = await _service.WriteMemoryAsync("X", "replace", "not present anywhere", null);
        Assert.StartsWith("Error: 'find' text not found", missing);

        var ambiguous = await _service.WriteMemoryAsync("X", "replace", "in the cave", null);
        Assert.StartsWith("Error: 'find' matched 2 places", ambiguous);

        var ok = await _service.WriteMemoryAsync("shield is in the temple.", "replace", "shield is in the cave.", null);
        Assert.StartsWith("Memory edited", ok);
        Assert.Contains("sword is in the cave. shield is in the temple.", await File.ReadAllTextAsync(_service.CurrentMemoryPath));
    }

    [Fact]
    public async Task WriteMemoryAsync_Replace_WithoutFile_ReturnsCreateHint()
    {
        var result = await _service.WriteMemoryAsync("X", "replace", "Y", null);

        Assert.StartsWith("Error: The memory file does not exist", result);
    }

    [Fact]
    public async Task WriteMemoryAsync_Overwrite_RewritesWholeFile()
    {
        await _service.WriteMemoryAsync("old", "append", null, null);

        var result = await _service.WriteMemoryAsync("fresh start", "overwrite", null, null);

        Assert.StartsWith("Memory file rewritten", result);
        Assert.Equal("fresh start", await File.ReadAllTextAsync(_service.CurrentMemoryPath));
    }

    [Fact]
    public async Task WriteMemoryAsync_GameParam_TargetsThatGamesFile()
    {
        await _service.WriteMemoryAsync("witcher notes", "append", null, "The Witcher 3");

        var path = Path.Combine(_memoryDir, "the-witcher-3.md");
        Assert.True(File.Exists(path));
        Assert.Equal("witcher notes", await File.ReadAllTextAsync(path));
        // the current game's file is untouched
        Assert.False(File.Exists(_service.CurrentMemoryPath));
    }

    [Fact]
    public async Task WriteMemoryAsync_Disabled_ReturnsError()
    {
        var disabled = new FileWriteService(filesRoot: _filesRoot, memoryDir: _memoryDir, memoryEnabled: false);

        var result = await disabled.WriteMemoryAsync("x", "append", null, null);

        Assert.StartsWith("Error: The memory feature is disabled", result);
    }

    // ─── Slug / path resolution ───────────────────────────────────────

    [Fact]
    public void Slugify_NormalizesGameNames()
    {
        Assert.Equal("dying-light-the-beast", FileWriteService.Slugify("Dying Light: The Beast"));
        Assert.Equal("general", FileWriteService.Slugify(""));
        Assert.Equal("general", FileWriteService.Slugify("!!!"));
        Assert.Equal("cyberpunk-2077", FileWriteService.Slugify("Cyberpunk 2077®"));
    }

    [Fact]
    public void Slugify_CapsLength()
    {
        var slug = FileWriteService.Slugify(new string('a', 200));

        Assert.True(slug.Length <= 60);
        Assert.False(slug.EndsWith('-'), $"slug should not end with '-': {slug}");
    }

    [Fact]
    public void MemoryPaths_AlwaysInsideMemoryDir()
    {
        _service.SetCurrentGame("Some Game: With Weird // Chars");

        Assert.StartsWith(_memoryDir, _service.CurrentMemoryPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".md", _service.CurrentMemoryPath);
    }

    // ─── Deletion (only ever the memory file) ─────────────────────────

    [Fact]
    public async Task DeleteCurrentMemoryFile_DeletesOnlyThatFile()
    {
        await _service.WriteMemoryAsync("remember this", "append", null, null);
        var scratch = Path.Combine(_filesRoot, "scratch.txt");
        await File.WriteAllTextAsync(scratch, "user file");

        var message = _service.DeleteCurrentMemoryFile();

        Assert.Contains("deleted", message);
        Assert.False(File.Exists(_service.CurrentMemoryPath));
        Assert.True(File.Exists(scratch)); // unrelated files are untouched

        // deleting again is a no-op
        Assert.Contains("does not exist", _service.DeleteCurrentMemoryFile());
    }

    [Fact]
    public void DeleteCurrentMemoryFile_PathAlwaysInsideMemoryDir()
    {
        _service.SetCurrentGame("../../evil");

        var message = _service.DeleteCurrentMemoryFile();

        // slug normalization can't escape the memory dir
        Assert.StartsWith(_memoryDir, _service.CurrentMemoryPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("🧠 Memory file does not exist", message);
    }

    // ─── Listing ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListMemoryFiles_ReturnsNames_Sorted()
    {
        await _service.WriteMemoryAsync("b", "append", null, "Beta Game");
        await _service.WriteMemoryAsync("a", "append", null, "Alpha Game");

        Assert.Equal(new[] { "alpha-game.md", "beta-game.md" }, _service.ListMemoryFiles());
    }

    // ─── Read-back via implicit roots ─────────────────────────────────

    [Fact]
    public async Task FileReadService_ImplicitRoots_AllowMemoryAndWriteArea_Only()
    {
        var allowlist = new FileAllowlist(); // empty — nothing user-approved
        var reader = new YAOLlm.FileReadService(allowlist: allowlist,
            implicitRoots: new[] { _filesRoot, _memoryDir });

        await _service.WriteMemoryAsync("stored knowledge", "append", null, null);
        var memoryFile = _service.CurrentMemoryPath;
        var scratch = Path.Combine(_filesRoot, "self-written.txt");
        await _service.WriteAsync(scratch, "model output", append: false);

        // memory file + self-written file are readable with an empty allowlist
        Assert.Equal("stored knowledge", await reader.ReadFileAsync(memoryFile));
        Assert.Equal("model output", await reader.ReadFileAsync(scratch));
        Assert.Contains("general.md", await reader.ListFilesAsync(_memoryDir));

        // everything else is still denied
        Assert.StartsWith("Error: Access denied", await reader.ReadFileAsync(_baseDir + "\\secret.txt"));
    }
}
