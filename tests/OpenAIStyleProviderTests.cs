using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using YAOLlm.Providers;
using Xunit;

namespace YAOLlm.Tests;

/// <summary>
/// Exposes protected OpenAIStyleProvider helpers for testing.
/// </summary>
internal class TestOpenAIStyleProvider : OpenAIStyleProvider
{
    public override string Name => "test";
    public override string Model { get; protected set; } = "test-model";
    public override bool SupportsWebSearch => true;
    protected override string StreamUrl => "http://localhost/test";

    public string FilterDsmlChunkPublic(string chunk) => FilterDsmlChunk(chunk);
    public void ResetDsmlBufferPublic() => ResetDsmlBuffer();

    public static string StripDsml(string input) => StripDsmlTags(input);
    public static Dictionary<string, object?> DeserializeArgs(string json) => DeserializeArguments(json);
    public static string? TtsText(ToolCall call) => ExtractTtsText(call);
    public static int IntArg(Dictionary<string, object?> args, string key, int fallback) => GetIntArg(args, key, fallback);
    public static string DetectMime(byte[] data) => DetectImageMimeType(data);

    // TryParseStreamChunk returns a protected nested type, so expose the
    // pieces the tests need as primitives.
    public string? ParseChunkError(string json) => TryParseStreamChunk(json).Error;
    public string? ParseChunkText(string json) => TryParseStreamChunk(json).Chunk;
    public int ParseChunkToolDeltaCount(string json) => TryParseStreamChunk(json).ToolCallDeltas.Count;

    public override async IAsyncEnumerable<string> StreamAsync(List<ChatMessage> history, byte[]? image = null, List<ToolDefinition>? tools = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public class OpenAIStyleProviderTests
{
    [Fact]
    public void ParseChunk_ExplicitNullToolCalls_DoesNotThrow()
    {
        // Regression: reasoning models / proxies emit "tool_calls": null in
        // deltas; this used to throw InvalidOperationException and kill the
        // whole stream ("...requires an element of type 'Array', but the
        // target element has type 'Null'").
        var provider = new TestOpenAIStyleProvider();

        var error = provider.ParseChunkError("""{"choices":[{"delta":{"tool_calls":null}}]}""");

        Assert.Null(error);
        Assert.Equal(0, provider.ParseChunkToolDeltaCount("""{"choices":[{"delta":{"tool_calls":null}}]}"""));
    }

    [Fact]
    public void ParseChunk_ExplicitNullChoices_DoesNotThrow()
    {
        var provider = new TestOpenAIStyleProvider();

        Assert.Null(provider.ParseChunkError("""{"choices":null}"""));
        Assert.Null(provider.ParseChunkText("""{"choices":null}"""));
    }

    [Fact]
    public void ParseChunk_ValidContentAndToolDeltas_StillParsed()
    {
        var provider = new TestOpenAIStyleProvider();

        Assert.Equal("hi", provider.ParseChunkText("""{"choices":[{"delta":{"content":"hi"}}]}"""));

        const string toolDelta = """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"web_fetch","arguments":"{\"url\":"}}]}}]}""";
        Assert.Null(provider.ParseChunkError(toolDelta));
        Assert.Equal(1, provider.ParseChunkToolDeltaCount(toolDelta));
    }

    [Fact]
    public void ParseChunk_MalformedJson_ReportsErrorWithoutThrowing()
    {
        var provider = new TestOpenAIStyleProvider();

        Assert.NotNull(provider.ParseChunkError("""{"choices": [broken"""));
    }

    [Fact]
    public void StripDsmlTags_RemovesTags()
    {
        Assert.Equal("hello", TestOpenAIStyleProvider.StripDsml("hello<||DSML||something>"));
        Assert.Equal("a b", TestOpenAIStyleProvider.StripDsml("a<||DSML||x>b".Replace("b", " b")));
        Assert.Equal("", TestOpenAIStyleProvider.StripDsml("<||DSML||all>"));
    }

    [Fact]
    public void FilterDsmlChunk_HoldsBackPartialTagAcrossChunks()
    {
        var provider = new TestOpenAIStyleProvider();
        provider.ResetDsmlBufferPublic();

        // First chunk ends with an unterminated DSML tag prefix — must be held back
        var first = provider.FilterDsmlChunkPublic("answer<|");
        Assert.Equal("answer", first);

        // Completion arrives in the next chunk — no leakage of partial tag
        var second = provider.FilterDsmlChunkPublic("|DSML||x>done");
        Assert.Equal("done", second);
    }

    [Fact]
    public void FilterDsmlChunk_LeavesRegularAngleBracketsAlone()
    {
        var provider = new TestOpenAIStyleProvider();
        provider.ResetDsmlBufferPublic();

        var result = provider.FilterDsmlChunkPublic("1 < 2 and 3 > 2");
        Assert.Equal("1 < 2 and 3 > 2", result);
    }

    [Fact]
    public void DeserializeArguments_ParsesNestedJson()
    {
        var args = TestOpenAIStyleProvider.DeserializeArgs("""{"query":"test","max_results":3,"flag":true}""");

        // Note: scalars box as JsonElement — GetIntArg handles that
        Assert.Equal("test", args["query"]?.ToString());
        Assert.Equal(3, TestOpenAIStyleProvider.IntArg(args, "max_results", 0));
        Assert.Equal("True", args["flag"]?.ToString());
    }

    [Fact]
    public void DeserializeArguments_InvalidJson_ReturnsEmpty()
    {
        var args = TestOpenAIStyleProvider.DeserializeArgs("not json {");

        Assert.Empty(args);
    }

    [Fact]
    public void ExtractTtsText_DetectsTtsSummaryOnly()
    {
        var tts = new ToolCall { Name = "tts_summary", Arguments = new() { ["text"] = "spoken words" } };
        var search = new ToolCall { Name = "web_search", Arguments = new() { ["text"] = "not tts" } };

        Assert.Equal("spoken words", TestOpenAIStyleProvider.TtsText(tts));
        Assert.Null(TestOpenAIStyleProvider.TtsText(search));
    }

    [Theory]
    [InlineData(5L, 5)]
    [InlineData(5.0, 5)]
    [InlineData("7", 7)]
    [InlineData(-1, -1)]
    [InlineData(null, 5)]
    public void GetIntArg_HandlesBoxedTypes(object? value, int expected)
    {
        var args = new Dictionary<string, object?> { ["k"] = value };
        Assert.Equal(expected, TestOpenAIStyleProvider.IntArg(args, "k", 5));
    }

    [Fact]
    public void GetIntArg_MissingKey_ReturnsDefault()
    {
        Assert.Equal(9, TestOpenAIStyleProvider.IntArg(new Dictionary<string, object?>(), "missing", 9));
    }

    [Fact]
    public void DetectImageMimeType_KnowsMagicBytes()
    {
        Assert.Equal("image/png", TestOpenAIStyleProvider.DetectMime(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }));
        Assert.Equal("image/jpeg", TestOpenAIStyleProvider.DetectMime(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal("image/gif", TestOpenAIStyleProvider.DetectMime(new byte[] { 0x47, 0x49, 0x46, 0x38 }));
        Assert.Equal("image/webp", TestOpenAIStyleProvider.DetectMime(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 }));
        Assert.Equal("image/jpeg", TestOpenAIStyleProvider.DetectMime(new byte[] { 0x00, 0x01 }));
    }
}
