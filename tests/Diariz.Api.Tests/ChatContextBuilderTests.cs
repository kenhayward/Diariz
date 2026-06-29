using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ChatContextBuilderTests
{
    private static readonly TranscriptContext Standup =
        new("Daily Standup", "[00:00] Alice: We ship Friday.\n[00:05] Bob: I'll write the tests.\n");
    private static readonly TranscriptContext Retro =
        new("Sprint Retro", "[00:00] Carol: The deploy was smooth.\n");

    [Fact]
    public void BuildSystemPrompt_IncludesTranscriptTitleAndBody()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt([Standup], null, null);

        Assert.Contains("Daily Standup", prompt);
        Assert.Contains("Alice: We ship Friday.", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_CombinesMultipleTranscripts()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt([Standup, Retro], null, null);

        Assert.Contains("Daily Standup", prompt);
        Assert.Contains("Sprint Retro", prompt);
        Assert.Contains("deploy was smooth", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_IncludesAttachment_WhenPresent()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt([Standup], "spec.pdf", "The widget must be blue.");

        Assert.Contains("spec.pdf", prompt);
        Assert.Contains("The widget must be blue.", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_AppendsEachAttachmentDocument()
    {
        var documents = new[]
        {
            new TranscriptContext("spec.docx", "Ship in Q3."),
            new TranscriptContext("roadmap-url", "Then Q4."),
        };

        var prompt = ChatContextBuilder.BuildSystemPrompt([Standup], null, null, documents);

        Assert.Contains("Attached document: spec.docx", prompt);
        Assert.Contains("Ship in Q3.", prompt);
        Assert.Contains("Attached document: roadmap-url", prompt);
        Assert.Contains("Then Q4.", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_OmitsAttachment_WhenEmpty()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt([Standup], "spec.pdf", "   ");
        Assert.DoesNotContain("spec.pdf", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_NoContext_StillProducesUsablePrompt()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt([], null, null);
        Assert.False(string.IsNullOrWhiteSpace(prompt));
    }

    [Fact]
    public void BuildSystemPrompt_TruncatesToCharBudget()
    {
        var big = new TranscriptContext("Big", new string('x', 5000));
        var prompt = ChatContextBuilder.BuildSystemPrompt([big], null, null, charBudget: 500);

        // Context body bounded to the budget; total = preamble + budget + a short marker, well under the 5000-char input.
        Assert.True(prompt.Length < 1000);
        Assert.Contains("truncated", prompt);
    }

    [Fact]
    public void BuildMessages_PrependsSystem_ThenHistory()
    {
        var system = "SYS";
        var history = new[] { new ChatMessage("user", "hi"), new ChatMessage("assistant", "hello") };

        var msgs = ChatContextBuilder.BuildMessages(system, history);

        Assert.Equal(3, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Equal("SYS", msgs[0].Content);
        Assert.Equal("user", msgs[1].Role);
        Assert.Equal("assistant", msgs[2].Role);
    }

    [Fact]
    public void BuildMessages_SkipsBlankHistoryTurns()
    {
        var msgs = ChatContextBuilder.BuildMessages("SYS",
            [new ChatMessage("user", "  "), new ChatMessage("user", "real")]);

        Assert.Equal(2, msgs.Count); // system + the one non-blank turn
        Assert.Equal("real", msgs[1].Content);
    }
}
