using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>The OCR endpoint on a capture.
///
/// The ownership assertions matter as much as the extraction ones: a screenshot id paired with the wrong
/// recording is answered with 404 rather than skipped, so a caller cannot probe which ids exist by
/// watching what comes back. That is the same rule the chat attachment path follows.</summary>
public class ScreenshotOcrTests
{
    private static (ScreenshotsController Controller, DiarizDbContext Db, FakeOcrClient Ocr,
        FakeLlmSettingsResolver Settings, Guid UserId, Guid RecordingId, Guid ShotId) Setup(
        bool ocrConfigured = true)
    {
        var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var shotId = Guid.NewGuid();

        db.Users.Add(new ApplicationUser { Id = userId, Email = "a@b.c", UserName = "a@b.c", QuotaBytes = 1 << 20 });
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t" });
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = shotId, UserId = userId, RecordingId = recordingId,
            BlobKey = "k.png", ThumbBlobKey = "k.jpg", Width = 2560, Height = 1697, CapturedAtMs = 61_000,
        });
        db.SaveChanges();

        var settings = new FakeLlmSettingsResolver
        {
            Config = new LlmRequestConfig(
                ocrConfigured ? "http://lmstudio.local/v1" : "",
                "", "olmocr-2-7b-1025",
                new LlmParameters { OcrPrompt = "Text Recognition:", OcrMaxEdge = 2048 }),
        };
        var ocr = new FakeOcrClient("Extracted text");

        var controller = new ScreenshotsController(
            db, new FakeAudioStorage(), new StorageUsage(db), Options.Create(new ScreenshotOptions()),
            new RoomScope(db), settings, ocr, new FakeOcrImageEncoder())
        {
            ControllerContext = Http.Context(userId),
        };
        return (controller, db, ocr, settings, userId, recordingId, shotId);
    }

    [Fact]
    public async Task Ocr_ReturnsTheExtractedTextAndTheModelThatProducedIt()
    {
        var (controller, _, _, _, _, recordingId, shotId) = Setup();

        var result = await controller.Ocr(recordingId, shotId, force: false, default);

        var dto = Assert.IsType<ScreenshotOcrDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Extracted text", dto.Text);
        Assert.Equal("olmocr-2-7b-1025", dto.Model);
        Assert.Equal("Extracted text".Length, dto.Chars);
        Assert.False(dto.Cached);
    }

    /// <summary>Attribution: an OCR pass spends tokens, so it must resolve as its own call kind and land in
    /// the usage log like every other model call.</summary>
    [Fact]
    public async Task Ocr_ResolvesTheScreenshotOcrCallKind()
    {
        var (controller, _, _, settings, _, recordingId, shotId) = Setup();

        await controller.Ocr(recordingId, shotId, force: false, default);

        Assert.Equal(LlmCallKind.ScreenshotOcr, settings.LastKind);
    }

    [Fact]
    public async Task Ocr_PersistsTheResultOnTheCapture()
    {
        var (controller, db, _, _, _, recordingId, shotId) = Setup();

        await controller.Ocr(recordingId, shotId, force: false, default);

        var row = await db.MeetingScreenshots.SingleAsync();
        Assert.Equal("Extracted text", row.OcrText);
        Assert.Equal("olmocr-2-7b-1025", row.OcrModel);
        Assert.NotNull(row.OcrGeneratedAt);
    }

    /// <summary>The second button is free after the first: a stored result is returned without a model call
    /// at all, which is the whole point of caching it.</summary>
    [Fact]
    public async Task Ocr_WithAStoredResult_ReturnsItWithoutCallingTheModel()
    {
        var (controller, db, ocr, _, _, recordingId, shotId) = Setup();
        var shot = await db.MeetingScreenshots.SingleAsync();
        shot.OcrText = "Previously extracted";
        shot.OcrModel = "glm-ocr";
        shot.OcrGeneratedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var result = await controller.Ocr(recordingId, shotId, force: false, default);

        var dto = Assert.IsType<ScreenshotOcrDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Previously extracted", dto.Text);
        Assert.True(dto.Cached);
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task Ocr_WithForce_ReExtractsAndOverwrites()
    {
        var (controller, db, ocr, _, _, recordingId, shotId) = Setup();
        var shot = await db.MeetingScreenshots.SingleAsync();
        shot.OcrText = "Stale";
        shot.OcrGeneratedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var result = await controller.Ocr(recordingId, shotId, force: true, default);

        var dto = Assert.IsType<ScreenshotOcrDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("Extracted text", dto.Text);
        Assert.False(dto.Cached);
        Assert.Equal(1, ocr.Calls);
        Assert.Equal("Extracted text", (await db.MeetingScreenshots.SingleAsync()).OcrText);
    }

    /// <summary>The cap and prompt reach the client as resolved, because they are what an administrator
    /// measured for that model - four models each wanted a different cap.</summary>
    [Fact]
    public async Task Ocr_PassesTheResolvedPromptThroughToTheClient()
    {
        var (controller, _, ocr, _, _, recordingId, shotId) = Setup();

        await controller.Ocr(recordingId, shotId, force: false, default);

        Assert.Equal("Text Recognition:", ocr.LastConfig!.Parameters.OcrPrompt);
        Assert.Equal(2048, ocr.LastConfig.Parameters.OcrMaxEdge);
    }

    [Fact]
    public async Task Ocr_WithNoOcrModelRouted_ReturnsBadRequest()
    {
        var (controller, _, ocr, _, _, recordingId, shotId) = Setup(ocrConfigured: false);

        var result = await controller.Ocr(recordingId, shotId, force: false, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task Ocr_ForAnotherUsersRecording_ReturnsNotFound()
    {
        var (controller, db, ocr, _, _, _, _) = Setup();
        var otherRecording = Guid.NewGuid();
        var otherShot = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = stranger, Email = "x@y.z", UserName = "x@y.z" });
        db.Recordings.Add(new Recording { Id = otherRecording, UserId = stranger, Title = "theirs" });
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = otherShot, UserId = stranger, RecordingId = otherRecording,
            BlobKey = "o.png", ThumbBlobKey = "o.jpg", Width = 100, Height = 100,
        });
        await db.SaveChangesAsync();

        var result = await controller.Ocr(otherRecording, otherShot, force: false, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, ocr.Calls);
    }

    /// <summary>A real screenshot id paired with the wrong recording is a 404, not a skip - skipping would
    /// let a caller enumerate which capture ids exist.</summary>
    [Fact]
    public async Task Ocr_WithAShotFromAnotherRecording_ReturnsNotFound()
    {
        var (controller, db, ocr, _, userId, recordingId, _) = Setup();
        var second = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = second, UserId = userId, Title = "second" });
        await db.SaveChangesAsync();

        var strayShot = Guid.NewGuid();
        db.MeetingScreenshots.Add(new MeetingScreenshot
        {
            Id = strayShot, UserId = userId, RecordingId = second,
            BlobKey = "s.png", ThumbBlobKey = "s.jpg", Width = 100, Height = 100,
        });
        await db.SaveChangesAsync();

        var result = await controller.Ocr(recordingId, strayShot, force: false, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, ocr.Calls);
    }

    /// <summary>A model that returns nothing must not be written over a good previous result, and must not
    /// be reported as a success with empty text.</summary>
    [Fact]
    public async Task Ocr_WhenTheModelReturnsNothing_DoesNotStoreAnEmptyResult()
    {
        var (controller, db, ocr, _, _, recordingId, shotId) = Setup();
        ocr.Text = "   ";

        var result = await controller.Ocr(recordingId, shotId, force: false, default);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Null((await db.MeetingScreenshots.SingleAsync()).OcrText);
    }
}

/// <summary>The availability probe the capture viewer reads before it decides whether to draw the extract
/// buttons. Offering an action that always 400s is worse than not offering it.</summary>
public class OcrStatusTests
{
    private static OcrController Build(string apiBase, string model = "olmocr-2-7b-1025") =>
        new(new FakeLlmSettingsResolver
        {
            Config = new LlmRequestConfig(apiBase, "", model, new LlmParameters()),
        })
        {
            ControllerContext = Http.Context(Guid.NewGuid()),
        };

    [Fact]
    public async Task Status_WithARoutedModel_ReportsEnabledAndNamesIt()
    {
        var dto = (await Build("http://lmstudio.local/v1").Status(default)).Value;

        Assert.NotNull(dto);
        Assert.True(dto!.Enabled);
        Assert.Equal("olmocr-2-7b-1025", dto.Model);
    }

    /// <summary>The model name is withheld when nothing is routed: reporting the name of a model that will
    /// not run would put a false provenance line in front of a user.</summary>
    [Fact]
    public async Task Status_WithNothingRouted_ReportsDisabledAndNoModel()
    {
        var dto = (await Build("").Status(default)).Value;

        Assert.NotNull(dto);
        Assert.False(dto!.Enabled);
        Assert.Null(dto.Model);
    }
}
