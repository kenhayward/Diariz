using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

public class TagsProcessorTests
{
    private static readonly string Template = TagsPrompt.DefaultTemplate;

    private static async Task<(Recording rec, Transcription tr)> Seed(
        DiarizDbContext db, Guid userId, bool withSegments = true,
        RecordingStatus status = RecordingStatus.Summarized)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Name = "Named", Status = status, BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        if (withSegments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = "Let's finalise the Q3 budget allocation.", Ordinal = 0
            });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static TagsJob Job(Recording rec, Transcription tr) => new(rec.Id, tr.Id);

    [Fact]
    public async Task ProcessAsync_ExtractsTags_MarksExtracted_ReusesConfig_Notifies()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);
        var client = new FakeTagsClient
        {
            Result = { new ExtractedTag("Budget Planning", 0.9), new ExtractedTag("Vendor Selection", 0.4) }
        };
        var resolver = new FakeSummarizationSettingsResolver();
        var hub = new FakeHubContext();

        await TagsProcessor.ProcessAsync(db, client, resolver, hub, Job(rec, tr), Template, NullLogger.Instance,
            new CapturingWebhookPublisher(), "");

        var tags = await db.RecordingTags.Where(t => t.RecordingId == rec.Id).OrderBy(t => t.Ordinal).ToListAsync();
        Assert.Equal(2, tags.Count);
        Assert.Equal("Budget-Planning", tags[0].Tag);   // stored normalised, not the LLM's raw spaced text
        Assert.Equal(0.9, tags[0].Weight, 3);
        Assert.Equal(0, tags[0].Ordinal);
        Assert.Equal("Vendor-Selection", tags[1].Tag);
        Assert.Equal(1, tags[1].Ordinal);
        Assert.Equal(userId, resolver.LastUserId);        // resolved for the owner
        Assert.Equal(resolver.Config, client.LastConfig); // passed straight to the client

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.NotNull(reloaded!.TagsExtractedAt);
        Assert.Equal(RecordingStatus.Summarized, reloaded.Status); // status untouched (tags never own status)

        var msg = Assert.Single(hub.Sent);
        Assert.Equal(userId.ToString(), msg.Group);
        Assert.Equal("RecordingStatusChanged", msg.Method);
    }

    [Fact]
    public async Task ProcessAsync_AttributesTheCall_ToTheRecordingAndItsOwner()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId);

        LlmCallKind? observedKind = null;
        Guid? observedRecording = null;
        Guid? observedUser = null;
        var client = new FakeTagsClient(onCall: () =>
        {
            observedKind = LlmCallScope.Active?.Kind;
            observedRecording = LlmCallScope.Active?.RecordingId;
            observedUser = LlmCallScope.Active?.UserId;
        });

        await TagsProcessor.ProcessAsync(db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            Job(rec, tr), Template, NullLogger.Instance, new CapturingWebhookPublisher(), "");

        Assert.Equal(LlmCallKind.Tags, observedKind);
        Assert.Equal(rec.Id, observedRecording);
        Assert.Equal(rec.UserId, observedUser);
    }

    [Fact]
    public async Task ProcessAsync_ReplacesExistingSuggestion()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Old Topic", Weight = 0.5, Ordinal = 0,
        });
        rec.TagsExtractedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        var client = new FakeTagsClient { Result = { new ExtractedTag("Fresh Topic", 0.8) } };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal("Fresh-Topic", tag.Tag); // a re-run replaces the still-suggested set (adopted/dismissed rows survive - see below); stored normalised
    }

    [Fact]
    public async Task ProcessAsync_InsertsTagsAsSuggested_WithNoAdoptedAt()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeTagsClient { Result = { new ExtractedTag("Roadmap", 0.9) } };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal(RecordingTagStatus.Suggested, tag.Status);
        Assert.Null(tag.AdoptedAt);
    }

    [Fact]
    public async Task ProcessAsync_ReplacesSuggestions_ButKeepsAdoptedAndDismissed()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        db.RecordingTags.AddRange(
            new RecordingTag { Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "stale-suggestion", Weight = 0.5, Ordinal = 0 },
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "my-tag", Weight = 1.0, Ordinal = 1,
                Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
            },
            new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "never-again", Weight = 0.4, Ordinal = 2,
                Status = RecordingTagStatus.Dismissed,
            });
        await db.SaveChangesAsync();
        var client = new FakeTagsClient { Result = { new ExtractedTag("Fresh", 0.7) } };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var byStatus = (await db.RecordingTags.Where(t => t.RecordingId == rec.Id).ToListAsync())
            .ToDictionary(t => t.Tag, t => t.Status);
        Assert.False(byStatus.ContainsKey("stale-suggestion"));             // replaced
        Assert.Equal(RecordingTagStatus.Adopted, byStatus["my-tag"]);       // survived
        Assert.Equal(RecordingTagStatus.Dismissed, byStatus["never-again"]); // survived
        Assert.Equal(RecordingTagStatus.Suggested, byStatus["Fresh"]);      // added
    }

    [Fact]
    public async Task ProcessAsync_DoesNotResuggest_AnAlreadyAdoptedTag_EvenInADifferentCase()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "metadata", Weight = 1.0, Ordinal = 0,
            Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var client = new FakeTagsClient { Result = { new ExtractedTag("Metadata", 0.9) } };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal("metadata", tag.Tag);
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotResuggest_AnAdoptedHyphenatedTag_WhenTheLlmReturnsTheSpacedVariant()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        // Adopted via RecordingsController.AddTag, which always rewrites the row to TagText.Normalize's
        // hyphenated form - so a real adopted tag looks like this, never like the LLM's raw Title Case.
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Data-Collection", Weight = 1.0, Ordinal = 0,
            Status = RecordingTagStatus.Adopted, AdoptedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        // The LLM returns its own raw, spaced spelling of the very same concept.
        var client = new FakeTagsClient { Result = { new ExtractedTag("Data Collection", 0.9) } };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        // A raw-text comparison would miss that these are the same tag and re-suggest "Data Collection"
        // alongside the adopted "Data-Collection". Comparing normalised forms recognises them as one tag.
        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal("Data-Collection", tag.Tag);
        Assert.Equal(RecordingTagStatus.Adopted, tag.Status);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotResuggest_ADismissedHyphenatedTag_WhenTheLlmReturnsTheSpacedVariant()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        // Dismissed via RecordingsController.DismissTag matching against the normalised form, but the row
        // itself keeps whatever text it had when it was Suggested - here, already hyphenated (e.g. it was
        // itself deduped-and-normalised by a previous extraction run).
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Data-Collection", Weight = 0.5, Ordinal = 0,
            Status = RecordingTagStatus.Dismissed,
        });
        await db.SaveChangesAsync();
        // The LLM returns its own raw, spaced spelling of the very same concept.
        var client = new FakeTagsClient { Result = { new ExtractedTag("Data Collection", 0.9) } };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        // A raw-text comparison would miss that these are the same tag and resurrect "Data Collection" as a
        // fresh suggestion right next to its own dismissal. Comparing normalised forms stops that.
        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal("Data-Collection", tag.Tag);
        Assert.Equal(RecordingTagStatus.Dismissed, tag.Status);
    }

    [Fact]
    public async Task ProcessAsync_DedupesCandidatesThatNormaliseToTheSameTag()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        // TagsPrompt.ParseResponse only dedupes the raw LLM text (case-insensitive), so a response
        // containing both spellings of the same concept survives that step as two distinct candidates.
        var client = new FakeTagsClient
        {
            Result = { new ExtractedTag("Data Collection", 0.9), new ExtractedTag("Data-Collection", 0.5) }
        };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        // Both candidates normalise to "Data-Collection" - inserting both would slip past the unique index
        // (their raw lower-case forms differ) and show up as a duplicated hint. Exactly one row survives.
        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal("Data-Collection", tag.Tag);
        Assert.Equal(RecordingTagStatus.Suggested, tag.Status);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotResuggest_ADismissedTag_EvenInADifferentCase()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Boilerplate", Weight = 0.3, Ordinal = 0,
            Status = RecordingTagStatus.Dismissed,
        });
        await db.SaveChangesAsync();
        var client = new FakeTagsClient { Result = { new ExtractedTag("boilerplate", 0.9) } };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal(RecordingTagStatus.Dismissed, tag.Status);
    }

    [Fact]
    public async Task ProcessAsync_SkipsWhenLlmNotConfigured_LeavesMarkerNull_ForBackfillRetry()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeTagsClient { Result = { new ExtractedTag("x", 0.5) } };
        // Empty ApiBase => Config.Enabled is false (no LLM endpoint at user or server level).
        var resolver = new FakeSummarizationSettingsResolver { Config = new SummarizationRequestConfig("", "", "", 30) };
        var hub = new FakeHubContext();

        await TagsProcessor.ProcessAsync(db, client, resolver, hub, Job(rec, tr), Template, NullLogger.Instance,
            new CapturingWebhookPublisher(), "");

        Assert.Equal(0, client.Calls);
        Assert.Empty(await db.RecordingTags.ToListAsync());
        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.TagsExtractedAt); // backfill can retry later
        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task ProcessAsync_ZeroTagsFound_StillMarksExtracted_AndNotifies()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var client = new FakeTagsClient(); // empty result: thin transcript
        var hub = new FakeHubContext();

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), hub, Job(rec, tr), Template, NullLogger.Instance,
            new CapturingWebhookPublisher(), "");

        Assert.Empty(await db.RecordingTags.ToListAsync());
        Assert.NotNull((await db.Recordings.FindAsync(rec.Id))!.TagsExtractedAt); // done, not retry-forever
        Assert.Single(hub.Sent);
    }

    [Fact]
    public async Task ProcessAsync_RecordingDeleted_ReturnsQuietly()
    {
        using var db = TestDb.Create();
        var client = new FakeTagsClient();

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            new TagsJob(Guid.NewGuid(), Guid.NewGuid()), Template, NullLogger.Instance,
            new CapturingWebhookPublisher(), "");

        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ProcessAsync_NoSegments_DoesNothing_LeavesMarkerNull()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), withSegments: false);
        var client = new FakeTagsClient();

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        Assert.Equal(0, client.Calls);
        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.TagsExtractedAt);
    }

    [Fact]
    public async Task ProcessAsync_OnClientError_PreservesExistingTags_AndMarker()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var previously = DateTimeOffset.UtcNow.AddDays(-1);
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Kept Topic", Weight = 0.6, Ordinal = 0,
        });
        rec.TagsExtractedAt = previously;
        await db.SaveChangesAsync();
        var client = new FakeTagsClient { ThrowOnCall = new InvalidOperationException("LLM down") };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, tr), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        // A failed re-extract must not wipe the previous tags (RemoveRange only after a successful call).
        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal("Kept Topic", tag.Tag);
        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(previously, reloaded!.TagsExtractedAt);
        Assert.Equal(RecordingStatus.Summarized, reloaded.Status); // never marked Failed
    }

    [Fact]
    public async Task ProcessAsync_StaleTranscription_NoOps_PreservingNewerTags()
    {
        using var db = TestDb.Create();
        var (rec, v1) = await Seed(db, Guid.NewGuid());
        // A re-transcribe has since produced v2 whose tags are already in place.
        db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 2 });
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Newer Topic", Weight = 0.7, Ordinal = 0,
        });
        await db.SaveChangesAsync();
        var client = new FakeTagsClient { Result = { new ExtractedTag("Stale Topic", 0.9) } };

        await TagsProcessor.ProcessAsync(
            db, client, new FakeSummarizationSettingsResolver(), new FakeHubContext(), Job(rec, v1), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "");

        Assert.Equal(0, client.Calls); // a slow/backfilled v1 job must not overwrite v2's tags
        var tag = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal("Newer Topic", tag.Tag);
    }
}
