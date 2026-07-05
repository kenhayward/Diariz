using Diariz.Api.Services;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Options;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class RecordingsControllerIntegrationTests(ContainersFixture fx)
{
    private static RecordingsController Build(Diariz.Domain.DiarizDbContext db, Guid userId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transcription:DefaultModel"] = "whisperx-large-v3" })
            .Build();
        var resolver = new SummarizationSettingsResolver(
            db, Options.Create(new SummarizationOptions { ApiBase = "http://llm.test/v1" }), new FakeApiKeyProtector());
        return new RecordingsController(db, new FakeAudioStorage(), new FakeJobQueue(), new FakeHubContext(), config,
            resolver, new FakeEmailSender(), new FakeSpeakerIdentifier(), Options.Create(new UploadOptions()))
        {
            ControllerContext = Http.Context(userId)
        };
    }

    private async Task<(Guid userId, Guid recId)> SeedRecording()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
        db.Users.Add(user);
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return (user.Id, rec.Id);
    }

    [Fact]
    public async Task RenameSpeaker_CreatesSpeaker_WhenLabelNotYetPresent()
    {
        var (userId, recId) = await SeedRecording();

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(
                await Build(db, userId).RenameSpeaker(recId, new RenameSpeakerRequest("SPEAKER_07", "Bob")));

        await using var verify = fx.CreateDbContext();
        var sp = await verify.Speakers.SingleAsync(s => s.RecordingId == recId);
        Assert.Equal("SPEAKER_07", sp.Label);
        Assert.Equal("Bob", sp.DisplayName);
    }

    [Fact]
    public async Task Delete_CascadesTranscriptionsSegmentsSpeakersAndSummary()
    {
        Guid userId, recId, trId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
            db.AddRange(
                user, rec, tr,
                new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1, Original = "x", Ordinal = 0 },
                new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "A" },
                new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "gpt", Text = "s" });
            await db.SaveChangesAsync();
            (userId, recId, trId) = (user.Id, rec.Id, tr.Id);
        }

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(await Build(db, userId).Delete(recId));

        await using var verify = fx.CreateDbContext();
        Assert.Null(await verify.Recordings.FindAsync(recId));
        Assert.False(await verify.Transcriptions.AnyAsync(t => t.RecordingId == recId));
        Assert.False(await verify.Segments.AnyAsync(s => s.TranscriptionId == trId));
        Assert.False(await verify.Speakers.AnyAsync(s => s.RecordingId == recId));
        Assert.False(await verify.Summaries.AnyAsync(s => s.TranscriptionId == trId));
    }

    [Fact]
    public async Task Merge_ReassignsSourceAttachmentToSurvivor_SurvivingTheSourceRowDeletion()
    {
        // The merge moves the merged-away source's attachments onto the survivor before the source row is
        // deleted. This is the real-Postgres guard: the Attachment FK has ON DELETE CASCADE, so EF must
        // UPDATE the row's RecordingId before DELETEing the old parent — otherwise the cascade would take it.
        Guid userId, survivorId, sourceId, attId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            // No audio on either → the merge settles synchronously and deletes the source row in one SaveChanges.
            var early = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k1", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5), AudioDeletedAt = DateTimeOffset.UtcNow, SizeBytes = 0, DurationMs = 1000, Status = RecordingStatus.Transcribed };
            var later = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k2", CreatedAt = DateTimeOffset.UtcNow, AudioDeletedAt = DateTimeOffset.UtcNow, SizeBytes = 0, DurationMs = 2000, Status = RecordingStatus.Transcribed };
            var trE = new Transcription { Id = Guid.NewGuid(), RecordingId = early.Id, Model = "m", Version = 1 };
            var trL = new Transcription { Id = Guid.NewGuid(), RecordingId = later.Id, Model = "m", Version = 1 };
            var att = new Attachment { Id = Guid.NewGuid(), RecordingId = later.Id, Kind = AttachmentKind.File, Name = "doc.pdf", BlobKey = $"{user.Id}/attachments/x.pdf", SizeBytes = 10, Ordinal = 0 };
            db.AddRange(user, early, later, trE, trL,
                new Segment { Id = Guid.NewGuid(), TranscriptionId = trE.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "Hello", Ordinal = 0 },
                new Segment { Id = Guid.NewGuid(), TranscriptionId = trL.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 2000, Original = "World", Ordinal = 0 },
                new Speaker { Id = Guid.NewGuid(), RecordingId = early.Id, Label = "SPEAKER_00", DisplayName = "A" },
                new Speaker { Id = Guid.NewGuid(), RecordingId = later.Id, Label = "SPEAKER_00", DisplayName = "B" },
                att);
            await db.SaveChangesAsync();
            (userId, survivorId, sourceId, attId) = (user.Id, early.Id, later.Id, att.Id);
        }

        await using (var db = fx.CreateDbContext())
            Assert.IsType<AcceptedResult>(
                await Build(db, userId).Merge(new MergeRecordingsRequest([sourceId, survivorId])));

        await using var verify = fx.CreateDbContext();
        Assert.Null(await verify.Recordings.FindAsync(sourceId)); // source row deleted
        var moved = await verify.Attachments.FindAsync(attId);
        Assert.NotNull(moved);                                    // survived the source deletion (not cascaded)
        Assert.Equal(survivorId, moved!.RecordingId);             // reassigned onto the survivor
    }

    [Fact]
    public async Task DeleteAudio_FreesQuota_FlagsDeleted_AndAudioBecomes404()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k", SizeBytes = 1000 };
            db.AddRange(user, rec);
            await db.SaveChangesAsync();
            (userId, recId) = (user.Id, rec.Id);
        }

        await using (var db = fx.CreateDbContext())
        {
            Assert.Equal(1000, await new StorageUsage(db).UsedBytesAsync(userId)); // counted before delete
            Assert.IsType<NoContentResult>(await Build(db, userId).DeleteAudio(recId));
        }

        await using var verify = fx.CreateDbContext();
        var rec2 = (await verify.Recordings.FindAsync(recId))!;
        Assert.NotNull(rec2.AudioDeletedAt);
        Assert.False(rec2.HasAudio);
        Assert.Equal(0, rec2.SizeBytes);
        Assert.Equal(0, await new StorageUsage(verify).UsedBytesAsync(userId)); // quota freed
        Assert.IsType<NotFoundResult>(await Build(verify, userId).GetAudio(recId)); // audio gone
    }

    [Fact]
    public async Task Get_ReturnsHighestVersionTranscription_WithSummary()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k", Name = "Demo" };
            db.AddRange(user, rec);
            foreach (var v in new[] { 1, 2, 3 })
            {
                var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = v };
                db.Add(tr);
                if (v == 3) db.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "gpt", Text = "The summary" });
            }
            await db.SaveChangesAsync();
            (userId, recId) = (user.Id, rec.Id);
        }

        await using var db2 = fx.CreateDbContext();
        var result = await Build(db2, userId).Get(recId);

        var dto = Assert.IsType<RecordingDetailDto>(result.Value);
        Assert.Equal(3, dto.Current!.Version);
        Assert.Equal("Demo", dto.Name);
        Assert.Equal("The summary", dto.Summary!.Text);
    }

    [Fact]
    public async Task UpdateSegment_PersistsRevised_AndGetReturnsBoth()
    {
        Guid userId, recId, segId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k", Name = "Demo" };
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
            var seg = new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "the original", Ordinal = 0 };
            db.AddRange(user, rec, tr, seg,
                new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
            await db.SaveChangesAsync();
            (userId, recId, segId) = (user.Id, rec.Id, seg.Id);
        }

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(
                await Build(db, userId).UpdateSegment(recId, segId, new UpdateSegmentRequest("a revision")));

        // The revision persists through real Postgres; the original column is untouched.
        await using var verify = fx.CreateDbContext();
        var stored = await verify.Segments.FindAsync(segId);
        Assert.Equal("the original", stored!.Original);
        Assert.Equal("a revision", stored.Revised);

        // The detail projection surfaces both, and effective text = revision.
        var dto = Assert.IsType<RecordingDetailDto>((await Build(verify, userId).Get(recId)).Value);
        var segDto = Assert.Single(dto.Current!.Segments);
        Assert.Equal("the original", segDto.Original);
        Assert.Equal("a revision", segDto.Revised);
        Assert.Equal("a revision", segDto.Text);

        // A null reset clears the revision back to the original.
        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(
                await Build(db, userId).UpdateSegment(recId, segId, new UpdateSegmentRequest(null)));
        await using var verify2 = fx.CreateDbContext();
        Assert.Null((await verify2.Segments.FindAsync(segId))!.Revised);
    }

    [Fact]
    public async Task Delete_CascadesCalendarLink()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            db.AddRange(user, rec, new RecordingCalendarLink
            {
                RecordingId = rec.Id, EventId = "evt1", Summary = "Planning",
                StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1), LinkedManually = true,
            });
            await db.SaveChangesAsync();
            (userId, recId) = (user.Id, rec.Id);
        }

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(await Build(db, userId).Delete(recId));

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.RecordingCalendarLinks.AnyAsync(l => l.RecordingId == recId)); // cascaded with the recording
    }

    [Fact]
    public async Task ListAndGet_SurfaceTheCalendarLink_OnRealPostgres()
    {
        // Proves the LEFT JOIN projection (r.CalendarLink != null ? ... : null) and the detail Include
        // translate against real Postgres, not just the in-memory provider.
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k", Name = "Demo" };
            db.AddRange(user, rec, new RecordingCalendarLink
            {
                RecordingId = rec.Id, EventId = "evt42", Summary = "Planning",
                StartsAt = DateTimeOffset.Parse("2026-07-02T09:00:00Z"), EndsAt = DateTimeOffset.Parse("2026-07-02T10:00:00Z"),
                HtmlLink = "https://cal/evt42", LinkedManually = true,
            });
            await db.SaveChangesAsync();
            (userId, recId) = (user.Id, rec.Id);
        }

        await using var db2 = fx.CreateDbContext();
        var list = await Build(db2, userId).List();
        Assert.Equal("evt42", list.Single(r => r.Id == recId).CalendarEventId);

        var dto = Assert.IsType<RecordingDetailDto>((await Build(db2, userId).Get(recId)).Value);
        Assert.Equal("evt42", dto.CalendarLink!.EventId);
        Assert.True(dto.CalendarLink.LinkedManually);
        Assert.Equal("https://cal/evt42", dto.CalendarLink.HtmlLink);
    }
}
