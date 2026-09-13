using Xunit;

namespace YAOLlm.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly SessionStore _store;

    public SessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "YAOLlmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "session.json");
        _store = new SessionStore(new Logger(), _path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRolesAndContent()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, "question"),
            new(ChatRole.Model, "answer"),
        };

        _store.Save(history);
        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Count);
        Assert.Equal(ChatRole.System, loaded[0].Role);
        Assert.Equal("system prompt", loaded[0].Content);
        Assert.Equal(ChatRole.User, loaded[1].Role);
        Assert.Equal("question", loaded[1].Content);
        Assert.Equal(ChatRole.Model, loaded[2].Role);
        Assert.Equal("answer", loaded[2].Content);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsImageBytes()
    {
        var image = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "with image", image),
        };

        _store.Save(history);
        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.NotNull(loaded![0].Image);
        Assert.Equal(image, loaded[0].Image);
    }

    [Fact]
    public void Save_OverwritesPreviousSession()
    {
        _store.Save(new List<ChatMessage> { new(ChatRole.User, "first") });
        _store.Save(new List<ChatMessage> { new(ChatRole.User, "second"), new(ChatRole.Model, "reply") });

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.Equal("second", loaded[0].Content);
    }

    [Fact]
    public void Save_DoesNotLeaveTempFileBehind()
    {
        _store.Save(new List<ChatMessage> { new(ChatRole.User, "hello") });

        Assert.False(File.Exists(_path + ".tmp"));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(_store.Load());
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        File.WriteAllText(_path, "{ this is not json !!!");

        Assert.Null(_store.Load());
    }

    [Fact]
    public void Load_EmptyMessages_ReturnsNull()
    {
        _store.Save(new List<ChatMessage>());

        Assert.Null(_store.Load());
    }

    [Fact]
    public void Save_NullHistory_DoesNotThrow()
    {
        _store.Save(null!);

        Assert.False(File.Exists(_path));
    }
}
