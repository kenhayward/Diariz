using Diariz.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>
/// The gate that keeps a half-finished transcript out of everything downstream of it. One test per row
/// of the table in spec section 7.2.
/// <para>
/// This exists <b>before</b> anything can create a provisional transcription, deliberately: adding the
/// producer first would leave a window in which summaries, actions, tags and embeddings all fire on
/// partial text, and several of those are one-shot - an extraction that runs on half a meeting marks
/// the recording done, and the real pass then skips it.
/// </para>
/// <para>
/// Each test <b>records</b> whether the model was reached rather than throwing from the fake. The
/// processors wrap their work in try/catch and log, so a throwing fake is swallowed and the test then
/// passes whether or not a gate exists - which is exactly how the first version of this file passed
/// before any gate was written.
/// </para>
/// </summary>
public class ProvisionalTranscriptionGateTests
{
    private const string Template = "Summarise: {{transcript}}";

    private static async Task<(Recording Rec, Transcription Tr)> Seed(
        DiarizDbContext db, Guid userId, bool provisional)
    {
        Users.Ensure(db, userId);
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Standup",
            BlobKey = $"{userId}/a.webm", Status = RecordingStatus.Transcribing,
        };
        db.Recordings.Add(rec);
        var tr = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx-live",
            Version = 1, IsProvisional = provisional,
        };
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 4000, Original = "Shall we make a start", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    [Fact]
    public async Task Summarization_OnAProvisionalTranscription_DoesNothing()
    {
        var reached = false;
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), provisional: true);

        await SummarizationProcessor.ProcessAsync(
            db, new FakeSummarizationClient(() => reached = true), new FakeLlmSettingsResolver(),
            new FakeHubContext(), new SummarizationJob(rec.Id, tr.Id), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "http://x.test");

        Assert.False(await db.Summaries.AnyAsync(s => s.TranscriptionId == tr.Id));
        Assert.False(reached, "the model must never be reached for a provisional transcription");
    }

    [Fact]
    public async Task Actions_OnAProvisionalTranscription_DoNotRun()
    {
        // Extraction stamps ActionsExtractedAt even when it finds nothing, so running on partial text
        // would mark the recording done and the real pass would then skip it - losing every action item
        // in the meeting, silently. The marker assertion matters as much as the row count.
        var reached = false;
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), provisional: true);

        await ActionsProcessor.ProcessAsync(
            db, new FakeActionsClient(() => reached = true), new FakeLlmSettingsResolver(),
            new FakeHubContext(), new FakeJobQueue(), new ActionsJob(rec.Id, tr.Id), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "http://x.test");

        Assert.False(await db.RecordingActions.AnyAsync(a => a.RecordingId == rec.Id));
        Assert.Null((await db.Recordings.SingleAsync(r => r.Id == rec.Id)).ActionsExtractedAt);
        Assert.False(reached, "the model must never be reached for a provisional transcription");
    }

    [Fact]
    public async Task Tags_OnAProvisionalTranscription_DoNotRun()
    {
        var reached = false;
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), provisional: true);

        await TagsProcessor.ProcessAsync(
            db, new FakeTagsClient(() => reached = true), new FakeLlmSettingsResolver(),
            new FakeHubContext(), new TagsJob(rec.Id, tr.Id), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "http://x.test");

        Assert.False(await db.RecordingTags.AnyAsync(t => t.RecordingId == rec.Id));
        Assert.Null((await db.Recordings.SingleAsync(r => r.Id == rec.Id)).TagsExtractedAt);
        Assert.False(reached, "the model must never be reached for a provisional transcription");
    }

    [Fact]
    public async Task Embedding_OnAProvisionalTranscription_WritesNoChunks()
    {
        // Spec D5: chunks are replaced wholesale, so embedding every thirty seconds would re-embed the
        // whole meeting repeatedly. Chat pre-loads the live transcript instead.
        var reached = false;
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), provisional: true);

        await EmbeddingProcessor.ProcessAsync(
            db, new FakeEmbeddingClient(() => reached = true), new FakeEmbeddingSettingsResolver(),
            new EmbeddingJob(rec.Id, tr.Id), NullLogger.Instance);

        Assert.False(await db.TranscriptChunks.AnyAsync(c => c.TranscriptionId == tr.Id));
        Assert.False(reached, "the model must never be reached for a provisional transcription");
    }

    [Fact]
    public async Task AFinalTranscription_IsStillProcessedNormally()
    {
        // The companion assertion, and the reason the four above are not vacuous: every one of them
        // would pass just as well if the processors had simply been broken.
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), provisional: false);

        await ActionsProcessor.ProcessAsync(
            db, new FakeActionsClient(), new FakeLlmSettingsResolver(),
            new FakeHubContext(), new FakeJobQueue(), new ActionsJob(rec.Id, tr.Id), Template,
            NullLogger.Instance, new CapturingWebhookPublisher(), "http://x.test");

        Assert.NotNull((await db.Recordings.SingleAsync(r => r.Id == rec.Id)).ActionsExtractedAt);
    }

    [Fact]
    public async Task TranscriptExport_OnAProvisionalTranscription_Returns409()
    {
        // Exporting would hand someone a document that reads like the record and is not. All four
        // formats funnel through one renderer, so gating there covers txt, md, rtf and srt.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, _) = await Seed(db, me, provisional: true);

        var result = await LiveTestSupport.Build(db, me).TranscriptTxt(rec.Id);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task TranscriptExport_OnAFinishedTranscription_StillWorks()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, _) = await Seed(db, me, provisional: false);

        var result = await LiveTestSupport.Build(db, me).TranscriptTxt(rec.Id);

        Assert.IsType<FileContentResult>(result);
    }

    [Fact]
    public async Task Get_ReturnsTheProvisionalTranscription_WithTheFlagSet()
    {
        // The one consumer that does NOT refuse it: the UI has to render the live transcript, and needs
        // to know it is provisional so it can say so.
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, _) = await Seed(db, me, provisional: true);

        var dto = (await LiveTestSupport.Build(db, me).Get(rec.Id)).Value!;

        Assert.NotNull(dto.Current);
        Assert.True(dto.Current!.IsProvisional);
        Assert.Single(dto.Current.Segments);
    }
}
