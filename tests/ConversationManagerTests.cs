using System.Linq;
using Xunit;

namespace YAOLlm.Tests;

public class ConversationManagerTests
{
    private static ConversationManager CreateManager()
        => new(new Logger());

    [Fact]
    public void Initialize_ContainsOnlySystemMessage()
    {
        var manager = CreateManager();
        manager.Initialize("system prompt");

        var snapshot = manager.GetSnapshot();
        Assert.Single(snapshot);
        Assert.Equal(ChatRole.System, snapshot[0].Role);
        Assert.Equal("system prompt", snapshot[0].Content);
    }

    [Fact]
    public void AddExchange_AppendsUserAndModel()
    {
        var manager = CreateManager();
        manager.Initialize("sys");

        manager.AddExchange(new ChatMessage(ChatRole.User, "hello"), "hi");

        var snapshot = manager.GetSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(ChatRole.User, snapshot[1].Role);
        Assert.Equal("hello", snapshot[1].Content);
        Assert.Equal(ChatRole.Model, snapshot[2].Role);
        Assert.Equal("hi", snapshot[2].Content);
    }

    [Fact]
    public void TrimHistoryIfNeeded_KeepsSystemMessageAndCapsEntries()
    {
        var manager = CreateManager();
        manager.Initialize("sys");

        // 40 exchanges = 80 messages + 1 system = 81 entries
        for (int i = 0; i < 40; i++)
            manager.AddExchange(new ChatMessage(ChatRole.User, $"q{i}"), $"a{i}");

        var snapshot = manager.GetSnapshot();
        Assert.True(snapshot.Count <= 32);
        Assert.Equal(ChatRole.System, snapshot[0].Role);
        // Oldest exchanges were removed, newest kept
        Assert.Equal("q39", snapshot[^2].Content);
        Assert.Equal("a39", snapshot[^1].Content);
    }

    [Fact]
    public void GetSnapshot_ReturnsCopy()
    {
        var manager = CreateManager();
        manager.Initialize("sys");

        var snapshot = manager.GetSnapshot();
        snapshot.Add(new ChatMessage(ChatRole.User, "injected"));

        Assert.Single(manager.GetSnapshot());
    }

    [Fact]
    public void CurrentWindowTitle_UpdatesSystemPrompt()
    {
        var manager = CreateManager();
        manager.Initialize(manager.BuildSystemPrompt());

        manager.CurrentWindowTitle = "Some Game";

        Assert.Contains("Some Game", manager.GetSnapshot()[0].Content);
    }

    [Fact]
    public void GetTotalCharacterCount_SumsContents()
    {
        var manager = CreateManager();
        manager.Initialize("1234"); // 4 chars

        Assert.Equal(4, manager.GetTotalCharacterCount());

        manager.AddExchange(new ChatMessage(ChatRole.User, "abc"), "de");
        Assert.Equal(9, manager.GetTotalCharacterCount());
    }
}
