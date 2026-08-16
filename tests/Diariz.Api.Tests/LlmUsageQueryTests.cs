using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class LlmUsageQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static LlmCall Row(
        DateTimeOffset startedAt, LlmCallKind kind = LlmCallKind.Summarize, Guid? userId = null,
        string model = "m", bool success = true, Guid? recordingId = null, Guid? sectionId = null) => new()
    {
        Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), Sequence = 1, Kind = kind,
        UserId = userId, UserEmail = "u@e.com", Model = model, Endpoint = "http://x/v1",
        StartedAt = startedAt, CompletedAt = startedAt, DurationMs = 1, Success = success,
        RecordingId = recordingId, SectionId = sectionId,
    };

    private static async Task<List<LlmCall>> QueryAsync(LlmUsageFilter filter, params LlmCall[] rows)
    {
        await using var db = TestDb.Create();
        db.LlmCalls.AddRange(rows);
        await db.SaveChangesAsync();
        return await LlmUsageQuery.Apply(db.LlmCalls, filter, Now).ToListAsync();
    }

    [Fact]
    public async Task DefaultsToTheLastThirtyDays_WhenNoFromIsGiven()
    {
        // The largest table in the database. An unbounded default would make the first page load a
        // full scan, and it would get worse every day the platform runs.
        var recent = Row(Now.AddDays(-3));
        var ancient = Row(Now.AddDays(-31));

        var found = await QueryAsync(new LlmUsageFilter(null, null, null, null, null, null, null, null), recent, ancient);

        Assert.Equal(recent.Id, Assert.Single(found).Id);
    }

    [Fact]
    public async Task AnExplicitFrom_OverridesTheDefaultWindow()
    {
        var ancient = Row(Now.AddDays(-90));

        var found = await QueryAsync(
            new LlmUsageFilter(Now.AddDays(-365), null, null, null, null, null, null, null), ancient);

        Assert.Single(found);
    }

    [Fact]
    public async Task FiltersByUser_Kind_AndModel()
    {
        var userId = Guid.NewGuid();
        var wanted = Row(Now.AddDays(-1), LlmCallKind.ChatMessage, userId, "qwen");
        var wrongUser = Row(Now.AddDays(-1), LlmCallKind.ChatMessage, Guid.NewGuid(), "qwen");
        var wrongKind = Row(Now.AddDays(-1), LlmCallKind.Tags, userId, "qwen");
        var wrongModel = Row(Now.AddDays(-1), LlmCallKind.ChatMessage, userId, "llama");

        var found = await QueryAsync(
            new LlmUsageFilter(null, null, [userId], [(int)LlmCallKind.ChatMessage], ["qwen"], null, null, null),
            wanted, wrongUser, wrongKind, wrongModel);

        Assert.Equal(wanted.Id, Assert.Single(found).Id);
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("ok", true)]
    public async Task FiltersByOutcome(string outcome, bool expectedSuccess)
    {
        var ok = Row(Now.AddDays(-1), success: true);
        var failed = Row(Now.AddDays(-1), success: false);

        var found = await QueryAsync(
            new LlmUsageFilter(null, null, null, null, null, outcome, null, null), ok, failed);

        Assert.Equal(expectedSuccess, Assert.Single(found).Success);
    }

    [Fact]
    public async Task OutcomeAll_ReturnsBoth()
    {
        var found = await QueryAsync(
            new LlmUsageFilter(null, null, null, null, null, "all", null, null),
            Row(Now.AddDays(-1), success: true), Row(Now.AddDays(-1), success: false));

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task FiltersByRecordingId()
    {
        // Never tested anywhere before this task (Task 1's review flagged it and it was carried
        // forward here deliberately) - and this same filter clause governs the destructive delete
        // path, not just this read path.
        var recordingId = Guid.NewGuid();
        var wanted = Row(Now.AddDays(-1), recordingId: recordingId);
        var otherRecording = Row(Now.AddDays(-1), recordingId: Guid.NewGuid());
        var noRecording = Row(Now.AddDays(-1));

        var found = await QueryAsync(
            new LlmUsageFilter(null, null, null, null, null, null, recordingId, null),
            wanted, otherRecording, noRecording);

        Assert.Equal(wanted.Id, Assert.Single(found).Id);
    }

    [Fact]
    public async Task FiltersBySectionId()
    {
        var sectionId = Guid.NewGuid();
        var wanted = Row(Now.AddDays(-1), sectionId: sectionId);
        var otherSection = Row(Now.AddDays(-1), sectionId: Guid.NewGuid());
        var noSection = Row(Now.AddDays(-1));

        var found = await QueryAsync(
            new LlmUsageFilter(null, null, null, null, null, null, null, sectionId),
            wanted, otherSection, noSection);

        Assert.Equal(wanted.Id, Assert.Single(found).Id);
    }

    [Fact]
    public async Task EmptyFilterArrays_AreIgnoredRatherThanMatchingNothing()
    {
        // A UI that clears its multi-select sends an empty array. Treating that as "match nothing"
        // would show an empty table and look like a bug.
        var found = await QueryAsync(
            new LlmUsageFilter(null, null, [], [], [], null, null, null), Row(Now.AddDays(-1)));

        Assert.Single(found);
    }

    [Theory]
    [InlineData("startedAt")]
    [InlineData("durationMs")]
    [InlineData("totalTokens")]
    public void TryResolveSort_AcceptsWhitelistedColumns(string sort)
    {
        Assert.True(LlmUsageQuery.TryResolveSort(sort, out _));
    }

    [Theory]
    [InlineData("UserEmail; DROP TABLE \"LlmCalls\"")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolveSort_RejectsAnythingElse(string? sort)
    {
        // The only defence that matters here: a sort key is a whitelist lookup, never string
        // interpolation into SQL.
        Assert.False(LlmUsageQuery.TryResolveSort(sort, out _));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("model")]
    [InlineData("kind")]
    [InlineData("user,model")]
    [InlineData("model,kind")]
    [InlineData("user,model,kind")]
    [InlineData(" user , model ")] // trimmed
    [InlineData("user,user")] // duplicates collapse rather than error
    public void TryResolveGroupBy_AcceptsWhitelistedDimensionCombinations(string groupBy)
    {
        Assert.True(LlmUsageQuery.TryResolveGroupBy(groupBy, out var dims));
        Assert.NotEmpty(dims);
    }

    [Theory]
    [InlineData("UserId; DROP TABLE \"LlmCalls\"")]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("user,nonsense")] // one bad token in an otherwise-valid list still rejects the whole thing
    public void TryResolveGroupBy_RejectsAnythingElse(string? groupBy)
    {
        // Same whitelist discipline as TryResolveSort: silently ignoring an unrecognised dimension would
        // show the administrator a different report from the one they asked for.
        Assert.False(LlmUsageQuery.TryResolveGroupBy(groupBy, out _));
    }
}
