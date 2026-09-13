using System.Linq;
using Xunit;

namespace YAOLlm.Tests;

public class CompactionPromptTests
{
    [Fact]
    public void BuildCompactionSystemPrompt_RequiresDetailPreservation()
    {
        var prompt = MainForm.BuildCompactionSystemPrompt();

        Assert.Contains("summar", prompt);
        Assert.Contains("next steps", prompt, StringComparison.OrdinalIgnoreCase);
        // The whole point of compaction: continue seamlessly without the original history
        Assert.Contains("seamlessly", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCompactionTranscript_LabelsUserAndAssistantTurns()
    {
        var turns = new List<ChatMessage>
        {
            new(ChatRole.User, "hello"),
            new(ChatRole.Model, "hi there"),
        };

        var transcript = MainForm.BuildCompactionTranscript(turns);

        Assert.Contains("Conversation to summarize:", transcript);
        Assert.Contains("[User]", transcript);
        Assert.Contains("hello", transcript);
        Assert.Contains("[Assistant]", transcript);
        Assert.Contains("hi there", transcript);
    }

    [Fact]
    public void BuildCompactionTranscript_NotesAttachedImages()
    {
        var turns = new List<ChatMessage>
        {
            new(ChatRole.User, "what is this?", image: new byte[] { 1, 2, 3 }),
        };

        var transcript = MainForm.BuildCompactionTranscript(turns);

        Assert.Contains("screenshot was attached", transcript);
    }

    [Fact]
    public void BuildCompactionTranscript_IncludesSystemAndErrorRoles()
    {
        var turns = new List<ChatMessage>
        {
            new(ChatRole.System, "searching..."),
            new(ChatRole.Error, "boom"),
            new(ChatRole.User, "ok"),
        };

        var transcript = MainForm.BuildCompactionTranscript(turns);

        Assert.Contains("[System]", transcript);
        Assert.Contains("searching...", transcript);
        Assert.Contains("[Message]", transcript);
        Assert.Contains("boom", transcript);
        Assert.Contains("[User]", transcript);
    }

    [Fact]
    public void BuildCompactionTranscript_HandlesNullContent()
    {
        var turns = new List<ChatMessage> { new(ChatRole.User, content: null) };

        var transcript = MainForm.BuildCompactionTranscript(turns);

        Assert.Contains("[User]", transcript);
    }
}
