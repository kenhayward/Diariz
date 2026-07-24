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

    [Fact]
    public async Task Successful_minutes_generation_publishes_recording_minutes_ready()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        db.MeetingTypes.Add(new MeetingType
        {
            Id = typeId, GroupName = "Standard", Title = "Customer Call", Overview = "o", Icon = "i", Color = "#fff",
        });
        var (rec, tr) = await Seed(db, userId);
        rec.MeetingTypeId = typeId;
        await db.SaveChangesAsync();
        var publisher = new CapturingWebhookPublisher();

        await MeetingMinutesProcessor.ProcessAsync(
            db, new FakeMeetingTypeMinutesGenerator { Result = "# Cadence Call\n\nMinutes." },
            new FakeSummarizationSettingsResolver(), new FakeHubContext(), new FakeJobQueue(),
            new MeetingMinutesJob(rec.Id, tr.Id), charBudget: 16000, NullLogger.Instance, publisher, "https://app.test");

        var published = Assert.Single(publisher.Published);
        Assert.Equal(WebhookEventTypes.RecordingMinutesReady, published.EventType);
        Assert.Equal(userId, published.Owner);

        var data = Payload(published.Data);
        Assert.Equal(rec.Id.ToString(), data.GetProperty("recordingId").GetString());
        Assert.Equal("Weekly sync", data.GetProperty("name").GetString());
        Assert.Equal(nameof(RecordingStatus.Summarizing), data.GetProperty("status").GetString());
        Assert.Equal("# Cadence Call\n\nMinutes.", data.GetProperty("minutes").GetString());
        Assert.Equal(typeId.ToString(), data.GetProperty("meetingTypeId").GetString());
        Assert.Equal("Customer Call", data.GetProperty("meetingTypeName").GetString());
        Assert.Contains($"https://app.test/api/recordings/{rec.Id}", data.GetProperty("links").GetRawText());
    }

    [Fact]
    public async Task Failed_minutes_generation_publishes_nothing()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var publisher = new CapturingWebhookPublisher();

        await MeetingMinutesProcessor.ProcessAsync(
            db, new FakeMeetingTypeMinutesGenerator { ThrowOnCall = new InvalidOperationException("LLM down") },
            new FakeSummarizationSettingsResolver(), new FakeHubContext(), new FakeJobQueue(),
            new MeetingMinutesJob(rec.Id, tr.Id), 16000, NullLogger.Instance, publisher, "https://app.test");

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Successful_action_extraction_publishes_recording_action_items_ready()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        var client = new FakeActionsClient { Result = { new ExtractedAction("Send the report", "Bob", "Friday") } };
        var publisher = new CapturingWebhookPublisher();

        await ActionsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), new FakeJobQueue(),
            new ActionsJob(rec.Id, tr.Id), ActionsPrompt.DefaultTemplate, NullLogger.Instance, publisher,
            "https://app.test");

        var published = Assert.Single(publisher.Published);
        Assert.Equal(WebhookEventTypes.RecordingActionItemsReady, published.EventType);
        Assert.Equal(userId, published.Owner);

        var data = Payload(published.Data);
        Assert.Equal(rec.Id.ToString(), data.GetProperty("recordingId").GetString());
        Assert.Equal("Weekly sync", data.GetProperty("name").GetString());
        Assert.Equal(nameof(RecordingStatus.Summarizing), data.GetProperty("status").GetString());
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        var item = Assert.Single(data.GetProperty("actionItems").EnumerateArray());
        Assert.Equal("Send the report", item.GetProperty("text").GetString());
        Assert.Equal("Bob", item.GetProperty("assignee").GetString());
        Assert.Equal("Friday", item.GetProperty("dueDate").GetString());
        Assert.False(item.GetProperty("completed").GetBoolean());
        Assert.Contains($"https://app.test/api/recordings/{rec.Id}", data.GetProperty("links").GetRawText());
    }

    [Fact]
    public async Task Failed_action_extraction_publishes_nothing()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeActionsClient { ThrowOnCall = new InvalidOperationException("LLM down") };
        var publisher = new CapturingWebhookPublisher();

        await ActionsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), new FakeJobQueue(),
            new ActionsJob(rec.Id, tr.Id), ActionsPrompt.DefaultTemplate, NullLogger.Instance, publisher,
            "https://app.test");

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Successful_tag_extraction_publishes_recording_tags_ready()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        var client = new FakeTagsClient { Result = { new ExtractedTag("Budget Planning", 0.9) } };
        var publisher = new CapturingWebhookPublisher();

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), new TagsJob(rec.Id, tr.Id),
            TagsPrompt.DefaultTemplate, NullLogger.Instance, publisher, "https://app.test");

        var published = Assert.Single(publisher.Published);
        Assert.Equal(WebhookEventTypes.RecordingTagsReady, published.EventType);
        Assert.Equal(userId, published.Owner);

        var data = Payload(published.Data);
        Assert.Equal(rec.Id.ToString(), data.GetProperty("recordingId").GetString());
        Assert.Equal("Weekly sync", data.GetProperty("name").GetString());
        Assert.Equal(nameof(RecordingStatus.Summarizing), data.GetProperty("status").GetString());
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        var tag = Assert.Single(data.GetProperty("tags").EnumerateArray());
        Assert.Equal("Budget Planning", tag.GetProperty("name").GetString());
        Assert.Equal(0.9, tag.GetProperty("weight").GetDouble(), 3);
        Assert.Contains($"https://app.test/api/recordings/{rec.Id}", data.GetProperty("links").GetRawText());
    }

    [Fact]
    public async Task Failed_tag_extraction_publishes_nothing()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeTagsClient { ThrowOnCall = new InvalidOperationException("LLM down") };
        var publisher = new CapturingWebhookPublisher();

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), new TagsJob(rec.Id, tr.Id),
            TagsPrompt.DefaultTemplate, NullLogger.Instance, publisher, "https://app.test");

        Assert.Empty(publisher.Published);
    }
}
