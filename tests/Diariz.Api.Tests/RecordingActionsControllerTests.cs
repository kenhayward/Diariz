using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class RecordingActionsControllerTests
{
    private static RecordingActionsController Build(
        DiarizDbContext db, Guid userId, IActionsClient client, ISummarizationSettingsResolver? settings = null) =>
        new(db, client, settings ?? new FakeSummarizationSettingsResolver())
        { ControllerContext = Http.Context(userId) };

    private static async Task<Recording> SeedTranscribed(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", Title = "T" };
        db.Recordings.Add(rec);
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Transcriptions.Add(tr);
        db.Segments.AddRange(
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Text = "Bob, send the report.", Ordinal = 0 });
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        await db.SaveChangesAsync();
        return rec;
    }

    private static FakeSummarizationSettingsResolver Disabled() =>
        new() { Config = new SummarizationRequestConfig("", "", "m", 60) };

    [Fact]
    public async Task Extract_ReplacesActions_SetsFlag_AndReturnsOrderedList()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribed(db, userId);
        var client = new FakeActionsClient
        {
            Result = { new ExtractedAction("Send the report", "Bob", "Friday"),
                       new ExtractedAction("Book the room", "", "") },
        };

        var result = await Build(db, userId, client).Extract(rec.Id);

        var dtos = Assert.IsType<List<RecordingActionDto>>(result.Value);
        Assert.Equal(2, dtos.Count);
        Assert.Equal("Send the report", dtos[0].Text);
        Assert.Equal("Bob", dtos[0].Actor);
        Assert.Equal(new[] { 0, 1 }, dtos.Select(d => d.Ordinal).ToArray());
        Assert.Equal(1, client.Calls);
        Assert.Equal("Alice: Bob, send the report.", client.LastSegments![0].SpeakerDisplay + ": " + client.LastSegments![0].Text);

        var saved = await db.RecordingActions.Where(a => a.RecordingId == rec.Id).ToListAsync();
        Assert.Equal(2, saved.Count);
        Assert.NotNull((await db.Recordings.FindAsync(rec.Id))!.ActionsExtractedAt);
    }

    [Fact]
    public async Task Extract_ReplacesAnyExistingActions()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribed(db, userId);
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Old", Ordinal = 0 });
        await db.SaveChangesAsync();
        var client = new FakeActionsClient { Result = { new ExtractedAction("New only", "", "") } };

        await Build(db, userId, client).Extract(rec.Id);

        var saved = await db.RecordingActions.Where(a => a.RecordingId == rec.Id).ToListAsync();
        var only = Assert.Single(saved);
        Assert.Equal("New only", only.Text);
    }

    [Fact]
    public async Task Extract_NoActionsFound_StillSetsFlag_AndReturnsEmpty()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribed(db, userId);
        var client = new FakeActionsClient(); // empty result

        var result = await Build(db, userId, client).Extract(rec.Id);

        Assert.Empty(Assert.IsType<List<RecordingActionDto>>(result.Value));
        Assert.NotNull((await db.Recordings.FindAsync(rec.Id))!.ActionsExtractedAt);
    }

    [Fact]
    public async Task Extract_NoEndpointConfigured_ReturnsBadRequest_AndDoesNotCallClient()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribed(db, userId);
        var client = new FakeActionsClient { Result = { new ExtractedAction("x", "", "") } };

        var result = await Build(db, userId, client, Disabled()).Extract(rec.Id);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Extract_OtherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribed(db, Guid.NewGuid());

        var result = await Build(db, Guid.NewGuid(), new FakeActionsClient()).Extract(rec.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_AddsAction_TrimsFields_AndAppendsOrdinal()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribed(db, userId);
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "First", Ordinal = 0 });
        await db.SaveChangesAsync();

        var result = await Build(db, userId, new FakeActionsClient())
            .Create(rec.Id, new CreateRecordingActionRequest("  Do thing  ", " Alice ", ""));

        var dto = Assert.IsType<RecordingActionDto>(result.Value);
        Assert.Equal("Do thing", dto.Text);
        Assert.Equal("Alice", dto.Actor);
        Assert.Equal(1, dto.Ordinal);
        Assert.NotNull((await db.Recordings.FindAsync(rec.Id))!.ActionsExtractedAt); // manual add surfaces the panel
    }

    [Fact]
    public async Task Create_OtherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribed(db, Guid.NewGuid());

        var result = await Build(db, Guid.NewGuid(), new FakeActionsClient())
            .Create(rec.Id, new CreateRecordingActionRequest("x", "", ""));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_ChangesProvidedFieldsOnly()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribed(db, userId);
        var a = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Old", Actor = "Bob", Deadline = "Mon", Ordinal = 0 };
        db.RecordingActions.Add(a);
        await db.SaveChangesAsync();

        var result = await Build(db, userId, new FakeActionsClient())
            .Update(rec.Id, a.Id, new UpdateRecordingActionRequest("New text", null, null));

        Assert.IsType<NoContentResult>(result);
        var saved = await db.RecordingActions.FindAsync(a.Id);
        Assert.Equal("New text", saved!.Text);
        Assert.Equal("Bob", saved.Actor); // unchanged (null)
    }

    [Fact]
    public async Task Update_OtherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribed(db, Guid.NewGuid());
        var a = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "x", Ordinal = 0 };
        db.RecordingActions.Add(a);
        await db.SaveChangesAsync();

        var result = await Build(db, Guid.NewGuid(), new FakeActionsClient())
            .Update(rec.Id, a.Id, new UpdateRecordingActionRequest("y", null, null));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Delete_RemovesAction()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribed(db, userId);
        var a = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "x", Ordinal = 0 };
        db.RecordingActions.Add(a);
        await db.SaveChangesAsync();

        var result = await Build(db, userId, new FakeActionsClient()).Delete(rec.Id, a.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await db.RecordingActions.AnyAsync(x => x.Id == a.Id));
    }

    [Fact]
    public async Task List_ReturnsOrdered_ForOwner_NotFoundForOthers()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribed(db, userId);
        db.RecordingActions.AddRange(
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "B", Ordinal = 1 },
            new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "A", Ordinal = 0 });
        await db.SaveChangesAsync();

        var ok = await Build(db, userId, new FakeActionsClient()).List(rec.Id);
        Assert.Equal(new[] { "A", "B" }, Assert.IsType<List<RecordingActionDto>>(ok.Value).Select(d => d.Text).ToArray());

        var other = await Build(db, Guid.NewGuid(), new FakeActionsClient()).List(rec.Id);
        Assert.IsType<NotFoundResult>(other.Result);
    }
}
