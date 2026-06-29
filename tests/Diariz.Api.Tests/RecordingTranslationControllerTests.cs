using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class RecordingTranslationControllerTests
{
    private static RecordingTranslationController Build(
        DiarizDbContext db, Guid userId, ITranslationClient client, ISummarizationSettingsResolver? settings = null) =>
        new(db, client, settings ?? new FakeSummarizationSettingsResolver())
        { ControllerContext = Http.Context(userId) };

    private static FakeSummarizationSettingsResolver Disabled() =>
        new() { Config = new SummarizationRequestConfig("", "", "m", 60) };

    private static async Task<(Recording rec, Guid segId)> SeedTranscribed(
        DiarizDbContext db, Guid userId, bool withSummary = true, bool withAction = true)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", Title = "T" };
        db.Recordings.Add(rec);
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Transcriptions.Add(tr);
        var seg = new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "Hello", Ordinal = 0 };
        db.Segments.AddRange(seg,
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 1000, EndMs = 2000, Original = "World", Ordinal = 1 });
        if (withSummary)
            db.Summaries.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "gpt", Text = "A summary." });
        if (withAction)
            db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Send it", Actor = "Bob", Deadline = "Friday", Ordinal = 0 });
        await db.SaveChangesAsync();
        return (rec, seg.Id);
    }

    [Fact]
    public async Task TranslateRecording_FillsRevised_TranslatesSummaryAndActions_KeepsOriginalAndActor()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedTranscribed(db, userId);
        var client = new FakeTranslationClient();

        var result = await Build(db, userId, client).TranslateRecording(rec.Id, new TranslateRequest("fr"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("French", client.LastLanguage); // resolved the code → English name for the prompt

        var segs = await db.Segments.OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal("Hello", segs[0].Original);            // original preserved
        Assert.Equal("[French] Hello", segs[0].Revised);    // translation lands in Revised
        Assert.Equal("[French] World", segs[1].Revised);

        var tr = await db.Transcriptions.SingleAsync();
        Assert.Equal("[French] A summary.", (await db.Summaries.SingleAsync(s => s.TranscriptionId == tr.Id)).Text);

        var action = await db.RecordingActions.SingleAsync();
        Assert.Equal("[French] Send it", action.Text);
        Assert.Equal("[French] Friday", action.Deadline);
        Assert.Equal("Bob", action.Actor); // actor (a name) is left untranslated
    }

    [Fact]
    public async Task TranslateRecording_UsesNativeLanguage_WhenRequestOmitsIt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedTranscribed(db, userId, withSummary: false, withAction: false);
        db.UserSettings.Add(new UserSettings { UserId = userId, NativeLanguage = "de" });
        await db.SaveChangesAsync();
        var client = new FakeTranslationClient();

        var result = await Build(db, userId, client).TranslateRecording(rec.Id, new TranslateRequest());

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("German", client.LastLanguage);
    }

    [Fact]
    public async Task TranslateRecording_NoLanguageAndNoNative_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedTranscribed(db, userId);

        var result = await Build(db, userId, new FakeTranslationClient()).TranslateRecording(rec.Id, new TranslateRequest());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TranslateRecording_UnknownLanguage_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedTranscribed(db, userId);

        var result = await Build(db, userId, new FakeTranslationClient()).TranslateRecording(rec.Id, new TranslateRequest("klingon"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TranslateRecording_NoLlmEndpoint_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedTranscribed(db, userId);

        var result = await Build(db, userId, new FakeTranslationClient(), Disabled())
            .TranslateRecording(rec.Id, new TranslateRequest("es"));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TranslateRecording_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var (rec, _) = await SeedTranscribed(db, Guid.NewGuid());

        var result = await Build(db, Guid.NewGuid(), new FakeTranslationClient()).TranslateRecording(rec.Id, new TranslateRequest("es"));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task TranslateSegment_SetsRevised_PreservingOriginal()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, segId) = await SeedTranscribed(db, userId, withSummary: false, withAction: false);

        var result = await Build(db, userId, new FakeTranslationClient()).TranslateSegment(rec.Id, segId, new TranslateRequest("es"));

        Assert.IsType<NoContentResult>(result);
        var seg = await db.Segments.FindAsync(segId);
        Assert.Equal("Hello", seg!.Original);
        Assert.Equal("[Spanish] Hello", seg.Revised);
    }

    [Fact]
    public async Task TranslateSegment_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var (rec, segId) = await SeedTranscribed(db, Guid.NewGuid(), withSummary: false, withAction: false);

        var result = await Build(db, Guid.NewGuid(), new FakeTranslationClient()).TranslateSegment(rec.Id, segId, new TranslateRequest("es"));

        Assert.IsType<NotFoundResult>(result);
    }
}
