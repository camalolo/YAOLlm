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
    public void MemoryContext_AppearsInSystemPrompt()
    {
        var manager = CreateManager();
        manager.Initialize(manager.BuildSystemPrompt());
        manager.MemoryFilePath = @"C:\Temp\YAOLlm\memory\memory.md";
        manager.WritableRoot = @"C:\Temp\YAOLlm\files";

        var prompt = manager.GetSnapshot()[0].Content ?? "";

        Assert.Contains("memory.md", prompt);
        Assert.Contains("writing memories", prompt);
        Assert.Contains("file_write may only create files under", prompt);
    }

    [Fact]
    public void MemoryContext_AbsentWhenFeatureOff()
    {
        var manager = CreateManager();
        manager.Initialize(manager.BuildSystemPrompt());

        Assert.DoesNotContain("Memory:", manager.GetSnapshot()[0].Content ?? "");
    }

    [Fact]
    public void SystemPrompt_SetsSpoilerFreePlayfulPersona()
    {
        var manager = CreateManager();
        var prompt = manager.BuildSystemPrompt();

        // Persona + default hint mode + experience-enhancement framing
        Assert.Contains("spoiler-free", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("playful", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("enhance the experience", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tone", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lore", prompt, StringComparison.OrdinalIgnoreCase);
        // Lore flavour is bounded — atmosphere must not foreshadow
        Assert.Contains("foreshadow", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemPrompt_GatesFullSolutionsBehindExplicitAsk()
    {
        var manager = CreateManager();
        var prompt = manager.BuildSystemPrompt();

        // The solution gate must demand an explicit ask, not just any help request
        Assert.Contains("ONLY", prompt, StringComparison.Ordinal);
        Assert.Contains("give me the solution", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemPrompt_TellsModelToUseInfoParamForToolCalls()
    {
        var manager = CreateManager();
        var prompt = manager.BuildSystemPrompt();

        // Reinforcement of the spoiler-free display label (tool schemas alone aren't enough)
        Assert.Contains("\"info\"", prompt, StringComparison.Ordinal);
        Assert.Contains("web_search", prompt, StringComparison.Ordinal);
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
    public void TtsEnabled_TogglesSystemPromptInstruction()
    {
        var manager = CreateManager();
        manager.Initialize(manager.BuildSystemPrompt());

        // Default is off — no TTS instruction in the prompt
        Assert.DoesNotContain("tts_summary", manager.GetSnapshot()[0].Content);

        manager.TtsEnabled = true;
        Assert.Contains("tts_summary", manager.GetSnapshot()[0].Content);

        manager.TtsEnabled = false;
        Assert.DoesNotContain("tts_summary", manager.GetSnapshot()[0].Content);
    }

    [Fact]
    public void TtsEnabled_PersistsThroughWindowTitlePromptRebuild()
    {
        var manager = CreateManager();
        manager.Initialize(manager.BuildSystemPrompt());
        manager.TtsEnabled = true;

        manager.CurrentWindowTitle = "Some Game";

        var prompt = manager.GetSnapshot()[0].Content;
        Assert.Contains("Some Game", prompt);
        Assert.Contains("tts_summary", prompt);
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

    [Fact]
    public void GetConversationTurns_ExcludesSystemMessage()
    {
        var manager = CreateManager();
        manager.Initialize("sys");
        manager.AddExchange(new ChatMessage(ChatRole.User, "q1"), "a1");

        var turns = manager.GetConversationTurns();

        Assert.Equal(2, turns.Count);
        Assert.Equal(ChatRole.User, turns[0].Role);
        Assert.Equal("q1", turns[0].Content);
        Assert.Equal(ChatRole.Model, turns[1].Role);
    }

    [Fact]
    public void GetConversationTurns_EmptyWhenOnlySystemMessage()
    {
        var manager = CreateManager();
        manager.Initialize("sys");

        Assert.Empty(manager.GetConversationTurns());
    }

    [Fact]
    public void GetConversationTurns_ReturnsCopy()
    {
        var manager = CreateManager();
        manager.Initialize("sys");
        manager.AddExchange(new ChatMessage(ChatRole.User, "q1"), "a1");

        var turns = manager.GetConversationTurns();
        turns.Add(new ChatMessage(ChatRole.User, "injected"));

        Assert.Equal(3, manager.GetSnapshot().Count);
    }

    [Fact]
    public void Compact_ReplacesHistoryWithSystemAndSummary()
    {
        var manager = CreateManager();
        manager.Initialize("sys");
        manager.AddExchange(new ChatMessage(ChatRole.User, "long question"), "long answer");
        manager.AddExchange(new ChatMessage(ChatRole.User, "q2"), "a2");

        manager.Compact("summary of everything");

        var snapshot = manager.GetSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(ChatRole.System, snapshot[0].Role);
        Assert.Equal("sys", snapshot[0].Content);
        Assert.Equal(ChatRole.User, snapshot[1].Role);
        Assert.Equal("summary of everything", snapshot[1].Content);
    }

    [Fact]
    public void Compact_KeepsLatestSystemPrompt()
    {
        var manager = CreateManager();
        manager.Initialize(manager.BuildSystemPrompt());
        manager.TtsEnabled = true;
        manager.CurrentWindowTitle = "Some Game";
        manager.AddExchange(new ChatMessage(ChatRole.User, "q1"), "a1");

        manager.Compact("summary");

        var systemPrompt = manager.GetSnapshot()[0].Content;
        Assert.Contains("Some Game", systemPrompt);
        Assert.Contains("tts_summary", systemPrompt);
    }

    [Fact]
    public void Compact_AllowsSubsequentExchangesAfterSummary()
    {
        var manager = CreateManager();
        manager.Initialize("sys");
        manager.AddExchange(new ChatMessage(ChatRole.User, "q1"), "a1");
        manager.Compact("summary");

        manager.AddExchange(new ChatMessage(ChatRole.User, "q2"), "a2");

        var snapshot = manager.GetSnapshot();
        Assert.Equal(4, snapshot.Count);
        Assert.Equal("summary", snapshot[1].Content);
        Assert.Equal("q2", snapshot[2].Content);
        Assert.Equal("a2", snapshot[3].Content);
    }

    [Fact]
    public void Compact_CanRunRepeatedly()
    {
        var manager = CreateManager();
        manager.Initialize("sys");
        manager.AddExchange(new ChatMessage(ChatRole.User, "q1"), "a1");
        manager.Compact("first summary");
        manager.AddExchange(new ChatMessage(ChatRole.User, "q2"), "a2");

        manager.Compact("second summary");

        var snapshot = manager.GetSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("second summary", snapshot[1].Content);
    }

    [Fact]
    public void Compact_ShrinksTotalCharacterCount()
    {
        var manager = CreateManager();
        manager.Initialize("sys");
        for (int i = 0; i < 10; i++)
            manager.AddExchange(new ChatMessage(ChatRole.User, $"question {i} with a lot of text"), $"answer {i} with a lot of text");

        var before = manager.GetTotalCharacterCount();
        manager.Compact("tiny");
        var after = manager.GetTotalCharacterCount();

        Assert.True(after < before);
    }

    [Fact]
    public void Compact_NullSummary_Throws()
    {
        var manager = CreateManager();
        manager.Initialize("sys");

        Assert.Throws<ArgumentNullException>(() => manager.Compact(null!));
    }

    [Fact]
    public void OnHistoryChanged_FiresOnStructuralMutations()
    {
        var manager = CreateManager();
        var snapshots = new List<IReadOnlyList<ChatMessage>>();
        manager.OnHistoryChanged = snapshots.Add;

        manager.Initialize("sys");
        manager.AddExchange(new ChatMessage(ChatRole.User, "q1"), "a1");
        manager.Compact("summary");

        Assert.Equal(3, snapshots.Count);
        Assert.Single(snapshots[0]);
        Assert.Equal(3, snapshots[1].Count);
        Assert.Equal(2, snapshots[2].Count);
        // Snapshots must be copies, not the live list
        Assert.NotSame(manager.GetSnapshot(), snapshots[^1]);
    }

    [Fact]
    public void RestoreTurns_AppendsAfterSystemMessage()
    {
        var manager = CreateManager();
        manager.Initialize("sys");

        manager.RestoreTurns(new List<ChatMessage>
        {
            new(ChatRole.User, "old question"),
            new(ChatRole.Model, "old answer"),
        });

        var snapshot = manager.GetSnapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(ChatRole.System, snapshot[0].Role);
        Assert.Equal("old question", snapshot[1].Content);
        Assert.Equal("old answer", snapshot[2].Content);
    }

    [Fact]
    public void RestoreTurns_EnforcesHistoryCap()
    {
        var manager = CreateManager();
        manager.Initialize("sys");

        var turns = new List<ChatMessage>();
        for (int i = 0; i < 40; i++)
        {
            turns.Add(new ChatMessage(ChatRole.User, $"q{i}"));
            turns.Add(new ChatMessage(ChatRole.Model, $"a{i}"));
        }

        manager.RestoreTurns(turns);

        var snapshot = manager.GetSnapshot();
        Assert.True(snapshot.Count <= 32);
        Assert.Equal(ChatRole.System, snapshot[0].Role);
        // Newest turns kept after trim
        Assert.Equal("q39", snapshot[^2].Content);
        Assert.Equal("a39", snapshot[^1].Content);
    }

    [Fact]
    public void RestoreTurns_FiresOnHistoryChangedOnce()
    {
        var manager = CreateManager();
        manager.Initialize("sys");
        var count = 0;
        manager.OnHistoryChanged = _ => count++;

        manager.RestoreTurns(new List<ChatMessage> { new(ChatRole.User, "q1") });

        Assert.Equal(1, count);
    }

    [Fact]
    public void RestoreTurns_ResumedExchangesAppendNormally()
    {
        var manager = CreateManager();
        manager.Initialize("sys");
        manager.RestoreTurns(new List<ChatMessage> { new(ChatRole.User, "old"), new(ChatRole.Model, "reply") });

        manager.AddExchange(new ChatMessage(ChatRole.User, "new"), "answer");

        var snapshot = manager.GetSnapshot();
        Assert.Equal(5, snapshot.Count);
        Assert.Equal("new", snapshot[3].Content);
        Assert.Equal("answer", snapshot[4].Content);
    }
}
