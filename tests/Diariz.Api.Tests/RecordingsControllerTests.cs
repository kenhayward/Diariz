using System.Text;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Diariz.Api.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Diariz.Api.Configuration;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class RecordingsControllerTests
{
    private static RecordingsController Build(DiarizDbContext db, Guid userId, FakeJobQueue queue,
        FakeAudioStorage? storage = null, bool summarizationEnabled = true, FakeEmailSender? email = null,
        FakeSpeakerIdentifier? identifier = null, UploadOptions? uploads = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transcription:DefaultModel"] = "whisperx-large-v3" })
            .Build();
        var resolver = new SummarizationSettingsResolver(
            db,
            Options.Create(new SummarizationOptions { ApiBase = summarizationEnabled ? "http://llm.test/v1" : "" }),
            new FakeApiKeyProtector());
        return new RecordingsController(db, storage ?? new FakeAudioStorage(), queue, new FakeHubContext(), config,
            resolver, email ?? new FakeEmailSender(), identifier ?? new FakeSpeakerIdentifier(),
            Options.Create(uploads ?? new UploadOptions()))
        {
            ControllerContext = Http.Context(userId)
        };
    }

    private static async Task SeedUser(DiarizDbContext db, Guid userId, long? quotaBytes = null)
    {
        var u = new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" };
        if (quotaBytes is not null) u.QuotaBytes = quotaBytes.Value;
        db.Users.Add(u);
        await db.SaveChangesAsync();
    }

    private static async Task<Recording> SeedRecording(DiarizDbContext db, Guid userId, params int[] versions)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BlobKey = $"{userId}/blob.webm",
            Status = RecordingStatus.Transcribed
        };
        db.Recordings.Add(rec);
        foreach (var v in versions)
            db.Transcriptions.Add(new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = v });
        await db.SaveChangesAsync();
        return rec;
    }

    [Fact]
    public async Task Retranscribe_CreatesNextVersion_AndEnqueuesJob()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, queue);

        var result = await controller.Retranscribe(rec.Id, new RetranscribeRequest(Model: null));

        Assert.IsType<AcceptedResult>(result);

        var versions = await db.Transcriptions.Where(t => t.RecordingId == rec.Id)
            .Select(t => t.Version).OrderBy(v => v).ToListAsync();
        Assert.Equal([1, 2], versions);

        var job = Assert.Single(queue.Enqueued);
        Assert.Equal(rec.Id, job.RecordingId);
        Assert.Equal(rec.BlobKey, job.BlobKey);

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Queued, reloaded!.Status);
    }

    [Fact]
    public async Task Retranscribe_WithSpeakerHints_PersistsThem_AndCarriesInJob()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, queue);

        await controller.Retranscribe(rec.Id, new RetranscribeRequest(Model: null, Speakers: new SpeakerHints(2, null)));

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(2, reloaded!.MinSpeakers);
        Assert.Null(reloaded.MaxSpeakers);
        var job = Assert.Single(queue.Enqueued);
        Assert.Equal(2, job.MinSpeakers);
        Assert.Null(job.MaxSpeakers);
    }

    [Fact]
    public async Task Retranscribe_WithoutSpeakers_PreservesExistingHints()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.MinSpeakers = 3;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, queue);

        // No Speakers object → the list/menu re-transcribe must not wipe the recording's hint.
        await controller.Retranscribe(rec.Id, new RetranscribeRequest(Model: null));

        Assert.Equal(3, (await db.Recordings.FindAsync(rec.Id))!.MinSpeakers);
        Assert.Equal(3, Assert.Single(queue.Enqueued).MinSpeakers);
    }

    [Fact]
    public async Task Retranscribe_MinAboveMax_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.Retranscribe(rec.Id, new RetranscribeRequest(null, new SpeakerHints(5, 2)));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Retranscribe_NonPositiveSpeakers_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.IsType<BadRequestObjectResult>(
            await controller.Retranscribe(rec.Id, new RetranscribeRequest(null, new SpeakerHints(0, null))));
    }

    [Fact]
    public async Task Retranscribe_UsesRequestedModel_WhenProvided()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, queue);

        await controller.Retranscribe(rec.Id, new RetranscribeRequest(Model: "whisperx-medium"));

        Assert.Equal("whisperx-medium", Assert.Single(queue.Enqueued).Model);
    }

    [Fact]
    public async Task Get_ReturnsNotFound_ForRecordingOwnedByAnotherUser()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var rec = await SeedRecording(db, owner, versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue()); // different user

        var result = await controller.Get(rec.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // The "current transcription = highest version" rule depends on a filtered Include
    // (OrderByDescending(Version).Take(1)) that the database translates. The EF in-memory
    // provider does NOT honour the ordering/Take inside a filtered Include (it loads the whole
    // collection), so this behaviour can only be verified faithfully against real Postgres.
    // Belongs in the integration (Testcontainers) harness — see CLAUDE.md.
    [Fact(Skip = "Filtered-Include ordering is not emulated by the EF in-memory provider; covered by the integration harness.")]
    public async Task Get_ReturnsOnlyHighestVersionTranscription()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: [1, 2, 3]);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.Get(rec.Id);

        var dto = Assert.IsType<RecordingDetailDto>(result.Value);
        Assert.Equal(3, dto.Current!.Version);
    }

    // ---- Upload ----

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
    public async Task Upload_StoresBlob_PersistsRecording_AndEnqueuesFirstTranscription()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var queue = new FakeJobQueue();
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, queue, storage);
        var bytes = Encoding.UTF8.GetBytes("hello-audio-bytes");

        var result = await controller.Upload(FakeAudio(bytes), title: "Standup", durationMs: 4200);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var summary = Assert.IsType<RecordingSummaryDto>(created.Value);
        Assert.Equal("Standup", summary.Title);
        Assert.Equal(RecordingStatus.Queued, summary.Status);

        var rec = await db.Recordings.SingleAsync();
        Assert.Equal(userId, rec.UserId);
        Assert.Equal(4200, rec.DurationMs);
        Assert.Equal(bytes.Length, rec.SizeBytes); // audio size recorded for quota accounting
        Assert.True(storage.Objects.ContainsKey(rec.BlobKey));
        Assert.Equal(bytes, storage.Objects[rec.BlobKey]);

        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.Equal(1, tr.Version);
        var job = Assert.Single(queue.Enqueued);
        Assert.Equal(rec.Id, job.RecordingId);
        Assert.Equal(tr.Id, job.TranscriptionId);
    }

    [Fact]
    public async Task Upload_EmptyFile_ReturnsBadRequest_AndStoresNothing()
    {
        using var db = TestDb.Create();
        var queue = new FakeJobQueue();
        var storage = new FakeAudioStorage();
        var controller = Build(db, Guid.NewGuid(), queue, storage);

        var result = await controller.Upload(FakeAudio(Array.Empty<byte>()), title: "x", durationMs: 0);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.Recordings.ToListAsync());
        Assert.Empty(queue.Enqueued);
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task Upload_BlankTitle_GetsGeneratedDefaultTitle()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.Upload(FakeAudio(), title: null, durationMs: 1000);

        var summary = Assert.IsType<RecordingSummaryDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.StartsWith("Recording ", summary.Title);
    }

    [Fact]
    public async Task Upload_OverQuota_Returns413_AndStoresNothing()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId, quotaBytes: 10); // tiny quota
        var queue = new FakeJobQueue();
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, queue, storage);

        var result = await controller.Upload(FakeAudio(Encoding.UTF8.GetBytes("more-than-ten-bytes")), title: "x", durationMs: 1);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(413, obj.StatusCode);
        Assert.Empty(await db.Recordings.ToListAsync());
        Assert.Empty(queue.Enqueued);
        Assert.Empty(storage.Objects);
    }

    // A minimal valid WAV header ("RIFF....WAVEfmt ") so source=Upload validation passes.
    private static byte[] WavBytes() => Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVEfmt ");

    [Fact]
    public async Task Upload_SourceUpload_ValidWav_Succeeds()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.Upload(
            FakeAudio(WavBytes(), fileName: "memo.wav", contentType: "audio/wav"),
            title: "Memo", durationMs: 0, source: RecordingSource.Upload);

        var summary = Assert.IsType<RecordingSummaryDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(RecordingSource.Upload, summary.Source);
        var rec = await db.Recordings.SingleAsync();
        Assert.True(storage.Objects.ContainsKey(rec.BlobKey));
        Assert.Equal(WavBytes(), storage.Objects[rec.BlobKey]); // whole stream stored after the head peek
    }

    [Fact]
    public async Task Upload_SourceUpload_UnsupportedBytes_Returns415_StoresNothing()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.Upload(
            FakeAudio(Encoding.ASCII.GetBytes("%PDF-1.7 not audio!"), fileName: "evil.wav"),
            title: "x", durationMs: 0, source: RecordingSource.Upload);

        Assert.Equal(415, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Empty(await db.Recordings.ToListAsync());
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task Upload_SourceUpload_M4a_RejectedWhenAacDisabled()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var controller = Build(db, userId, new FakeJobQueue(), uploads: new UploadOptions { AllowAac = false });

        var result = await controller.Upload(
            FakeAudio(Encoding.ASCII.GetBytes("\0\0\0ftypM4A \0\0"), fileName: "voice.m4a"),
            title: "x", durationMs: 0, source: RecordingSource.Upload);

        Assert.Equal(415, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task Upload_SourceUpload_OverMaxBytes_Returns413()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var controller = Build(db, userId, new FakeJobQueue(), uploads: new UploadOptions { MaxBytes = 8 });

        var result = await controller.Upload(
            FakeAudio(WavBytes(), fileName: "big.wav"), // 16 bytes > 8
            title: "x", durationMs: 0, source: RecordingSource.Upload);

        Assert.Equal(413, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task Upload_WithinQuota_CountsExistingUsage()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId, quotaBytes: 100);
        // 95 bytes already used; a 10-byte upload would exceed the 100-byte quota.
        db.Recordings.Add(new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", SizeBytes = 95 });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), new FakeAudioStorage());

        var result = await controller.Upload(FakeAudio(new byte[10]), title: "x", durationMs: 1);

        Assert.Equal(413, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    // ---- RenameSpeaker ----

    [Fact]
    public async Task RenameSpeaker_UpdatesExistingSpeakerDisplayName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.RenameSpeaker(rec.Id, new RenameSpeakerRequest("SPEAKER_00", "Alice"));

        Assert.IsType<NoContentResult>(result);
        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id && s.Label == "SPEAKER_00");
        Assert.Equal("Alice", sp.DisplayName);
    }

    [Fact]
    public async Task RenameSpeaker_DetachesFromVoiceprint()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        db.SpeakerProfiles.Add(profile);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", ProfileId = profile.Id, IdentifiedAuto = true
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.RenameSpeaker(rec.Id, new RenameSpeakerRequest("SPEAKER_00", "Carol"));

        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id);
        Assert.Equal("Carol", sp.DisplayName);
        Assert.Null(sp.ProfileId);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task RenameSpeaker_CreatesSpeaker_WhenLabelNotYetPresent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.RenameSpeaker(rec.Id, new RenameSpeakerRequest("SPEAKER_07", "Bob"));

        Assert.IsType<NoContentResult>(result);
        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id);
        Assert.Equal("SPEAKER_07", sp.Label);
        Assert.Equal("Bob", sp.DisplayName);
    }

    [Fact]
    public async Task RenameSpeaker_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        var result = await controller.RenameSpeaker(rec.Id, new RenameSpeakerRequest("SPEAKER_00", "X"));

        Assert.IsType<NotFoundResult>(result);
    }

    // ---- AssignSpeaker (voiceprints) ----

    [Fact]
    public async Task AssignSpeaker_LinksSpeakerToProfile_AndSetsDisplayName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00" });
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.AssignSpeaker(rec.Id, "SPEAKER_00", new AssignSpeakerRequest(profile.Id));

        Assert.IsType<NoContentResult>(result);
        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id);
        Assert.Equal(profile.Id, sp.ProfileId);
        Assert.Equal("Alice", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto); // explicit manual assignment
    }

    [Fact]
    public async Task AssignSpeaker_WithNullProfile_RevertsToAnonymousLabel()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        db.SpeakerProfiles.Add(profile);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", ProfileId = profile.Id, IdentifiedAuto = true
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.AssignSpeaker(rec.Id, "SPEAKER_00", new AssignSpeakerRequest(null));

        Assert.IsType<NoContentResult>(result);
        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id);
        Assert.Null(sp.ProfileId);
        Assert.Equal("SPEAKER_00", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task AssignSpeaker_ToProfileOwnedByAnotherUser_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00" });
        var othersProfile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.SpeakerProfiles.Add(othersProfile);
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.AssignSpeaker(rec.Id, "SPEAKER_00", new AssignSpeakerRequest(othersProfile.Id));

        Assert.IsType<NotFoundResult>(result);
        Assert.Null((await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id)).ProfileId);
    }

    [Fact]
    public async Task AssignSpeaker_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        var result = await controller.AssignSpeaker(rec.Id, "SPEAKER_00", new AssignSpeakerRequest(Guid.NewGuid()));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AssignSpeaker_UnknownLabel_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.AssignSpeaker(rec.Id, "SPEAKER_99", new AssignSpeakerRequest(profile.Id));

        Assert.IsType<NotFoundResult>(result);
    }

    // ---- Move to section ----

    [Fact]
    public async Task MoveToSection_SetsSectionId_OnOwnedRecordingAndSection()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var section = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = userId, Name = "Work" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.MoveToSection(rec.Id, new MoveRecordingRequest(section.Id));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(section.Id, (await db.Recordings.FindAsync(rec.Id))!.SectionId);
    }

    [Fact]
    public async Task MoveToSection_NullSection_Ungroups()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = userId, Name = "Work" };
        var rec = await SeedRecording(db, userId, versions: 1);
        db.Sections.Add(section);
        rec.SectionId = section.Id;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.MoveToSection(rec.Id, new MoveRecordingRequest(null));

        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.SectionId);
    }

    [Fact]
    public async Task MoveToSection_AnotherUsersSection_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var othersSection = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.Sections.Add(othersSection);
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.MoveToSection(rec.Id, new MoveRecordingRequest(othersSection.Id));

        Assert.IsType<NotFoundResult>(result);
        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.SectionId);
    }

    [Fact]
    public async Task MoveToSection_AnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        var result = await controller.MoveToSection(rec.Id, new MoveRecordingRequest(null));

        Assert.IsType<NotFoundResult>(result);
    }

    // ---- Update segment ----

    [Fact]
    public async Task UpdateSegment_UpdatesText_OnOwnedRecording()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId);
        var seg = await db.Segments.FirstAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.UpdateSegment(rec.Id, seg.Id, new UpdateSegmentRequest("Corrected text"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Corrected text", (await db.Segments.FindAsync(seg.Id))!.Text);
    }

    [Fact]
    public async Task UpdateSegment_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribedRecording(db, Guid.NewGuid());
        var seg = await db.Segments.FirstAsync();
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        var result = await controller.UpdateSegment(rec.Id, seg.Id, new UpdateSegmentRequest("hijack"));

        Assert.IsType<NotFoundResult>(result);
        Assert.NotEqual("hijack", (await db.Segments.FindAsync(seg.Id))!.Text);
    }

    [Fact]
    public async Task UpdateSegment_WrongRecordingId_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedTranscribedRecording(db, userId);
        var seg = await db.Segments.FirstAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.UpdateSegment(Guid.NewGuid(), seg.Id, new UpdateSegmentRequest("x"));

        Assert.IsType<NotFoundResult>(result);
    }

    // ---- Merge segments ----

    [Fact]
    public async Task MergeSegments_CollapsesConsecutiveSameSpeaker()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId); // two consecutive SPEAKER_00 segments
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.MergeSegments(rec.Id);

        Assert.IsType<NoContentResult>(result);
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        var seg = Assert.Single(await db.Segments.Where(s => s.TranscriptionId == tr.Id).ToListAsync());
        Assert.Equal("Hello\n\nWorld", seg.Text); // paragraph break between merged sections
        Assert.Equal(0, seg.StartMs);
        Assert.Equal(2000, seg.EndMs);
        Assert.Equal(0, seg.Ordinal);
    }

    [Fact]
    public async Task MergeSegments_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribedRecording(db, Guid.NewGuid());
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.MergeSegments(rec.Id));
    }

    [Fact]
    public async Task MergeSegments_MergesDifferentLabelsAssignedToTheSamePerson()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        // Two consecutive segments diarized as different speakers...
        db.Segments.AddRange(
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Text = "Hello", Ordinal = 0 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_01", StartMs = 1000, EndMs = 2000, Text = "World", Ordinal = 1 });
        // ...but both reassigned to the same person.
        var profileId = Guid.NewGuid();
        db.Speakers.AddRange(
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice", ProfileId = profileId },
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Alice", ProfileId = profileId });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.MergeSegments(rec.Id);

        Assert.IsType<NoContentResult>(result);
        var seg = Assert.Single(await db.Segments.Where(s => s.TranscriptionId == tr.Id).ToListAsync());
        Assert.Equal("Hello\n\nWorld", seg.Text);
        Assert.Equal("SPEAKER_00", seg.SpeakerLabel); // keeps the first run's label
        Assert.Equal(0, seg.StartMs);
        Assert.Equal(2000, seg.EndMs);
    }

    // ---- Re-identify ----

    [Fact]
    public async Task Reidentify_OnOwnedRecording_ReturnsNoContent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId);
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.IsType<NoContentResult>(await controller.Reidentify(rec.Id));
    }

    [Fact]
    public async Task Reidentify_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribedRecording(db, Guid.NewGuid());
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.Reidentify(rec.Id));
    }

    // ---- Email transcript ----

    [Fact]
    public async Task EmailTranscript_SendsToUsersAddress_WithSubjectAndBody()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        var email = new FakeEmailSender { Sent = true };
        var controller = Build(db, userId, new FakeJobQueue(), email: email);

        var result = await controller.EmailTranscript(rec.Id);

        Assert.IsType<OkResult>(result);
        var msg = Assert.Single(email.Messages);
        Assert.Equal($"{userId}@x.test", msg.To);
        Assert.Equal("Transcript for Team Sync", msg.Subject);
        Assert.Contains("Alice", msg.Body);            // speaker display name
        Assert.Contains("Sent from Diariz", msg.Body);
    }

    [Fact]
    public async Task EmailTranscript_WhenEmailUnconfigured_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedTranscribedRecording(db, userId, name: "X");
        var controller = Build(db, userId, new FakeJobQueue(), email: new FakeEmailSender { Sent = false });

        Assert.IsType<BadRequestObjectResult>(await controller.EmailTranscript(rec.Id));
    }

    [Fact]
    public async Task EmailTranscript_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribedRecording(db, Guid.NewGuid(), name: "X");
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.EmailTranscript(rec.Id));
    }

    // ---- Summarize ----

    [Fact]
    public async Task Summarize_SetsStatusSummarizing_AndEnqueuesJob()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        var controller = Build(db, userId, queue);

        var result = await controller.Summarize(rec.Id);

        Assert.IsType<AcceptedResult>(result);
        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Summarizing, reloaded!.Status);
        var job = Assert.Single(queue.SummarizationEnqueued);
        Assert.Equal(rec.Id, job.RecordingId);
        Assert.Equal(tr.Id, job.TranscriptionId);
    }

    [Fact]
    public async Task Summarize_WhenAlreadySummarizing_DoesNotDoubleEnqueue()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.Status = RecordingStatus.Summarizing;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, queue);

        var result = await controller.Summarize(rec.Id);

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(queue.SummarizationEnqueued);
    }

    [Fact]
    public async Task Summarize_WhenNotConfigured_ReturnsBadRequest_AndDoesNotEnqueue()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, queue, summarizationEnabled: false);

        var result = await controller.Summarize(rec.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(queue.SummarizationEnqueued);
        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.NotEqual(RecordingStatus.Summarizing, reloaded!.Status);
    }

    [Fact]
    public async Task Summarize_AllowedWhenUserConfigured_EvenIfServerEmpty()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        // The user has their own endpoint configured even though the server default is empty.
        db.UserSettings.Add(new Diariz.Domain.Entities.UserSettings { UserId = userId, SummaryApiBase = "https://mine/v1" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, queue, summarizationEnabled: false); // server ApiBase empty

        var result = await controller.Summarize(rec.Id);

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(queue.SummarizationEnqueued);
    }

    [Fact]
    public async Task Summarize_NoTranscription_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId); // no transcription versions
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.Summarize(rec.Id));
    }

    [Fact]
    public async Task Summarize_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.Summarize(rec.Id));
    }

    // ---- Rename recording ----

    [Fact]
    public async Task Rename_SetsName_OnOwnedRecording()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.Rename(rec.Id, new RenameRecordingRequest("Weekly Standup"));

        Assert.IsType<NoContentResult>(result);
        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal("Weekly Standup", reloaded!.Name);
    }

    [Fact]
    public async Task Rename_BlankName_ClearsToNull()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.Name = "Old name";
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.Rename(rec.Id, new RenameRecordingRequest("   "));

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Null(reloaded!.Name);
    }

    [Fact]
    public async Task Rename_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        var result = await controller.Rename(rec.Id, new RenameRecordingRequest("X"));

        Assert.IsType<NotFoundResult>(result);
    }

    // ---- Delete recording ----

    [Fact]
    public async Task Delete_RemovesRecording_AndBlob()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, userId, versions: 1);
        storage.Objects[rec.BlobKey] = Encoding.UTF8.GetBytes("audio");
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.Delete(rec.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.Recordings.FindAsync(rec.Id));
        Assert.False(storage.Objects.ContainsKey(rec.BlobKey));
    }

    [Fact]
    public async Task Delete_OnAnotherUsersRecording_ReturnsNotFound_AndKeepsBlob()
    {
        using var db = TestDb.Create();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        storage.Objects[rec.BlobKey] = Encoding.UTF8.GetBytes("audio");
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue(), storage);

        var result = await controller.Delete(rec.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.NotNull(await db.Recordings.FindAsync(rec.Id));
        Assert.True(storage.Objects.ContainsKey(rec.BlobKey));
    }

    // ---- Transcript download ----

    private static async Task<Recording> SeedTranscribedRecording(
        DiarizDbContext db, Guid userId, string? name = null)
    {
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.Name = name;
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        db.Segments.AddRange(
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Text = "Hello", Ordinal = 0 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 1000, EndMs = 2000, Text = "World", Ordinal = 1 });
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        await db.SaveChangesAsync();
        return rec;
    }

    [Fact]
    public async Task TranscriptTxt_ReturnsTextFile_WithSpeakerNames_AndSlugFilename()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.TranscriptTxt(rec.Id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/plain", file.ContentType);
        Assert.Equal("team-sync.txt", file.FileDownloadName);
        var text = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("Transcript Name\nTeam Sync", text); // mirrors the emailed layout
        Assert.Contains("Summary\n—", text);
        Assert.Contains("[00:00] Alice\nHello", text);
        Assert.Contains("[00:01] Alice\nWorld", text);
    }

    [Fact]
    public async Task TranscriptMd_ReturnsMarkdownTable()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        var controller = Build(db, userId, new FakeJobQueue());

        var file = Assert.IsType<FileContentResult>(await controller.TranscriptMd(rec.Id));
        Assert.Equal("text/markdown", file.ContentType);
        Assert.Equal("team-sync.md", file.FileDownloadName);
        var md = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("# Team Sync", md);
        Assert.Contains("| Time | Speaker | Text |", md);
        Assert.Contains("| 00:00 | Alice | Hello |", md);
    }

    [Fact]
    public async Task TranscriptRtf_ReturnsRtfDocument()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        var controller = Build(db, userId, new FakeJobQueue());

        var file = Assert.IsType<FileContentResult>(await controller.TranscriptRtf(rec.Id));
        Assert.Equal("application/rtf", file.ContentType);
        Assert.Equal("team-sync.rtf", file.FileDownloadName);
        Assert.StartsWith("{\\rtf1", Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task TranscriptSrt_ReturnsSubripFile()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.TranscriptSrt(rec.Id);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/x-subrip", file.ContentType);
        Assert.Equal("team-sync.srt", file.FileDownloadName);
        Assert.StartsWith("1\n00:00:00,000 --> 00:00:01,000\nAlice: Hello\n", Encoding.UTF8.GetString(file.FileContents));
    }

    [Fact]
    public async Task TranscriptTxt_NoSegments_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1); // transcription row but no segments
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.TranscriptTxt(rec.Id));
    }

    [Fact]
    public async Task TranscriptTxt_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribedRecording(db, Guid.NewGuid(), name: "x");
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.TranscriptTxt(rec.Id));
    }

    // ---- AudioUrl (same-origin streaming URL) ----

    private static string UrlOf(Microsoft.AspNetCore.Mvc.ActionResult<object> result) =>
        (string)result.Value!.GetType().GetProperty("url")!.GetValue(result.Value)!;

    [Fact]
    public async Task AudioUrl_ReturnsSameOriginStreamingUrl_ForOwnedRecording()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.AudioUrl(rec.Id);

        Assert.Null(result.Result); // not a NotFound
        Assert.StartsWith($"/api/recordings/{rec.Id}/audio?access_token=", UrlOf(result));
        Assert.DoesNotContain("download=true", UrlOf(result));
    }

    [Fact]
    public async Task AudioUrl_WithDownload_AddsDownloadFlag()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.Contains("download=true", UrlOf(await controller.AudioUrl(rec.Id, download: true)));
    }

    [Fact]
    public async Task AudioUrl_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>((await controller.AudioUrl(rec.Id)).Result);
    }

    // ---- GetAudio (streaming) ----

    [Fact]
    public async Task GetAudio_StreamsWholeBlob_WithAcceptRanges()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, userId, versions: 1);
        var bytes = Encoding.UTF8.GetBytes("hello-audio-data");
        storage.Objects[rec.BlobKey] = bytes;
        rec.SizeBytes = bytes.Length;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);
        var body = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = body;

        var result = await controller.GetAudio(rec.Id);

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(bytes, body.ToArray());
        var resp = controller.ControllerContext.HttpContext.Response;
        Assert.Equal(200, resp.StatusCode);
        Assert.Equal("bytes", resp.Headers.AcceptRanges.ToString());
        Assert.Equal(bytes.Length, resp.ContentLength);
    }

    [Fact]
    public async Task GetAudio_WithRange_Returns206PartialContent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, userId, versions: 1);
        storage.Objects[rec.BlobKey] = Encoding.UTF8.GetBytes("0123456789");
        rec.SizeBytes = 10;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);
        var http = controller.ControllerContext.HttpContext;
        http.Request.Headers.Range = "bytes=2-5";
        var body = new MemoryStream();
        http.Response.Body = body;

        await controller.GetAudio(rec.Id);

        Assert.Equal(206, http.Response.StatusCode);
        Assert.Equal("bytes 2-5/10", http.Response.Headers.ContentRange.ToString());
        Assert.Equal("2345", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task GetAudio_Download_SetsAttachmentFilename()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.Name = "Demo";
        rec.SizeBytes = 3;
        storage.Objects[rec.BlobKey] = Encoding.UTF8.GetBytes("abc");
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);
        controller.ControllerContext.HttpContext.Response.Body = new MemoryStream();

        await controller.GetAudio(rec.Id, download: true);

        Assert.Contains("demo.webm",
            controller.ControllerContext.HttpContext.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task GetAudio_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.GetAudio(rec.Id));
    }
}
