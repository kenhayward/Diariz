using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>Verifies the <c>recording.*</c> webhook events actually fire at the SignalR notify call-sites -
/// <see cref="WorkerCallbackController"/>'s result/failure callbacks and <see cref="RecordingsController"/>'s
/// Upload. The publisher itself (Task 4) is tested separately; here we only assert the controllers call it
/// with the right event type, owner, and thin data shape.</summary>
public class RecordingWebhookEmitTests
{
    private const string Secret = "shared-secret";

    // ---- WorkerCallbackController ----

    private static (WorkerCallbackController controller, DiarizDbContext db, CapturingWebhookPublisher publisher)
        BuildWorkerCallback(bool summarizationEnabled = false)
    {
        var db = TestDb.Create();
        var hub = new FakeHubContext();
        var queue = new FakeJobQueue();
        var summaryOpts = new SummarizationOptions { ApiBase = summarizationEnabled ? "http://llm.test/v1" : "" };
        var resolver = new SummarizationSettingsResolver(db, Options.Create(summaryOpts), new FakeApiKeyProtector());
        var embedding = new EmbeddingSettingsResolver(
            db, Options.Create(new EmbeddingOptions()), Options.Create(summaryOpts), new FakeApiKeyProtector());
        var publisher = new CapturingWebhookPublisher();
        var controller = new WorkerCallbackController(
            db, hub, queue, resolver, embedding, new FakeSpeakerIdentifier(),
            Options.Create(new WorkerOptions { CallbackSecret = Secret }),
            publisher, Options.Create(new AppPublicOptions()))
        {
            ControllerContext = Http.Context(headers: ("X-Worker-Secret", Secret))
        };
        return (controller, db, publisher);
    }

    private static async Task<(Guid recordingId, Guid transcriptionId)> SeedQueuedRecording(DiarizDbContext db, Guid userId)
    {
        Users.Ensure(db, userId);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "Standup", Status = RecordingStatus.Queued };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx-large-v3", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        await db.SaveChangesAsync();
        return (rec.Id, tr.Id);
    }

    [Fact]
    public async Task Transcription_complete_publishes_recording_transcribed()
    {
        var (controller, db, publisher) = BuildWorkerCallback();
        var ownerId = Guid.NewGuid();
        var (recordingId, transcriptionId) = await SeedQueuedRecording(db, ownerId);

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
            [new SegmentResult("SPEAKER_00", 0, 1000, "Hello")], DurationMs: 42_000));

        Assert.Contains(publisher.Published, p =>
            p.EventType == WebhookEventTypes.RecordingTranscribed && p.Owner == ownerId);

        // Assert the external `data` contract, not just the event type/owner - a caller-facing shape regression
        // (a renamed or dropped field) would otherwise slip past every test in this file.
        var (_, _, data) = publisher.Published.Single(p => p.EventType == WebhookEventTypes.RecordingTranscribed);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(recordingId.ToString(), doc.RootElement.GetProperty("recordingId").GetString());
        Assert.Equal("Standup", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(nameof(RecordingStatus.Transcribed), doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(42_000, doc.RootElement.GetProperty("durationMs").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.GetProperty("links").ValueKind);
        Assert.Contains(recordingId.ToString(), doc.RootElement.GetProperty("links").GetRawText());
    }

    [Fact]
    public async Task Transcription_complete_whenSummarizing_stillPublishesRecordingTranscribed()
    {
        var (controller, db, publisher) = BuildWorkerCallback(summarizationEnabled: true);
        var ownerId = Guid.NewGuid();
        var (recordingId, transcriptionId) = await SeedQueuedRecording(db, ownerId);

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
            [new SegmentResult("SPEAKER_00", 0, 1000, "Hello")]));

        var rec = await db.Recordings.FindAsync(recordingId);
        Assert.Equal(RecordingStatus.Summarizing, rec!.Status);
        Assert.Contains(publisher.Published, p =>
            p.EventType == WebhookEventTypes.RecordingTranscribed && p.Owner == ownerId);
    }

    [Fact]
    public async Task Failure_publishes_recording_transcription_failed()
    {
        var (controller, db, publisher) = BuildWorkerCallback();
        var ownerId = Guid.NewGuid();
        var (_, transcriptionId) = await SeedQueuedRecording(db, ownerId);

        await controller.Failure(new TranscriptionFailure(transcriptionId, "model exploded"));

        Assert.Contains(publisher.Published, p =>
            p.EventType == WebhookEventTypes.RecordingTranscriptionFailed && p.Owner == ownerId);
    }

    // ---- RecordingsController ----

    private static RecordingsController BuildRecordings(DiarizDbContext db, Guid userId, out CapturingWebhookPublisher publisher)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transcription:DefaultModel"] = "whisperx-large-v3" })
            .Build();
        var resolver = new SummarizationSettingsResolver(
            db, Options.Create(new SummarizationOptions()), new FakeApiKeyProtector());
        publisher = new CapturingWebhookPublisher();
        return new RecordingsController(db, new FakeAudioStorage(), new FakeJobQueue(), new FakeHubContext(), config,
            resolver, new FakeEmailSender(), new FakeSpeakerIdentifier(), Options.Create(new UploadOptions()),
            new RoomScope(db), publisher, Options.Create(new AppPublicOptions()))
        {
            ControllerContext = Http.Context(userId)
        };
    }

    private static FormFile FakeAudio(byte[]? bytes = null, string fileName = "recording.webm", string contentType = "audio/webm")
    {
        bytes ??= Encoding.UTF8.GetBytes("pretend-audio");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "audio", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    [Fact]
    public async Task Upload_publishes_recording_created()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var controller = BuildRecordings(db, userId, out var publisher);

        var result = await controller.Upload(FakeAudio(), title: "Standup", durationMs: 1000);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Contains(publisher.Published, p =>
            p.EventType == WebhookEventTypes.RecordingCreated && p.Owner == userId);

        var (_, _, data) = publisher.Published.Single(p => p.EventType == WebhookEventTypes.RecordingCreated);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var recordingId = doc.RootElement.GetProperty("recordingId").GetString();
        Assert.False(string.IsNullOrEmpty(recordingId));
        Assert.Equal("Standup", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(nameof(RecordingStatus.Queued), doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.GetProperty("links").ValueKind);
        Assert.Contains(recordingId!, doc.RootElement.GetProperty("links").GetRawText());
    }
}
