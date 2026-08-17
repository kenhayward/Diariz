using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres fidelity for the <c>transcript</c> merge field. Resolving it loads the recording's
/// CURRENT transcription (highest Version, Take(1)) and its segments in Ordinal order - a filtered Include with
/// ordering and a Take, which the in-memory provider ignores outright. So "the field renders the latest
/// re-transcribe, in order" cannot be asserted as a unit test; it is verified here instead.</summary>
[Collection(IntegrationCollection.Name)]
public class TranscriptFieldIntegrationTests(ContainersFixture fx)
{
    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A template that is nothing but a transcript field: no prompt block, so the run makes no LLM call
    /// and the persisted document IS the substituted table.</summary>
    private static string TranscriptOnlyTemplate() =>
        new TemplateContent([
            new TemplateSection(0, "", [new TemplateBlock(TemplateBlock.FieldKind, Field: "transcript")]),
        ]).Serialize();

    [Fact]
    public async Task Transcript_field_renders_the_current_versions_segments_in_order_with_speaker_names()
    {
        var userId = await SeedUser();
        var recId = Guid.NewGuid();
        var formulaId = Guid.NewGuid();
        var resultId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Add(new Recording
            {
                Id = recId, UserId = userId, Title = "Team Sync", BlobKey = "k", CreatedAt = DateTimeOffset.UtcNow,
            });

            // Renamed speakers: the table must show DisplayName, not the raw diarization label.
            db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice" });
            db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = recId, Label = "SPEAKER_01", DisplayName = "Bob" });

            // Version 1 - superseded by the re-transcribe below, so none of its text may appear.
            var v1 = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "whisperx", Version = 1 };
            db.Transcriptions.Add(v1);
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = v1.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = "Stale first-pass text.", Ordinal = 0,
            });

            // Version 2 - the current one. Added out of ordinal order so the ordering is actually exercised.
            var v2 = new Transcription { Id = Guid.NewGuid(), RecordingId = recId, Model = "whisperx", Version = 2 };
            db.Transcriptions.Add(v2);
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = v2.Id, SpeakerLabel = "SPEAKER_01",
                StartMs = 64_000, EndMs = 66_500, Original = "Right, on the API.", Ordinal = 1,
            });
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = v2.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 852, EndMs = 3_896, Original = "So here's the thing.", Ordinal = 0,
            });

            db.Formulas.Add(new Formula
            {
                Id = formulaId, Scope = FormulaScope.Personal, OwnerUserId = userId, Name = "Full record",
                ContentJson = TranscriptOnlyTemplate(), Context = FormulaContext.Transcript, Enabled = true,
            });
            db.FormulaResults.Add(new FormulaResult
            {
                Id = resultId, RecordingId = recId, CreatedByUserId = userId, FormulaId = formulaId,
                Name = "Full record", Ordinal = 0, Status = FormulaRunStatus.Generating,
            });
            await db.SaveChangesAsync();
        }

        var chat = new FakeChatStreamClient();
        await using (var db = fx.CreateDbContext())
        {
            await FormulaRunProcessor.ProcessAsync(
                db, chat, new FakeLlmSettingsResolver(), new FakeHubContext(),
                new FormulaRunJob(recId, null, resultId, formulaId, userId), NullLogger.Instance,
                new CapturingWebhookPublisher(), "");
        }

        Assert.Equal(0, chat.Calls); // a field-only template asks the model nothing

        await using (var verify = fx.CreateDbContext())
        {
            var persisted = await verify.FormulaResults.FindAsync(resultId);
            Assert.NotNull(persisted);
            Assert.Equal(FormulaRunStatus.Ready, persisted!.Status);

            var text = persisted.Text;
            Assert.StartsWith("| Time | Speaker | Text |", text);   // the bare table, no heading of its own
            Assert.Contains("| 00:00 | Alice | So here's the thing. |", text);
            Assert.Contains("| 01:04 | Bob | Right, on the API. |", text);
            Assert.DoesNotContain("Stale first-pass text.", text);  // version 1 is not the current transcription
            Assert.DoesNotContain("SPEAKER_0", text);               // labels never surface, only display names
            Assert.True(
                text.IndexOf("So here's the thing.", StringComparison.Ordinal)
                < text.IndexOf("Right, on the API.", StringComparison.Ordinal),
                "segments must be ordered by Ordinal, not insertion order");
        }
    }
}
