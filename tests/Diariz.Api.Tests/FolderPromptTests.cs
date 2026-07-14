using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Pure construction of the folder-summary and folder-minutes chat requests (combining N recording
/// summaries / minutes into one roll-up), including char-budget truncation and meeting-type substitution.</summary>
public class FolderPromptTests
{
    [Fact]
    public void FolderSummary_BuildsSystemAndLabelledItems()
    {
        var msgs = FolderSummaryPrompt.BuildMessages(
            FolderSummaryPrompt.DefaultTemplate,
            [("Kickoff", "We agreed the scope."), ("Review", "Progress on track.")],
            10_000);

        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Contains("folder summary", msgs[0].Content);
        Assert.Contains("## Kickoff", msgs[1].Content);
        Assert.Contains("We agreed the scope.", msgs[1].Content);
        Assert.Contains("## Review", msgs[1].Content);
    }

    [Fact]
    public void FolderSummary_EmptyItems_YieldsNonePlaceholder()
    {
        var msgs = FolderSummaryPrompt.BuildMessages(FolderSummaryPrompt.DefaultTemplate, [], 10_000);
        Assert.Contains("(none)", msgs[1].Content);
    }

    [Fact]
    public void FolderSummary_OverBudget_TruncatesAndNotesOmitted()
    {
        var items = Enumerable.Range(0, 20)
            .Select(i => ($"Meeting {i}", new string('x', 500)))
            .ToList();
        var msgs = FolderSummaryPrompt.BuildMessages(FolderSummaryPrompt.DefaultTemplate, items, 1_200);

        Assert.Contains("more meeting(s) omitted", msgs[1].Content);
        Assert.True(msgs[1].Content.Length < 4_000); // bounded, not the full 20*500
    }

    [Fact]
    public void FolderMinutes_SubstitutesTypeTitleOverviewAndStructure()
    {
        var type = new MeetingType
        {
            Id = Guid.NewGuid(), Title = "1:1", Overview = "A manager-report sync.",
            ContentJson = new TemplateContent(
            [
                new TemplateSection(1, "Agenda", []),
                new TemplateSection(2, "Follow-ups", []),
            ]).Serialize(),
        };

        var msgs = FolderMinutesPrompt.BuildMessages(
            FolderMinutesPrompt.DefaultTemplate, type,
            [("Week 1", "# Notes\nDiscussed goals.")], 10_000);

        Assert.Contains("Meeting type: 1:1", msgs[0].Content);
        Assert.Contains("A manager-report sync.", msgs[0].Content);
        Assert.Contains("# Agenda", msgs[0].Content);
        Assert.Contains("## Follow-ups", msgs[0].Content);
        Assert.Contains("## Week 1", msgs[1].Content);
        Assert.Contains("Discussed goals.", msgs[1].Content);
    }

    [Fact]
    public void FolderMinutes_NullType_UsesGenericOutline()
    {
        var msgs = FolderMinutesPrompt.BuildMessages(
            FolderMinutesPrompt.DefaultTemplate, null, [("X", "body")], 10_000);
        Assert.Contains("General meeting", msgs[0].Content);
        Assert.Contains("Action items", msgs[0].Content); // generic fallback outline
    }
}
