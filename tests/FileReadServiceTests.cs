using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace YAOLlm.Tests;

public class FileReadServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly FileReadService _service = new();

    public FileReadServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "YAOLlmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ReadFileAsync_ReadsTextContent()
    {
        var path = WriteFile("notes.txt", "line one\nline two\nline three");

        var result = await _service.ReadFileAsync(path);

        Assert.Equal("line one\nline two\nline three", result);
    }

    [Fact]
    public async Task ReadFileAsync_MissingFile_ReturnsError()
    {
        var result = await _service.ReadFileAsync(Path.Combine(_dir, "nope.txt"));

        Assert.StartsWith("Error: File not found", result);
    }

    [Fact]
    public async Task ReadFileAsync_Directory_ReturnsError()
    {
        var result = await _service.ReadFileAsync(_dir);

        Assert.StartsWith("Error:", result);
        Assert.Contains("is a directory", result);
    }

    [Fact]
    public async Task ReadFileAsync_RelativePath_ReturnsError()
    {
        var result = await _service.ReadFileAsync("some/relative/file.txt");

        Assert.StartsWith("Error:", result);
        Assert.Contains("absolute path", result);
    }

    [Fact]
    public async Task ReadFileAsync_MissingPath_ReturnsError()
    {
        Assert.StartsWith("Error: Missing path", await _service.ReadFileAsync(""));
        Assert.StartsWith("Error: Missing path", await _service.ReadFileAsync("   "));
    }

    [Fact]
    public async Task ReadFileAsync_QuotedPath_IsUnwrapped()
    {
        var path = WriteFile("quoted.txt", "content");

        var result = await _service.ReadFileAsync($"\"{path}\"");

        Assert.Equal("content", result);
    }

    [Fact]
    public void ResolvePath_ExpandsHomeTilde()
    {
        var (path, error) = FileReadService.ResolvePath("~/Documents/save.txt");

        Assert.Null(error);
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "save.txt");
        Assert.Equal(expected, path);
    }

    [Fact]
    public void ResolvePath_ExpandsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("YAOLLM_TEST_DIR", _dir);
        try
        {
            var (path, error) = FileReadService.ResolvePath("%YAOLLM_TEST_DIR%\\file.txt");

            Assert.Null(error);
            Assert.Equal(Path.Combine(_dir, "file.txt"), path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YAOLLM_TEST_DIR", null);
        }
    }

    [Fact]
    public async Task ReadFileAsync_BinaryFile_IsRejected()
    {
        var path = Path.Combine(_dir, "blob.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x0D, 0x0A });

        var result = await _service.ReadFileAsync(path);

        Assert.StartsWith("Error:", result);
        Assert.Contains("binary", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFileAsync_EmptyFile_ReturnsMarker()
    {
        var path = WriteFile("empty.txt", "");

        var result = await _service.ReadFileAsync(path);

        Assert.Equal("(empty file)", result);
    }

    [Fact]
    public async Task ReadFileAsync_TruncatesLongContent_WithMarker()
    {
        var path = WriteFile("long.txt", string.Join("\n", Enumerable.Range(1, 1000).Select(i => $"line {i}")));

        var result = await _service.ReadFileAsync(path, maxLength: 500);

        Assert.True(result.Length < 700);
        Assert.Contains("[Truncated at 500 characters", result);
        Assert.Contains("start_line", result);
        Assert.StartsWith("line 1", result);
    }

    [Fact]
    public async Task ReadFileAsync_LineWindow_ReturnsOnlyRequestedLines()
    {
        var path = WriteFile("window.txt", string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}")));

        var result = await _service.ReadFileAsync(path, startLine: 10, endLine: 12);

        Assert.Equal("line 10\nline 11\nline 12", result);
    }

    [Fact]
    public async Task ReadFileAsync_StartLineOnly_ReadsToEof()
    {
        var path = WriteFile("tail.txt", "a\nb\nc\nd");

        var result = await _service.ReadFileAsync(path, startLine: 3);

        Assert.Equal("c\nd", result);
    }

    [Fact]
    public async Task ReadFileAsync_StartAfterEnd_ReturnsError()
    {
        var path = WriteFile("bad.txt", "a\nb");

        var result = await _service.ReadFileAsync(path, startLine: 5, endLine: 2);

        Assert.StartsWith("Error:", result);
        Assert.Contains("start_line", result);
    }

    [Fact]
    public async Task ReadFileAsync_LineRangeBeyondEof_ReturnsMarker()
    {
        var path = WriteFile("short.txt", "a\nb");

        var result = await _service.ReadFileAsync(path, startLine: 50, endLine: 60);

        Assert.StartsWith("(no content in lines 50-60", result);
    }

    [Fact]
    public async Task ReadFileAsync_CancellationToken_Throws()
    {
        var path = WriteFile("cancel.txt", "content");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.ReadFileAsync(path, cancellationToken: cts.Token));
    }
}
