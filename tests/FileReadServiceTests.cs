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

    // ─── grep ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Grep_ReturnsOnlyMatchingLines_WithAbsoluteLineNumbers()
    {
        var path = WriteFile("code.txt",
            "using System;\nvar x = 1;\n// TODO fix\nvar y = 2;\n// TODO later");

        var result = await _service.ReadFileAsync(path, grep: "todo");

        Assert.Equal("3: // TODO fix\n5: // TODO later\n[2 matches for 'todo']", result);
    }

    [Fact]
    public async Task Grep_IsCaseInsensitive_ByDefault()
    {
        var path = WriteFile("case.txt", "ERROR boom\nfine\nerror again");

        var result = await _service.ReadFileAsync(path, grep: "error");

        Assert.Contains("1: ERROR boom", result);
        Assert.Contains("3: error again", result);
        Assert.DoesNotContain("2: fine", result);
    }

    [Fact]
    public async Task Grep_NoMatches_ReturnsCheapMarker()
    {
        var path = WriteFile("none.txt", "a\nb\nc");

        var result = await _service.ReadFileAsync(path, grep: "zzz");

        Assert.Equal("(no matches for 'zzz')", result);
    }

    [Fact]
    public async Task Grep_WithoutContext_NeverSeparatesMatches()
    {
        var path = WriteFile("gap.txt", "M1\nx\ny\nz\nM2");

        var result = await _service.ReadFileAsync(path, grep: "M", context: 0);

        Assert.Equal("1: M1\n5: M2\n[2 matches for 'M']", result);
    }

    [Fact]
    public async Task Grep_Context_DisjointWindows_GetSeparator()
    {
        var path = WriteFile("gap.txt", "a\nM1\nb\nc\nd\nM2\ne");

        // Window 1 = lines 1-3, window 2 = lines 5-7; line 4 is the gap.
        var result = await _service.ReadFileAsync(path, grep: "M", context: 1);

        Assert.Equal("1: a\n2: M1\n3: b\n--\n5: d\n6: M2\n7: e\n[2 matches for 'M']", result);
    }

    [Fact]
    public async Task Grep_Context_OverlappingWindows_Merge()
    {
        var path = WriteFile("merge.txt", "a\nM1\nb\nc\nM2\nd");

        // Window 1 = lines 1-3, window 2 = lines 3-5 — overlap at line 3.
        var result = await _service.ReadFileAsync(path, grep: "M", context: 1);

        Assert.Equal("1: a\n2: M1\n3: b\n4: c\n5: M2\n6: d\n[2 matches for 'M']", result);
    }

    [Fact]
    public async Task Grep_RegexMode_MatchesPattern()
    {
        var path = WriteFile("rx.txt", "code=404\nok\ncode=500\nplain 200");

        var result = await _service.ReadFileAsync(path, grep: @"code=\d{3}", regex: true);

        Assert.Contains("1: code=404", result);
        Assert.Contains("3: code=500", result);
        Assert.DoesNotContain("plain 200", result);
    }

    [Fact]
    public async Task Grep_InvalidRegex_ReturnsError()
    {
        var path = WriteFile("bad.txt", "content");

        var result = await _service.ReadFileAsync(path, grep: "([unclosed", regex: true);

        Assert.StartsWith("Error: Invalid grep regex", result);
    }

    [Fact]
    public async Task Grep_EmptyPattern_ReturnsError()
    {
        var path = WriteFile("empty.txt", "content");

        Assert.StartsWith("Error: grep parameter is empty",
            await _service.ReadFileAsync(path, grep: "   "));
    }

    [Fact]
    public async Task Grep_RespectsLineRange_KeepsAbsoluteNumbers()
    {
        var path = WriteFile("range.txt", string.Join("\n",
            Enumerable.Range(1, 20).Select(i => $"item {i} hit={i % 2}")));

        // Search only lines 5-15; numbers stay absolute.
        var result = await _service.ReadFileAsync(path, grep: "hit=0", startLine: 5, endLine: 15);

        Assert.Contains("6: item 6 hit=0", result);
        Assert.Contains("14: item 14 hit=0", result);
        Assert.DoesNotContain("2: item 2", result);
        Assert.DoesNotContain("16: item 16", result);
    }

    [Fact]
    public async Task Grep_CharCap_TruncatesWithMarker()
    {
        var lines = Enumerable.Range(1, 500).Select(i => $"line {i} needle").ToList();
        var path = WriteFile("big.txt", string.Join("\n", lines));

        var result = await _service.ReadFileAsync(path, grep: "needle", maxLength: 400);

        Assert.True(result.Length < 700);
        Assert.Contains("truncated at 400 characters", result);
        Assert.Contains("more specific pattern", result);
    }

    [Fact]
    public async Task Grep_MatchCap_StopsAt200_WithMarker()
    {
        var lines = Enumerable.Range(1, 250).Select(i => $"hit {i}").ToList();
        var path = WriteFile("many.txt", string.Join("\n", lines));

        var result = await _service.ReadFileAsync(path, grep: "hit");

        Assert.Contains("201 matches for 'hit'", result);
        Assert.Contains($"showing the first {FileReadService.MaxGrepMatches}", result);
        Assert.Contains($"200: hit 200", result);
        Assert.DoesNotContain("201: hit 201", result);
    }

    [Fact]
    public async Task Grep_LongFile_SkipsWithoutStoringEverything()
    {
        // 20k non-matching lines then one match — must not blow up.
        var lines = new List<string>(Enumerable.Range(1, 20_000).Select(i => $"filler {i}"))
            .Append("the one match").ToList();
        var path = WriteFile("huge.txt", string.Join("\n", lines));

        var result = await _service.ReadFileAsync(path, grep: "the one match");

        Assert.Contains($"20001: the one match", result);
    }

    // ─── Allowlist gating ─────────────────────────────────────────────

    [Fact]
    public async Task AllowlistedDirectory_FileInside_IsReadable()
    {
        var path = WriteFile("inside.txt", "secret content");
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { _dir });
        var service = new FileReadService(allowlist: allowlist);

        var result = await service.ReadFileAsync(path);

        Assert.Equal("secret content", result);
    }

    [Fact]
    public async Task AllowlistedFile_ExactlyThatFile_IsReadable()
    {
        var path = WriteFile("single.txt", "content");
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { path });
        var service = new FileReadService(allowlist: allowlist);

        Assert.Equal("content", await service.ReadFileAsync(path));
        Assert.StartsWith("Error: Access denied", await service.ReadFileAsync(WriteFile("outside.txt", "nope")));
    }

    [Fact]
    public async Task NoAllowlist_Unrestricted_LegacyBehavior()
    {
        var path = WriteFile("legacy.txt", "content");

        Assert.Equal("content", await _service.ReadFileAsync(path));
    }

    [Fact]
    public async Task EmptyAllowlist_DeniesReads_WithHelpfulError()
    {
        var path = WriteFile("denied.txt", "content");
        var service = new FileReadService(allowlist: new FileAllowlist());

        var result = await service.ReadFileAsync(path);

        Assert.StartsWith("Error: Access denied", result);
        Assert.Contains("not approved any files", result);
    }

    [Fact]
    public async Task PathOutsideAllowlist_Denied_WithHelpfulError()
    {
        var path = WriteFile("denied.txt", "content");
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { Path.Combine(_dir, "other") });
        var service = new FileReadService(allowlist: allowlist);

        var result = await service.ReadFileAsync(path);

        Assert.StartsWith("Error: Access denied", result);
        Assert.Contains("user-approved paths", result);
    }

    // ─── list_files ───────────────────────────────────────────────────

    [Fact]
    public async Task ListFilesAsync_ListsDirsFirst_ThenFilesWithSizes()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "zsub"));
        WriteFile("bfile.txt", "12345"); // 5 bytes

        var result = await _service.ListFilesAsync(_dir);

        Assert.Equal("zsub/\nbfile.txt (5 bytes)", result);
    }

    [Fact]
    public async Task ListFilesAsync_EmptyDirectory_ReturnsMarker()
    {
        var sub = SubDir("empty");

        Assert.Equal("(empty directory)", await _service.ListFilesAsync(sub));
    }

    [Fact]
    public async Task ListFilesAsync_MissingDirectory_ReturnsError()
    {
        var result = await _service.ListFilesAsync(Path.Combine(_dir, "nope"));

        Assert.StartsWith("Error: Directory not found", result);
    }

    [Fact]
    public async Task ListFilesAsync_FilePath_ReturnsError()
    {
        var path = WriteFile("afile.txt", "content");

        var result = await _service.ListFilesAsync(path);

        Assert.StartsWith("Error:", result);
        Assert.Contains("is a file", result);
    }

    [Fact]
    public async Task ListFilesAsync_OutsideAllowlist_Denied()
    {
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { Path.Combine(_dir, "approved") });
        var service = new FileReadService(allowlist: allowlist);

        var result = await service.ListFilesAsync(_dir);

        Assert.StartsWith("Error: Access denied", result);
    }

    [Fact]
    public async Task ListFilesAsync_InsideAllowlist_Works()
    {
        var approved = SubDir("approved");
        var inner = Directory.CreateDirectory(Path.Combine(approved, "inner"));
        WriteFile(Path.Combine("approved", "inner", "leaf.txt"), "x");
        var allowlist = new FileAllowlist();
        allowlist.Restore(new[] { approved });
        var service = new FileReadService(allowlist: allowlist);

        var approvedList = await service.ListFilesAsync(approved);
        Assert.Contains("inner/", approvedList);
        Assert.Equal("leaf.txt (1 bytes)", await service.ListFilesAsync(inner.FullName));
    }

    [Fact]
    public async Task ListFilesAsync_MoreThanCap_TruncatesWithMarker()
    {
        var sub = SubDir("many");
        for (var i = 0; i < FileReadService.MaxListEntries + 10; i++)
            WriteFile(Path.Combine("many", $"f{i:D3}.txt"), "x");

        var result = await _service.ListFilesAsync(sub);

        Assert.Contains($"more entries not shown", result);
        Assert.DoesNotContain($"f{FileReadService.MaxListEntries:D3}.txt", result);
    }

    private string SubDir(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
