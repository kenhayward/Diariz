using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Pure (EF-free) tests for <see cref="FormulaContextBuilder"/>: each <see cref="FormulaContext"/>
/// flag pulls in exactly its section, labelled with a short header, and Attachments is a Phase 1 no-op.</summary>
public class FormulaContextBuilderTests
{
    private static readonly SegmentDto Seg =
        new(Guid.NewGuid(), "SPEAKER_00", "Alice", 0, 1000, "Hello there.");

    private static FormulaContextData FullData() => new(
        Segments: [Seg],
        Summary: "A concise summary.",
        Minutes: "# Minutes\n\nBody.",
        NoteLines: ["Ask about budget"],
        Actions: [new RecordingActionDto(Guid.NewGuid(), "Send report", "Bob", "Friday", 0)]);

    [Fact]
    public void Build_TranscriptFlag_IncludesTranscriptSection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Transcript, FullData());
        Assert.Contains("## Transcript", text);
        Assert.Contains("Alice", text);
        Assert.Contains("Hello there.", text);
    }

    [Fact]
    public void Build_WithoutTranscriptFlag_ExcludesTranscriptSection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Summary, FullData());
        Assert.DoesNotContain("## Transcript", text);
        Assert.DoesNotContain("Hello there.", text);
    }

    [Fact]
    public void Build_SummaryFlag_IncludesSummarySection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Summary, FullData());
        Assert.Contains("## Summary", text);
        Assert.Contains("A concise summary.", text);
    }

    [Fact]
    public void Build_WithoutSummaryFlag_ExcludesSummarySection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Transcript, FullData());
        Assert.DoesNotContain("## Summary", text);
        Assert.DoesNotContain("A concise summary.", text);
    }

    [Fact]
    public void Build_MinutesFlag_IncludesMinutesSection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Minutes, FullData());
        Assert.Contains("## Minutes", text);
        Assert.Contains("Body.", text);
    }

    [Fact]
    public void Build_WithoutMinutesFlag_ExcludesMinutesSection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Transcript, FullData());
        Assert.DoesNotContain("## Minutes", text);
    }

    [Fact]
    public void Build_NotesFlag_IncludesNotesSection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Notes, FullData());
        Assert.Contains("## Notes", text);
        Assert.Contains("Ask about budget", text);
    }

    [Fact]
    public void Build_WithoutNotesFlag_ExcludesNotesSection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Transcript, FullData());
        Assert.DoesNotContain("## Notes", text);
        Assert.DoesNotContain("Ask about budget", text);
    }

    [Fact]
    public void Build_ActionsFlag_IncludesActionsSection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Actions, FullData());
        Assert.Contains("## Actions", text);
        Assert.Contains("Send report", text);
    }

    [Fact]
    public void Build_WithoutActionsFlag_ExcludesActionsSection()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.Transcript, FullData());
        Assert.DoesNotContain("## Actions", text);
        Assert.DoesNotContain("Send report", text);
    }

    [Fact]
    public void Build_AttachmentsFlag_IsANoOp()
    {
        // Phase 1: attachments extraction isn't implemented. Setting the flag must never fail, and must not
        // add any section (there is no attachment content in FormulaContextData to draw from yet). With no
        // other content, the empty-context fallback stands in for a body.
        var text = FormulaContextBuilder.Build(FormulaContext.Attachments, FullData());
        Assert.DoesNotContain("## ", text);
        Assert.Equal(FormulaContextBuilder.EmptyContextFallback, text);
    }

    [Fact]
    public void Build_NoneFlag_YieldsFallback()
    {
        var text = FormulaContextBuilder.Build(FormulaContext.None, FullData());
        Assert.Equal(FormulaContextBuilder.EmptyContextFallback, text);
    }

    [Fact]
    public void Build_AllFlags_IncludesAllSectionsInOrder()
    {
        var flags = FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Minutes
                    | FormulaContext.Notes | FormulaContext.Actions | FormulaContext.Attachments;
        var text = FormulaContextBuilder.Build(flags, FullData());

        var transcriptIdx = text.IndexOf("## Transcript", StringComparison.Ordinal);
        var summaryIdx = text.IndexOf("## Summary", StringComparison.Ordinal);
        var minutesIdx = text.IndexOf("## Minutes", StringComparison.Ordinal);
        var notesIdx = text.IndexOf("## Notes", StringComparison.Ordinal);
        var actionsIdx = text.IndexOf("## Actions", StringComparison.Ordinal);

        Assert.True(transcriptIdx >= 0 && summaryIdx > transcriptIdx && minutesIdx > summaryIdx
                    && notesIdx > minutesIdx && actionsIdx > notesIdx);
    }

    [Fact]
    public void Build_EmptyOptionalSections_AreSkippedEvenWhenFlagged()
    {
        var data = new FormulaContextData([], null, null, [], []);
        var flags = FormulaContext.Summary | FormulaContext.Minutes | FormulaContext.Notes | FormulaContext.Actions;
        var text = FormulaContextBuilder.Build(flags, data);
        Assert.DoesNotContain("## ", text);
    }

    [Fact]
    public void Build_FlagsSetButAllDataEmpty_ReturnsFallback()
    {
        var data = new FormulaContextData([], null, null, [], []);
        var flags = FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Minutes
                    | FormulaContext.Notes | FormulaContext.Actions;
        var text = FormulaContextBuilder.Build(flags, data);
        Assert.Equal(FormulaContextBuilder.EmptyContextFallback, text);
    }

    [Fact]
    public void Build_BodyExceedingCharBudget_IsTruncatedWithMarker()
    {
        // A summary far larger than the tiny budget forces truncation.
        var big = new string('x', 5_000);
        var data = new FormulaContextData([], big, null, [], []);
        var text = FormulaContextBuilder.Build(FormulaContext.Summary, data, charBudget: 500);
        Assert.Contains("[context truncated]", text);
        Assert.True(text.Length < big.Length);
    }
}
