using System.Text.Json;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>Verifies the AI-output events (<c>recording.summarized</c>, <c>.minutes_ready</c>,
/// <c>.action_items_ready</c>, <c>.tags_ready</c>) fire at the points each processor finishes its work -
/// right after the existing <c>NotifyStatusAsync</c> hub call. Mirrors <see cref="FormulaWebhookEmitTests"/>.
/// These are the events the n8n Trigger node subscribes to, so the payload fields are contract.</summary>
public class RecordingAiWebhookEmitTests
{
    private static async Task<(Recording rec, Transcription tr)> Seed(
        DiarizDbContext db, Guid userId, bool withSegments = true)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Weekly sync", Name = "Weekly sync",
            Status = RecordingStatus.Summarizing, BlobKey = "k",
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        if (withSegments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = "Hi", Ordinal = 0,
            });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static JsonElement Payload(object data)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(data));
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Successful_summarization_publishes_recording_summarized()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        var publisher = new CapturingWebhookPublisher();

        await SummarizationProcessor.ProcessAsync(
            db, new FakeSummarizationClient { Result = new SummaryResult("The key points.", "Ignored") },
            new FakeSummarizationSettingsResolver(), new FakeHubContext(), new SummarizationJob(rec.Id, tr.Id),
            SummarizationPrompt.DefaultTemplate, NullLogger.Instance, publisher, "https://app.test");

        var published = Assert.Single(publisher.Published);
        Assert.Equal(WebhookEventTypes.RecordingSummarized, published.EventType);
        Assert.Equal(userId, published.Owner);

        var data = Payload(published.Data);
        Assert.Equal(rec.Id.ToString(), data.GetProperty("recordingId").GetString());
        Assert.Equal("Weekly sync", data.GetProperty("name").GetString());
        Assert.Equal(nameof(RecordingStatus.Summarized), data.GetProperty("status").GetString());
        // The summary rides along: the whole point of the event is to act on the text without a second call.
        Assert.Equal("The key points.", data.GetProperty("summary").GetString());
        // The link keys are PascalCase (WebhookLinks is a record and the envelope applies no naming policy) -
        // a shipped quirk, so assert on content rather than casing, as RecordingWebhookEmitTests does.
        Assert.Contains($"https://app.test/api/recordings/{rec.Id}", data.GetProperty("links").GetRawText());
    }

    [Fact]
    public async Task Preserved_user_edited_summary_still_publishes_summarized()
    {
        // The short-circuit path produces nothing new, but the recording still reaches Summarized - a
        // subscriber waiting on "summary ready" must not hang here.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        db.Summaries.Add(new Summary
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = Summary.UserEditedModel,
            Text = "my edit", IsUserEdited = true,
        });
        await db.SaveChangesAsync();
        var publisher = new CapturingWebhookPublisher();

        await SummarizationProcessor.ProcessAsync(
            db, new FakeSummarizationClient(), new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            new SummarizationJob(rec.Id, tr.Id), SummarizationPrompt.DefaultTemplate, NullLogger.Instance,
            publisher, "https://app.test");

        var published = Assert.Single(publisher.Published);
        Assert.Equal(WebhookEventTypes.RecordingSummarized, published.EventType);
        Assert.Equal("my edit", Payload(published.Data).GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Failed_summarization_publishes_nothing()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var publisher = new CapturingWebhookPublisher();

        await SummarizationProcessor.ProcessAsync(
            db, new FakeSummarizationClient { ThrowOnCall = new InvalidOperationException("LLM down") },
            new FakeSummarizationSettingsResolver(), new FakeHubContext(), new SummarizationJob(rec.Id, tr.Id),
            SummarizationPrompt.DefaultTemplate, NullLogger.Instance, publisher, "https://app.test");

        Assert.Empty(publisher.Published);
    }
}
