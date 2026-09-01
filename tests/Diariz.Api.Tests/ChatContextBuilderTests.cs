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
    public void BuildSystemPrompt_NamesTheCurrentUser_WhenProvided()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt(
            [Standup], null, null, null, ChatContextBuilder.DefaultCharBudget, "Alice Smith", "alice@example.com");

        Assert.Contains("Alice Smith", prompt);
        Assert.Contains("alice@example.com", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_OmitsUserLine_WhenNoIdentityProvided()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt([Standup], null, null);
        Assert.DoesNotContain("You are assisting", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_IncludesTodaysDate_WhenProvided()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt(
            [Standup], null, null, null, ChatContextBuilder.DefaultCharBudget, null, null,
            new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("Today's date is 2026-07-04.", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_OmitsTodaysDate_WhenNotProvided()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt([Standup], null, null);
        Assert.DoesNotContain("Today's date is", prompt);
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
    public void BuildSystemPrompt_GroundsQuestionsInTheUsersMeetings()
    {
        var prompt = ChatContextBuilder.BuildSystemPrompt([Standup], null, null);
        // A bare name/topic should be treated as about their recordings (the user's reported pain point).
        Assert.Contains("their recordings", prompt);
        Assert.Contains("general knowledge", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_TruncatesToCharBudget()
    {
        var big = new TranscriptContext("Big", new string('x', 5000));
        var prompt = ChatContextBuilder.BuildSystemPrompt([big], null, null, charBudget: 500);

        // Context body bounded to the budget; total = preamble + budget + a short marker, well under the 5000-char input.
        Assert.True(prompt.Length < 1500);
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

    // ---- Vision attachments ----

    [Fact]
    public void BuildMessages_AttachesImagesToTheLastUserMessage()
    {
        // The last user turn is the one the images are context FOR. Putting them on the system message
        // would be rejected outright by several OpenAI-compatible servers.
        var msgs = ChatContextBuilder.BuildMessages(
            "sys",
            [new("user", "first"), new("assistant", "reply"), new("user", "what is this?")],
            ["data:image/png;base64,AAA"]);

        Assert.Equal(["data:image/png;base64,AAA"], msgs[^1].ImageDataUrls);
        Assert.All(msgs.Take(msgs.Count - 1), m => Assert.Null(m.ImageDataUrls));
    }

    [Fact]
    public void BuildMessages_WithNoImages_LeavesEveryMessageUnadorned()
    {
        var msgs = ChatContextBuilder.BuildMessages("sys", [new("user", "hello")]);

        Assert.All(msgs, m => Assert.Null(m.ImageDataUrls));
    }

    /// <summary>A history with nothing to attach to must not throw mid-turn. There is no sensible place to
    /// put the images, so they are dropped rather than forced onto the system prompt.</summary>
    [Fact]
    public void BuildMessages_NoUserMessage_DropsTheImagesInsteadOfThrowing()
    {
        var msgs = ChatContextBuilder.BuildMessages("sys", [], ["data:image/png;base64,AAA"]);

        Assert.All(msgs, m => Assert.Null(m.ImageDataUrls));
    }

    [Fact]
    public void BuildMessages_LastTurnIsAssistant_StillAttachesToTheLastUserTurn()
    {
        var msgs = ChatContextBuilder.BuildMessages(
            "sys", [new("user", "look"), new("assistant", "at what?")], ["data:image/png;base64,AAA"]);

        var lastUser = msgs.Last(m => m.Role == "user");
        Assert.Equal(["data:image/png;base64,AAA"], lastUser.ImageDataUrls);
    }


    // ---- a meeting still in progress ----

    [Fact]
    public void BuildSystemPrompt_MarksATranscriptFromAMeetingStillRunning()
    {
        // Without this the model reads a partial transcript as a finished one and answers as though
        // the discussion concluded - "they decided to defer it" when they are still arguing about it.
        // One line of prompt, easy to drop, and impossible to notice from the outside.
        var prompt = ChatContextBuilder.BuildSystemPrompt(
            [new TranscriptContext("Standup", "Shall we make a start", InProgress: true)], null, null);

        Assert.Contains("in progress", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSystemPrompt_DoesNotMarkAFinishedTranscript()
    {
        // The companion: the marker must not appear on every meeting, or it means nothing on the one
        // that is actually running.
        var prompt = ChatContextBuilder.BuildSystemPrompt(
            [new TranscriptContext("Standup", "Shall we make a start")], null, null);

        Assert.DoesNotContain("in progress", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSystemPrompt_StillIncludesTheTextOfARunningMeeting()
    {
        // Marking it must not mean withholding it - answering questions about what has been said is
        // the entire point.
        var prompt = ChatContextBuilder.BuildSystemPrompt(
            [new TranscriptContext("Standup", "the warehouse integration is slow", InProgress: true)], null, null);

        Assert.Contains("the warehouse integration is slow", prompt);
    }
}
