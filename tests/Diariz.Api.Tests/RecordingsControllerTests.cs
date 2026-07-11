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
        FakeSpeakerIdentifier? identifier = null, UploadOptions? uploads = null, IExportLocalizer? exportLocalizer = null,
        IGoogleCalendarClient? calendar = null)
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
            Options.Create(uploads ?? new UploadOptions()), new RoomScope(db), exportLocalizer, calendar)
        {
            ControllerContext = Http.Context(userId)
        };
    }

    /// <summary>Returns canned events and records the query window.</summary>
    private sealed class FakeCalendarClient : IGoogleCalendarClient
    {
        public IReadOnlyList<CalendarEvent>? Events { get; set; } = new List<CalendarEvent>();
        public CalendarEvent? Event { get; set; }
        public string? RequestedEventId { get; private set; }
        public DateTimeOffset? TimeMin { get; private set; }
        public DateTimeOffset? TimeMax { get; private set; }

        public Task<IReadOnlyList<CalendarEvent>?> ListEventsAsync(
            Guid userId, DateTimeOffset timeMin, DateTimeOffset timeMax, CancellationToken ct = default)
        {
            TimeMin = timeMin; TimeMax = timeMax;
            return Task.FromResult(Events);
        }

        public Task<CalendarEvent?> GetEventAsync(Guid userId, string eventId, CancellationToken ct = default)
        {
            RequestedEventId = eventId;
            return Task.FromResult(Event);
        }

        public Task<IReadOnlyList<CalendarListEntry>?> ListAllCalendarsAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarListEntry>?>(null);
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
        Users.Ensure(db, userId);
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
        // The recordings list is room-scoped now, so give it its main placement in the owner's personal room.
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId: null);
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

    /// <summary>Phase 6: List(?roomId=) browses a room's recordings. A recording shared into a room shows up
    /// when a member lists that room; a non-member 404s.</summary>
    [Fact]
    public async Task List_ByRoom_ShowsRecordingsSharedIntoThatRoom()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        await SeedUser(db, owner);
        Users.Ensure(db, member);
        var rec = await SeedRecording(db, owner, versions: 1); // placed in owner's personal room
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Engineering", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, member, RoomPermission.CreateRecording);
        await scope.ShareIntoRoomAsync(rec.Id, roomId, owner, sectionId: null);

        // The member lists the shared room and sees the shared recording.
        var list = (await Build(db, member, new FakeJobQueue()).List(roomId)).Value!;
        Assert.Single(list, r => r.Id == rec.Id);

        // A stranger who isn't a member 404s.
        Users.Ensure(db, Guid.NewGuid());
        var stranger = Guid.NewGuid();
        Users.Ensure(db, stranger);
        Assert.IsType<NotFoundResult>((await Build(db, stranger, new FakeJobQueue()).List(roomId)).Result);
    }

    /// <summary>Phase 5: the detail carries who recorded it and the rooms it is placed in (home room first).</summary>
    [Fact]
    public async Task Get_ReturnsRecordedBy_AndTheRoomsItIsIn()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        await SeedUser(db, owner);
        var rec = await SeedRecording(db, owner, versions: 1);
        var scope = new RoomScope(db);
        await scope.PlaceInMainRoomAsync(rec.Id, owner, sectionId: null);
        var sharedId = await scope.CreateSharedRoomAsync("Engineering", null, null, null);
        await scope.ShareIntoRoomAsync(rec.Id, sharedId, owner, sectionId: null);
        await scope.SetMemberAsync(sharedId, RoomPrincipalType.User, owner, RoomPermission.CreateRecording);
        var controller = Build(db, owner, new FakeJobQueue());

        var detail = (await controller.Get(rec.Id)).Value!;

        Assert.Equal(owner, detail.RecordedByUserId);
        Assert.NotNull(detail.Rooms);
        Assert.True(detail.Rooms![0].IsMain); // personal (home) room first
        Assert.Contains(detail.Rooms, r => !r.IsMain && r.Name == "Engineering");
    }

    /// <summary>Phase 5: the recorder shares their recording from their personal room (ShareOut is implicit
    /// there) into a shared room they can record in.</summary>
    [Fact]
    public async Task Share_AddsASharedPlacement_InTheTargetRoom()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        await SeedUser(db, owner);
        var rec = await SeedRecording(db, owner, versions: 1);
        var scope = new RoomScope(db);
        var personalRoomId = await scope.PersonalRoomIdAsync(owner);
        await scope.PlaceInMainRoomAsync(rec.Id, owner, sectionId: null);
        var target = await scope.CreateSharedRoomAsync("Engineering", null, null, null);
        await scope.SetMemberAsync(target, RoomPrincipalType.User, owner, RoomPermission.CreateRecording);
        var controller = Build(db, owner, new FakeJobQueue());

        Assert.IsType<NoContentResult>(await controller.Share(rec.Id, new ShareRecordingRequest(personalRoomId, target)));
        var shared = db.RoomRecordings.Single(p => p.RoomId == target);
        Assert.False(shared.IsMainRoom);
        Assert.Equal(owner, shared.SharedByUserId);
    }

    /// <summary>Sharing into a room the caller can't record in is a 403.</summary>
    [Fact]
    public async Task Share_IntoRoomWithoutCreateRecording_Returns403()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        await SeedUser(db, owner);
        var rec = await SeedRecording(db, owner, versions: 1);
        var scope = new RoomScope(db);
        var personalRoomId = await scope.PersonalRoomIdAsync(owner);
        await scope.PlaceInMainRoomAsync(rec.Id, owner, sectionId: null);
        var target = await scope.CreateSharedRoomAsync("Engineering", null, null, null); // owner is not a member
        var controller = Build(db, owner, new FakeJobQueue());

        Assert.Equal(403, ((ObjectResult)await controller.Share(rec.Id, new ShareRecordingRequest(personalRoomId, target))).StatusCode);
        Assert.False(db.RoomRecordings.Any(p => p.RoomId == target));
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

    /// <summary>Uploading creates the recording's main placement in the uploader's personal room, so it appears
    /// in their list. Before placements existed this was Recording.SectionId, defaulting to null.</summary>
    [Fact]
    public async Task Upload_CreatesTheMainPlacement_InThePersonalRoom()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var scope = new RoomScope(db);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.Upload(FakeAudio(Encoding.UTF8.GetBytes("audio")), title: "Standup", durationMs: 1000);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var placement = db.RoomRecordings.Single();
        Assert.Equal(await scope.PersonalRoomIdAsync(userId), placement.RoomId);
        Assert.True(placement.IsMainRoom);
        Assert.Null(placement.SectionId);
    }

    /// <summary>A recording uploaded with a folder in the caller's personal room lands in that folder (the
    /// placement-preference "record into the selected folder" path).</summary>
    [Fact]
    public async Task Upload_WithSectionInPersonalRoom_FilesThePlacementThere()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var sectionId = Guid.NewGuid();
        db.Sections.Add(new Section { Id = sectionId, UserId = userId, RoomId = roomId, Name = "Projects" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.Upload(FakeAudio(Encoding.UTF8.GetBytes("audio")), "Standup", durationMs: 1000, sectionId: sectionId);

        Assert.Equal(sectionId, db.RoomRecordings.Single().SectionId);
    }

    /// <summary>A section id that doesn't belong to the caller's personal room is ignored - the recording is
    /// ungrouped rather than misfiled.</summary>
    [Fact]
    public async Task Upload_WithAlienSection_IsIgnored_AndUngrouped()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.Upload(FakeAudio(Encoding.UTF8.GetBytes("audio")), "Standup", durationMs: 1000, sectionId: Guid.NewGuid());

        Assert.Null(db.RoomRecordings.Single().SectionId);
    }

    /// <summary>Recording into a shared room the caller may record in creates BOTH placements: the always-main
    /// personal one (ungrouped) and a non-main shared one in the room.</summary>
    [Fact]
    public async Task Upload_IntoSharedRoom_CreatesMainPersonalAndSharedPlacements()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var scope = new RoomScope(db);
        var personalId = await scope.PersonalRoomIdAsync(userId);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, userId, RoomPermission.CreateRecording);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.Upload(FakeAudio(Encoding.UTF8.GetBytes("audio")), "Standup", durationMs: 1000, roomId: roomId);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var main = db.RoomRecordings.Single(p => p.IsMainRoom);
        Assert.Equal(personalId, main.RoomId);
        Assert.Null(main.SectionId); // ungrouped in the personal room, per spec
        var shared = db.RoomRecordings.Single(p => !p.IsMainRoom);
        Assert.Equal(roomId, shared.RoomId);
        Assert.Equal(userId, shared.SharedByUserId);
    }

    /// <summary>Recording into a room the caller can't record in is a 403, and nothing is stored.</summary>
    [Fact]
    public async Task Upload_IntoRoomWithoutCreateRecording_Returns403_AndStoresNothing()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null); // caller is not a member
        var storage = new FakeAudioStorage();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.Upload(FakeAudio(Encoding.UTF8.GetBytes("audio")), "Standup", durationMs: 1000, roomId: roomId);

        Assert.Equal(403, ((ObjectResult)result.Result!).StatusCode);
        Assert.Empty(db.Recordings);
        Assert.Empty(db.RoomRecordings);
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
    public async Task Upload_WithCombinedSource_PersistsCombined()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var controller = Build(db, userId, new FakeJobQueue(), new FakeAudioStorage());

        var result = await controller.Upload(
            FakeAudio(Encoding.UTF8.GetBytes("both-sides")), title: "Call", durationMs: 1000,
            source: RecordingSource.Combined);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var rec = await db.Recordings.SingleAsync();
        Assert.Equal(RecordingSource.Combined, rec.Source);
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

    // The folder a recording sits in, read from its placement in the owner's personal room.
    private static async Task<Guid?> FolderOf(DiarizDbContext db, Guid userId, Guid recordingId)
    {
        var scope = new RoomScope(db);
        return await scope.SectionIdAsync(await scope.PersonalRoomIdAsync(userId), recordingId);
    }

    [Fact]
    public async Task MoveToSection_SetsSectionId_OnOwnedRecordingAndSection()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecording(db, userId, versions: 1);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var section = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "Work" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId: null); // main placement, ungrouped
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.MoveToSection(rec.Id, new MoveRecordingRequest(section.Id));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(section.Id, await FolderOf(db, userId, rec.Id));
    }

    [Fact]
    public async Task MoveToSection_NullSection_Ungroups()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var section = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "Work" };
        var rec = await SeedRecording(db, userId, versions: 1);
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, section.Id); // placed in the folder
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.MoveToSection(rec.Id, new MoveRecordingRequest(null));

        Assert.Null(await FolderOf(db, userId, rec.Id));
    }

    [Fact]
    public async Task MoveToSection_AnotherUsersSection_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId); // MoveToSection mints the caller's personal room to scope the section check
        var rec = await SeedRecording(db, userId, versions: 1);
        var othersSection = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Theirs" };
        db.Sections.Add(othersSection);
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.MoveToSection(rec.Id, new MoveRecordingRequest(othersSection.Id));

        // Rejected on the ownership check, before any placement write - nothing changed.
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task MoveToSection_AnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await SeedUser(db, me);
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1); // owned by someone else, not placed in my room
        var controller = Build(db, me, new FakeJobQueue());

        var result = await controller.MoveToSection(rec.Id, new MoveRecordingRequest(null));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task MoveToSection_IntoASharedRoomSection_FilesThePlacementThere()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await SeedUser(db, me);
        var scope = new RoomScope(db);
        var rec = await SeedRecording(db, me, versions: 1);
        await scope.PlaceInMainRoomAsync(rec.Id, me, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, me,
            RoomPermission.ManageContents | RoomPermission.CreateRecording);
        await scope.ShareIntoRoomAsync(rec.Id, roomId, me, sectionId: null);
        var section = new Section { Id = Guid.NewGuid(), UserId = me, RoomId = roomId, Name = "Topics" };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        var controller = Build(db, me, new FakeJobQueue());

        var result = await controller.MoveToSection(rec.Id, new MoveRecordingRequest(section.Id, RoomId: roomId));

        Assert.IsType<NoContentResult>(result);
        var placement = await db.RoomRecordings.SingleAsync(p => p.RecordingId == rec.Id && p.RoomId == roomId);
        Assert.Equal(section.Id, placement.SectionId); // filed in the shared room, not the personal one
    }

    [Fact]
    public async Task MoveToSection_InSharedRoom_WithoutManageContents_Is403()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        await SeedUser(db, me);
        var scope = new RoomScope(db);
        var rec = await SeedRecording(db, me, versions: 1);
        await scope.PlaceInMainRoomAsync(rec.Id, me, sectionId: null);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, me, RoomPermission.CreateRecording); // no ManageContents
        await scope.ShareIntoRoomAsync(rec.Id, roomId, me, sectionId: null);
        var controller = Build(db, me, new FakeJobQueue());

        var result = await controller.MoveToSection(rec.Id, new MoveRecordingRequest(null, RoomId: roomId));
        Assert.IsType<ForbidResult>(result);
    }

    // ---- Update segment ----

    [Fact]
    public async Task UpdateSegment_SetsRevised_PreservingOriginal()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId);
        var seg = await db.Segments.OrderBy(s => s.Ordinal).FirstAsync();
        var original = seg.Original;
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.UpdateSegment(rec.Id, seg.Id, new UpdateSegmentRequest("Corrected text"));

        Assert.IsType<NoContentResult>(result);
        var updated = (await db.Segments.FindAsync(seg.Id))!;
        Assert.Equal("Corrected text", updated.Revised);     // the edit lands on Revised
        Assert.Equal(original, updated.Original);            // the model's original is preserved
        Assert.Equal("Corrected text", updated.EffectiveText);
    }

    [Fact]
    public async Task UpdateSegment_NullText_ResetsRevisedToOriginal()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId);
        var seg = await db.Segments.OrderBy(s => s.Ordinal).FirstAsync();
        var controller = Build(db, userId, new FakeJobQueue());
        await controller.UpdateSegment(rec.Id, seg.Id, new UpdateSegmentRequest("an edit"));

        var result = await controller.UpdateSegment(rec.Id, seg.Id, new UpdateSegmentRequest(null));

        Assert.IsType<NoContentResult>(result);
        var updated = (await db.Segments.FindAsync(seg.Id))!;
        Assert.Null(updated.Revised);                        // revision cleared
        Assert.Equal(updated.Original, updated.EffectiveText); // shows the original again
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
        Assert.Null((await db.Segments.FindAsync(seg.Id))!.Revised); // unchanged
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

    // ---- Delete segment ----

    [Fact]
    public async Task DeleteSegment_RemovesSegment_AndRenumbersOrdinals()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId); // two segments: "Hello"(0), "World"(1)
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        var first = await db.Segments.Where(s => s.TranscriptionId == tr.Id).OrderBy(s => s.Ordinal).FirstAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.DeleteSegment(rec.Id, first.Id);

        Assert.IsType<NoContentResult>(result);
        var remaining = await db.Segments.Where(s => s.TranscriptionId == tr.Id)
            .OrderBy(s => s.Ordinal).ToListAsync();
        var seg = Assert.Single(remaining);
        Assert.Equal("World", seg.Original);
        Assert.Equal(0, seg.Ordinal); // renumbered contiguously from 0
    }

    [Fact]
    public async Task DeleteSegment_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribedRecording(db, Guid.NewGuid());
        var seg = await db.Segments.FirstAsync();
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        var result = await controller.DeleteSegment(rec.Id, seg.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.NotNull(await db.Segments.FindAsync(seg.Id)); // untouched
    }

    [Fact]
    public async Task DeleteSegment_WrongRecordingId_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedTranscribedRecording(db, userId);
        var seg = await db.Segments.FirstAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.DeleteSegment(Guid.NewGuid(), seg.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteSegments_RemovesTheSet_AndRenumbersSurvivorsOnce()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId); // "Hello"(0), "World"(1)
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 2000, EndMs = 3000, Original = "Again", Ordinal = 2 });
        await db.SaveChangesAsync();
        var hello = await db.Segments.SingleAsync(s => s.Original == "Hello");
        var again = await db.Segments.SingleAsync(s => s.Original == "Again");
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.DeleteSegments(rec.Id, new DeleteSegmentsRequest(new[] { hello.Id, again.Id }));

        Assert.IsType<NoContentResult>(result);
        var remaining = await db.Segments.Where(s => s.TranscriptionId == tr.Id).OrderBy(s => s.Ordinal).ToListAsync();
        var seg = Assert.Single(remaining);
        Assert.Equal("World", seg.Original);
        Assert.Equal(0, seg.Ordinal); // renumbered contiguously
    }

    [Fact]
    public async Task DeleteSegments_RemovingASpeakersLastSegment_PrunesThatSpeakerRow()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId); // "Hello"/"World" both SPEAKER_00 + Speaker SPEAKER_00
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Bob" });
        var bob = new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_01", StartMs = 4000, EndMs = 5000, Original = "Bye", Ordinal = 2 };
        db.Segments.Add(bob);
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.DeleteSegments(rec.Id, new DeleteSegmentsRequest(new[] { bob.Id }));

        Assert.IsType<NoContentResult>(result);
        var labels = await db.Speakers.Where(s => s.RecordingId == rec.Id).Select(s => s.Label).ToListAsync();
        Assert.DoesNotContain("SPEAKER_01", labels); // its last segment is gone → row pruned
        Assert.Contains("SPEAKER_00", labels);       // still has segments → kept
    }

    [Fact]
    public async Task DeleteSegments_LeavingSomeOfASpeakersSegments_KeepsTheSpeakerRow()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId); // "Hello"/"World" both SPEAKER_00
        var hello = await db.Segments.SingleAsync(s => s.Original == "Hello");
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.DeleteSegments(rec.Id, new DeleteSegmentsRequest(new[] { hello.Id }));

        var labels = await db.Speakers.Where(s => s.RecordingId == rec.Id).Select(s => s.Label).ToListAsync();
        Assert.Contains("SPEAKER_00", labels); // "World" is still SPEAKER_00 → kept
    }

    [Fact]
    public async Task DeleteSegments_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribedRecording(db, Guid.NewGuid());
        var seg = await db.Segments.FirstAsync();
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        var result = await controller.DeleteSegments(rec.Id, new DeleteSegmentsRequest(new[] { seg.Id }));

        Assert.IsType<NotFoundResult>(result);
        Assert.NotNull(await db.Segments.FindAsync(seg.Id)); // untouched
    }

    // ---- Multiple Speakers ----

    [Fact]
    public async Task MarkMultiSpeaker_SetsFlag_AndDetachesFromVoiceprint()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var profileId = Guid.NewGuid();
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", ProfileId = profileId, IdentifiedAuto = true,
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.MarkMultiSpeaker(rec.Id, "SPEAKER_00");

        Assert.IsType<NoContentResult>(result);
        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id && s.Label == "SPEAKER_00");
        Assert.True(sp.IsMultiSpeaker);
        Assert.Null(sp.ProfileId);
        Assert.False(sp.IdentifiedAuto);
        Assert.Equal(Speaker.MultiSpeakerName, sp.DisplayName);
    }

    [Fact]
    public async Task MarkMultiSpeaker_CreatesSpeaker_WhenLabelNotYetPresent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.MarkMultiSpeaker(rec.Id, "SPEAKER_07");

        Assert.IsType<NoContentResult>(result);
        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id && s.Label == "SPEAKER_07");
        Assert.True(sp.IsMultiSpeaker);
    }

    [Fact]
    public async Task MarkMultiSpeaker_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.MarkMultiSpeaker(rec.Id, "SPEAKER_00"));
    }

    [Fact]
    public async Task AssignSpeaker_Unassign_ClearsMultiFlag()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = Speaker.MultiSpeakerName, IsMultiSpeaker = true,
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.AssignSpeaker(rec.Id, "SPEAKER_00", new AssignSpeakerRequest(null));

        Assert.IsType<NoContentResult>(result);
        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id && s.Label == "SPEAKER_00");
        Assert.False(sp.IsMultiSpeaker);
        Assert.Equal("SPEAKER_00", sp.DisplayName); // reverted to the raw label
    }

    [Fact]
    public async Task RenameSpeaker_ClearsMultiFlag()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = Speaker.MultiSpeakerName, IsMultiSpeaker = true,
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.RenameSpeaker(rec.Id, new RenameSpeakerRequest("SPEAKER_00", "Bob"));

        var sp = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id && s.Label == "SPEAKER_00");
        Assert.False(sp.IsMultiSpeaker);
        Assert.Equal("Bob", sp.DisplayName);
    }

    // ---- Manual summary editing ----

    [Fact]
    public async Task UpdateSummary_CreatesSummary_WhenNone_EvenWithoutLlm()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId);
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        // No LLM configured — manual editing must still work.
        var controller = Build(db, userId, new FakeJobQueue(), summarizationEnabled: false);

        var result = await controller.UpdateSummary(rec.Id, new UpdateSummaryRequest("My hand-written notes."));

        Assert.IsType<NoContentResult>(result);
        var summary = await db.Summaries.SingleAsync(s => s.TranscriptionId == tr.Id);
        Assert.Equal("My hand-written notes.", summary.Text);
        Assert.True(summary.IsUserEdited);
        Assert.NotNull(summary.UpdatedAt);
        Assert.Equal(Summary.UserEditedModel, summary.Model);
    }

    [Fact]
    public async Task UpdateSummary_UpdatesExisting_AndFlagsUserEdited()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId);
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        db.Summaries.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "gpt", Text = "auto" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.UpdateSummary(rec.Id, new UpdateSummaryRequest("edited"));

        var summary = await db.Summaries.SingleAsync(s => s.TranscriptionId == tr.Id);
        Assert.Equal("edited", summary.Text);
        Assert.True(summary.IsUserEdited);
    }

    [Fact]
    public async Task UpdateSummary_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedTranscribedRecording(db, Guid.NewGuid());
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.UpdateSummary(rec.Id, new UpdateSummaryRequest("x")));
    }

    [Fact]
    public async Task Summarize_ClearsUserEditedFlag_SoTheForcedResummaryOverwrites()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId);
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        db.Summaries.Add(new Summary
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "user", Text = "my edit", IsUserEdited = true,
        });
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();
        var controller = Build(db, userId, queue, summarizationEnabled: true);

        var result = await controller.Summarize(rec.Id);

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(queue.SummarizationEnqueued);
        var summary = await db.Summaries.SingleAsync(s => s.TranscriptionId == tr.Id);
        Assert.False(summary.IsUserEdited); // cleared so the queued job is allowed to overwrite it
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
        Assert.Equal("Hello\nWorld", seg.EffectiveText); // single line break between merged sections, no blank line
        Assert.Equal(0, seg.StartMs);
        Assert.Equal(2000, seg.EndMs);
        Assert.Equal(0, seg.Ordinal);
    }

    [Fact]
    public async Task MergeSegments_DoesNotMergeAcrossANote()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId); // two consecutive SPEAKER_00 segments (0-1000, 1000-2000)
        // A note taken during the first segment sits between the two - they must stay separate.
        db.MeetingNotes.Add(new MeetingNote
        {
            Id = Guid.NewGuid(), UserId = userId, RecordingId = rec.Id, Text = "budget concern", CapturedAtMs = 500, Ordinal = 0,
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.MergeSegments(rec.Id);

        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        var segs = await db.Segments.Where(s => s.TranscriptionId == tr.Id).OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal(2, segs.Count); // the note boundary kept the same-speaker segments apart
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
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "Hello", Ordinal = 0 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_01", StartMs = 1000, EndMs = 2000, Original = "World", Ordinal = 1 });
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
        Assert.Equal("Hello\nWorld", seg.EffectiveText);
        Assert.Equal("SPEAKER_00", seg.SpeakerLabel); // keeps the first run's label
        Assert.Equal(0, seg.StartMs);
        Assert.Equal(2000, seg.EndMs);
    }

    // ---- Localized exports ----

    [Fact]
    public async Task TranscriptTxt_LocalizesHeadings_ToOwnersUiLanguage()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        db.UserSettings.Add(new Domain.Entities.UserSettings { UserId = userId, UiLanguage = "es" });
        await db.SaveChangesAsync();

        var root = Path.Combine(Path.GetTempPath(), "diariz-exp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "es"));
        await File.WriteAllTextAsync(Path.Combine(root, "es", "exports.json"),
            "{\"summary\":\"Resumen\",\"transcript\":\"Transcripción\"}");
        try
        {
            var controller = Build(db, userId, new FakeJobQueue(),
                exportLocalizer: new JsonExportLocalizer(root));

            var result = await controller.TranscriptTxt(rec.Id);

            var body = Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(result).FileContents);
            Assert.Contains("Resumen", body);        // localized heading
            Assert.Contains("Transcripción", body);
            Assert.DoesNotContain("\nSummary\n", body); // the English heading is gone
        }
        finally { Directory.Delete(root, true); }
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

    [Fact]
    public async Task Delete_AlsoFreesFileAttachmentBlobs_LeavingUrlAttachments()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, userId, versions: 1);
        storage.Objects[rec.BlobKey] = Encoding.UTF8.GetBytes("audio");
        var f1 = $"{userId}/attachments/{Guid.NewGuid()}.pdf";
        var f2 = $"{userId}/attachments/{Guid.NewGuid()}.docx";
        db.Attachments.AddRange(
            new Attachment { Id = Guid.NewGuid(), RecordingId = rec.Id, Kind = AttachmentKind.File, Name = "a.pdf", BlobKey = f1, Ordinal = 0 },
            new Attachment { Id = Guid.NewGuid(), RecordingId = rec.Id, Kind = AttachmentKind.File, Name = "b.docx", BlobKey = f2, Ordinal = 1 },
            new Attachment { Id = Guid.NewGuid(), RecordingId = rec.Id, Kind = AttachmentKind.Url, Name = "link", Url = "https://x.test", Ordinal = 2 });
        storage.Objects[f1] = Encoding.UTF8.GetBytes("pdf");
        storage.Objects[f2] = Encoding.UTF8.GetBytes("doc");
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.Delete(rec.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.Recordings.FindAsync(rec.Id));
        Assert.False(storage.Objects.ContainsKey(rec.BlobKey)); // audio freed
        Assert.False(storage.Objects.ContainsKey(f1));          // file-attachment blobs freed (no leak)
        Assert.False(storage.Objects.ContainsKey(f2));
    }

    // ---- Transcript download ----

    private static async Task<Recording> SeedTranscribedRecording(
        DiarizDbContext db, Guid userId, string? name = null)
    {
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.Name = name;
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        db.Segments.AddRange(
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "Hello", Ordinal = 0 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 1000, EndMs = 2000, Original = "World", Ordinal = 1 });
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        await db.SaveChangesAsync();
        return rec;
    }

    private static async Task AddMinutes(DiarizDbContext db, Guid recId, string text, bool userEdited = false)
    {
        var tr = await db.Transcriptions.OrderByDescending(t => t.Version)
            .FirstAsync(t => t.RecordingId == recId);
        db.MeetingMinutes.Add(new MeetingMinutes
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = userEdited ? "user" : "m", Text = text,
            IsUserEdited = userEdited,
        });
        await db.SaveChangesAsync();
    }

    // ---- Meeting minutes ----

    [Fact]
    public async Task GenerateMeetingMinutes_EnqueuesJob_WhenConfigured()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        var controller = Build(db, userId, queue);

        var result = await controller.GenerateMeetingMinutes(rec.Id);

        Assert.IsType<AcceptedResult>(result);
        var job = Assert.Single(queue.MeetingMinutesEnqueued);
        Assert.Equal(rec.Id, job.RecordingId);
        Assert.Equal(tr.Id, job.TranscriptionId);
    }

    [Fact]
    public async Task GenerateMeetingMinutes_ClearsUserEditedFlag_SoTheForcedRecreateOverwrites()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        await AddMinutes(db, rec.Id, "hand edit", userEdited: true);
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.GenerateMeetingMinutes(rec.Id);

        var minutes = await db.MeetingMinutes.SingleAsync();
        Assert.False(minutes.IsUserEdited); // cleared, so the queued job may overwrite it
    }

    [Fact]
    public async Task GenerateMeetingMinutes_WhenNotConfigured_ReturnsBadRequest_AndDoesNotEnqueue()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, queue, summarizationEnabled: false);

        Assert.IsType<BadRequestObjectResult>(await controller.GenerateMeetingMinutes(rec.Id));
        Assert.Empty(queue.MeetingMinutesEnqueued);
    }

    [Fact]
    public async Task UpdateMeetingMinutes_CreatesRow_MarksUserEdited()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.UpdateMeetingMinutes(rec.Id, new UpdateMeetingMinutesRequest("# My minutes"));

        Assert.IsType<NoContentResult>(result);
        var minutes = await db.MeetingMinutes.SingleAsync();
        Assert.Equal("# My minutes", minutes.Text);
        Assert.True(minutes.IsUserEdited);
        Assert.Equal(MeetingMinutes.UserEditedModel, minutes.Model);
    }

    [Fact]
    public async Task EmailMeetingMinutes_SendsMinutesOnly_ToUser()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        await AddMinutes(db, rec.Id, "# Team Sync\n\n## Overview\n\nWe met.");
        var email = new FakeEmailSender { Sent = true };
        var controller = Build(db, userId, new FakeJobQueue(), email: email);

        var result = await controller.EmailMeetingMinutes(rec.Id, new EmailMeetingMinutesRequest(false));

        Assert.IsType<OkResult>(result);
        var msg = Assert.Single(email.Messages);
        Assert.Equal($"{userId}@x.test", msg.To);
        Assert.Equal("Meeting minutes for Team Sync", msg.Subject);
        Assert.Contains("Overview", msg.Body);        // rendered from the Markdown
        Assert.DoesNotContain("Alice", msg.Body);     // NOT the transcript — minutes only
        Assert.Empty(email.LastAttachments);
    }

    [Fact]
    public async Task EmailMeetingMinutes_WhenNoMinutes_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedTranscribedRecording(db, userId, name: "X");
        var controller = Build(db, userId, new FakeJobQueue(), email: new FakeEmailSender());

        Assert.IsType<BadRequestObjectResult>(
            await controller.EmailMeetingMinutes(rec.Id, new EmailMeetingMinutesRequest(false)));
    }

    [Fact]
    public async Task EmailMeetingMinutes_IncludeAttachments_AttachesRecordingFiles()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedTranscribedRecording(db, userId, name: "X");
        await AddMinutes(db, rec.Id, "# X");
        var storage = new FakeAudioStorage();
        storage.Objects[$"{userId}/attachments/doc.pdf"] = Encoding.UTF8.GetBytes("PDFBYTES");
        db.Attachments.Add(new Attachment
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Kind = AttachmentKind.File, Name = "doc.pdf",
            BlobKey = $"{userId}/attachments/doc.pdf", ContentType = "application/pdf", SizeBytes = 8, Ordinal = 0,
        });
        await db.SaveChangesAsync();
        var email = new FakeEmailSender { Sent = true };
        var controller = Build(db, userId, new FakeJobQueue(), storage: storage, email: email);

        var result = await controller.EmailMeetingMinutes(rec.Id, new EmailMeetingMinutesRequest(true));

        Assert.IsType<OkResult>(result);
        var attachment = Assert.Single(email.LastAttachments);
        Assert.Equal("doc.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal("PDFBYTES", Encoding.UTF8.GetString(attachment.Content));
    }

    // ---- Calendar match ----

    private static async Task<Recording> SeedRecordingAt(DiarizDbContext db, Guid userId, DateTimeOffset createdAt, long durationMs)
    {
        Users.Ensure(db, userId);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", CreatedAt = createdAt, DurationMs = durationMs };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId: null); // list is room-scoped now
        return rec;
    }

    private static void GrantCalendar(DiarizDbContext db, Guid userId) =>
        db.UserSettings.Add(new Domain.Entities.UserSettings { UserId = userId, GoogleCalendarGranted = true });

    [Fact]
    public async Task CalendarMatch_WhenCalendarNotGranted_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 3_600_000);
        var cal = new FakeCalendarClient();
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);

        Assert.IsType<BadRequestObjectResult>(await controller.CalendarMatch(rec.Id, default));
        Assert.Null(cal.TimeMin); // never reached the Calendar client
    }

    [Fact]
    public async Task CalendarMatch_ReturnsBestOverlappingEvent_AndPadsTheWindow()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 3_600_000); // 09:00–10:00
        GrantCalendar(db, userId);
        await db.SaveChangesAsync();
        var cal = new FakeCalendarClient
        {
            Events = new List<CalendarEvent>
            {
                new("early", "Earlier", DateTimeOffset.Parse("2026-07-02T08:00:00Z"), DateTimeOffset.Parse("2026-07-02T09:05:00Z"), null),
                new("main", "Planning", DateTimeOffset.Parse("2026-07-02T09:00:00Z"), DateTimeOffset.Parse("2026-07-02T10:00:00Z"), "https://cal/main"),
            },
        };
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);

        var ok = Assert.IsType<OkObjectResult>(await controller.CalendarMatch(rec.Id, default));
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Planning", json);
        Assert.Contains("https://cal/main", json);
        // Window padded ±30 min around the recording span.
        Assert.Equal(DateTimeOffset.Parse("2026-07-02T08:30:00Z"), cal.TimeMin);
        Assert.Equal(DateTimeOffset.Parse("2026-07-02T10:30:00Z"), cal.TimeMax);
    }

    [Fact]
    public async Task CalendarMatch_WhenNothingOverlaps_ReturnsNullMatch()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 600_000);
        GrantCalendar(db, userId);
        await db.SaveChangesAsync();
        var cal = new FakeCalendarClient
        {
            Events = new List<CalendarEvent>
            {
                new("far", "Way earlier", DateTimeOffset.Parse("2026-07-02T06:00:00Z"), DateTimeOffset.Parse("2026-07-02T06:30:00Z"), null),
            },
        };
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);

        var ok = Assert.IsType<OkObjectResult>(await controller.CalendarMatch(rec.Id, default));
        Assert.Contains("\"match\":null", System.Text.Json.JsonSerializer.Serialize(ok.Value));
    }

    [Fact]
    public async Task CalendarMatch_WhenTokenExpired_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 600_000);
        GrantCalendar(db, userId);
        await db.SaveChangesAsync();
        var cal = new FakeCalendarClient { Events = null }; // refresh token revoked/expired
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);

        Assert.IsType<BadRequestObjectResult>(await controller.CalendarMatch(rec.Id, default));
    }

    // ---- Calendar link (persisted) ----

    private static CalendarEvent SampleEvent(string id = "evt1") => new(
        id, "Planning", DateTimeOffset.Parse("2026-07-02T09:00:00Z"), DateTimeOffset.Parse("2026-07-02T10:00:00Z"),
        "https://cal/evt1", CalendarId: "team@group.calendar.google.com", Color: "#0B8043");

    [Fact]
    public async Task LinkCalendar_WhenNotGranted_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 600_000);
        var cal = new FakeCalendarClient { Event = SampleEvent() };
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);

        Assert.IsType<BadRequestObjectResult>(await controller.LinkCalendar(rec.Id, new("evt1", true), default));
        Assert.Null(cal.RequestedEventId); // never reached the Calendar client
    }

    [Fact]
    public async Task LinkCalendar_WhenRecordingNotOwned_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        GrantCalendar(db, userId);
        await db.SaveChangesAsync();
        var cal = new FakeCalendarClient { Event = SampleEvent() };
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);

        Assert.IsType<NotFoundResult>(await controller.LinkCalendar(Guid.NewGuid(), new("evt1", true), default));
    }

    [Fact]
    public async Task LinkCalendar_WhenEventNotFound_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 600_000);
        GrantCalendar(db, userId);
        await db.SaveChangesAsync();
        var cal = new FakeCalendarClient { Event = null }; // event deleted / not reachable
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);

        Assert.IsType<BadRequestObjectResult>(await controller.LinkCalendar(rec.Id, new("evt1", true), default));
    }

    [Fact]
    public async Task LinkCalendar_StoresSnapshot_AndDetailAndListCarryIt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 600_000);
        GrantCalendar(db, userId);
        await db.SaveChangesAsync();
        var cal = new FakeCalendarClient { Event = SampleEvent() };
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);

        var ok = Assert.IsType<OkObjectResult>(await controller.LinkCalendar(rec.Id, new("evt1", true), default));
        var dto = Assert.IsType<CalendarLinkDto>(ok.Value);
        Assert.Equal("evt1", dto.EventId);
        Assert.Equal("Planning", dto.Summary);
        Assert.True(dto.LinkedManually);
        Assert.Equal("team@group.calendar.google.com", dto.CalendarId); // stored the event's calendar
        Assert.Equal("#0B8043", dto.Color);
        Assert.Equal("evt1", cal.RequestedEventId);

        var detail = (await controller.Get(rec.Id)).Value!;
        Assert.Equal("evt1", detail.CalendarLink!.EventId);
        Assert.Equal("team@group.calendar.google.com", detail.CalendarLink.CalendarId);

        var list = (await controller.List()).Value!;
        var summary = Assert.Single(list);
        Assert.Equal("evt1", summary.CalendarEventId);
        Assert.Equal("#0B8043", summary.CalendarColor);
    }

    [Fact]
    public async Task LinkCalendar_AdoptsPreMeetingEventNotes()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 600_000);
        GrantCalendar(db, userId);
        // A pre-meeting note anchored to the event (on the calendar the sample event reports).
        db.MeetingNotes.Add(new MeetingNote
        {
            Id = Guid.NewGuid(), UserId = userId,
            CalendarId = "team@group.calendar.google.com", EventId = "evt1", Text = "prep note", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), calendar: new FakeCalendarClient { Event = SampleEvent() });

        Assert.IsType<OkObjectResult>(await controller.LinkCalendar(rec.Id, new("evt1", true), default));

        var note = await db.MeetingNotes.SingleAsync();
        Assert.Equal(rec.Id, note.RecordingId); // adopted onto the recording
        Assert.Null(note.CalendarId);
        Assert.Null(note.EventId);
    }

    [Fact]
    public async Task LinkCalendar_Relinking_OverwritesTheExistingSnapshot()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 600_000);
        GrantCalendar(db, userId);
        await db.SaveChangesAsync();
        var cal = new FakeCalendarClient { Event = SampleEvent() };
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);
        await controller.LinkCalendar(rec.Id, new("evt1", false), default);

        cal.Event = new("evt2", "Retro", DateTimeOffset.Parse("2026-07-03T09:00:00Z"),
            DateTimeOffset.Parse("2026-07-03T10:00:00Z"), "https://cal/evt2");
        var ok = Assert.IsType<OkObjectResult>(await controller.LinkCalendar(rec.Id, new("evt2", true), default));
        var dto = Assert.IsType<CalendarLinkDto>(ok.Value);
        Assert.Equal("evt2", dto.EventId);
        Assert.True(dto.LinkedManually);
        Assert.Single(db.RecordingCalendarLinks); // still exactly one link row
    }

    [Fact]
    public async Task UnlinkCalendar_RemovesTheLink()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedRecordingAt(db, userId, DateTimeOffset.Parse("2026-07-02T09:00:00Z"), 600_000);
        GrantCalendar(db, userId);
        await db.SaveChangesAsync();
        var cal = new FakeCalendarClient { Event = SampleEvent() };
        var controller = Build(db, userId, new FakeJobQueue(), calendar: cal);
        await controller.LinkCalendar(rec.Id, new("evt1", true), default);

        Assert.IsType<NoContentResult>(await controller.UnlinkCalendar(rec.Id, default));
        Assert.Empty(db.RecordingCalendarLinks);
        var detail = (await controller.Get(rec.Id)).Value!;
        Assert.Null(detail.CalendarLink);
    }

    [Fact]
    public async Task EmailTranscript_IncludesMeetingMinutes_WhenPresent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await SeedUser(db, userId);
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        await AddMinutes(db, rec.Id, "## Overview\n\nWe met and agreed.");
        var email = new FakeEmailSender { Sent = true };
        var controller = Build(db, userId, new FakeJobQueue(), email: email);

        await controller.EmailTranscript(rec.Id);

        var msg = Assert.Single(email.Messages);
        Assert.Contains("Meeting Minutes", msg.Body);   // the section label
        Assert.Contains("We met and agreed.", msg.Body); // rendered minutes
        Assert.Contains("Alice", msg.Body);              // still carries the transcript
    }

    [Fact]
    public async Task TranscriptMd_IncludesMeetingMinutes_WhenPresent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedTranscribedRecording(db, userId, name: "Team Sync");
        await AddMinutes(db, rec.Id, "## Overview\n\nKey points.");
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.TranscriptMd(rec.Id);

        var file = Assert.IsType<FileContentResult>(result);
        var md = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("## Meeting Minutes", md);
        Assert.Contains("Key points.", md);
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

    // ---- Delete audio (keep the transcript) ----

    [Fact]
    public async Task DeleteAudio_RemovesBlob_FlagsDeleted_AndFreesQuota()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.SizeBytes = 1234;
        storage.Objects[rec.BlobKey] = Encoding.UTF8.GetBytes("audio");
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.DeleteAudio(rec.Id);

        Assert.IsType<NoContentResult>(result);
        var reloaded = (await db.Recordings.FindAsync(rec.Id))!;
        Assert.NotNull(reloaded.AudioDeletedAt);   // flagged
        Assert.False(reloaded.HasAudio);
        Assert.Equal(0, reloaded.SizeBytes);       // stops counting toward quota
        Assert.False(storage.Objects.ContainsKey(rec.BlobKey)); // blob gone
        Assert.NotNull(await db.Transcriptions.SingleOrDefaultAsync(t => t.RecordingId == rec.Id)); // transcript kept
    }

    [Fact]
    public async Task DeleteAudio_WhenAlreadyDeleted_IsNoOpSuccess()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.AudioDeletedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        rec.SizeBytes = 0;
        await db.SaveChangesAsync();
        var stamp = rec.AudioDeletedAt;
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.IsType<NoContentResult>(await controller.DeleteAudio(rec.Id));
        Assert.Equal(stamp, (await db.Recordings.FindAsync(rec.Id))!.AudioDeletedAt); // unchanged
    }

    [Fact]
    public async Task DeleteAudio_OnAnotherUsersRecording_ReturnsNotFound_AndKeepsBlob()
    {
        using var db = TestDb.Create();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        storage.Objects[rec.BlobKey] = Encoding.UTF8.GetBytes("audio");
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue(), storage);

        Assert.IsType<NotFoundResult>(await controller.DeleteAudio(rec.Id));
        Assert.True(storage.Objects.ContainsKey(rec.BlobKey));
        Assert.True((await db.Recordings.FindAsync(rec.Id))!.HasAudio);
    }

    [Fact]
    public async Task DeleteAudioBulk_DeletesOwnedWithAudio_SkipsRest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var withAudio = await SeedRecording(db, userId, versions: 1);
        withAudio.SizeBytes = 500;
        var alreadyGone = await SeedRecording(db, userId, versions: 1);
        alreadyGone.AudioDeletedAt = DateTimeOffset.UtcNow;
        var othersRec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        storage.Objects[withAudio.BlobKey] = Encoding.UTF8.GetBytes("a");
        storage.Objects[othersRec.BlobKey] = Encoding.UTF8.GetBytes("b");
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.DeleteAudioBulk(
            new DeleteAudioRequest([withAudio.Id, alreadyGone.Id, othersRec.Id]));

        Assert.IsType<NoContentResult>(result);
        Assert.False((await db.Recordings.FindAsync(withAudio.Id))!.HasAudio);
        Assert.Equal(0, (await db.Recordings.FindAsync(withAudio.Id))!.SizeBytes);
        Assert.False(storage.Objects.ContainsKey(withAudio.BlobKey));
        Assert.True(storage.Objects.ContainsKey(othersRec.BlobKey)); // other user's blob untouched
        Assert.True((await db.Recordings.FindAsync(othersRec.Id))!.HasAudio);
    }

    [Fact]
    public async Task GetAudio_AfterDelete_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.AudioDeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.GetAudio(rec.Id));
        Assert.IsType<NotFoundResult>((await controller.AudioUrl(rec.Id)).Result);
    }

    [Fact]
    public async Task Get_ReflectsHasAudio_FalseAfterDelete()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.AudioDeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var dto = Assert.IsType<RecordingDetailDto>((await controller.Get(rec.Id)).Value);
        Assert.False(dto.HasAudio);
    }

    // ---- Audio-deletion protection ----

    [Fact]
    public async Task SetAudioProtection_Protect_StampsProtectedAt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.SetAudioProtection(rec.Id, new SetAudioProtectionRequest(true));

        Assert.IsType<NoContentResult>(result);
        Assert.NotNull((await db.Recordings.FindAsync(rec.Id))!.AudioProtectedAt);
    }

    [Fact]
    public async Task SetAudioProtection_Unprotect_ClearsProtectedAt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.AudioProtectedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var result = await controller.SetAudioProtection(rec.Id, new SetAudioProtectionRequest(false));

        Assert.IsType<NoContentResult>(result);
        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.AudioProtectedAt);
    }

    [Fact]
    public async Task SetAudioProtection_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid(), versions: 1);
        var controller = Build(db, userId: Guid.NewGuid(), new FakeJobQueue());

        Assert.IsType<NotFoundResult>(
            await controller.SetAudioProtection(rec.Id, new SetAudioProtectionRequest(true)));
        Assert.Null((await db.Recordings.FindAsync(rec.Id))!.AudioProtectedAt);
    }

    [Fact]
    public async Task Get_Surfaces_AudioProtectedAt_AndAudioDeletedAt()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId, versions: 1);
        var protectedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var deletedAt = DateTimeOffset.UtcNow.AddDays(-1);
        rec.AudioProtectedAt = protectedAt;
        rec.AudioDeletedAt = deletedAt;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var dto = Assert.IsType<RecordingDetailDto>((await controller.Get(rec.Id)).Value);
        Assert.Equal(protectedAt, dto.AudioProtectedAt);
        Assert.Equal(deletedAt, dto.AudioDeletedAt);
    }

    [Fact]
    public async Task Get_ProjectsAudioScheduledDeletion_WhenAutoDeleteEnabledAndEligible()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new Diariz.Domain.Entities.PlatformSettings
        {
            Id = Diariz.Domain.Entities.PlatformSettings.SingletonId,
            AutoDeleteAudioEnabled = true, AudioRetentionDays = 30,
        });
        var rec = await SeedRecording(db, userId, versions: 1); // Status = Transcribed (eligible)
        var created = DateTimeOffset.UtcNow.AddDays(-2);
        rec.CreatedAt = created;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var dto = Assert.IsType<RecordingDetailDto>((await controller.Get(rec.Id)).Value);
        Assert.Equal(created.AddDays(30), dto.AudioScheduledDeletionAt);
    }

    [Fact]
    public async Task Get_NoAudioScheduledDeletion_WhenProtected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.PlatformSettings.Add(new Diariz.Domain.Entities.PlatformSettings
        {
            Id = Diariz.Domain.Entities.PlatformSettings.SingletonId,
            AutoDeleteAudioEnabled = true, AudioRetentionDays = 30,
        });
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.AudioProtectedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        var dto = Assert.IsType<RecordingDetailDto>((await controller.Get(rec.Id)).Value);
        Assert.Null(dto.AudioScheduledDeletionAt);
    }

    [Fact]
    public async Task Get_NoAudioScheduledDeletion_WhenAutoDeleteDisabled()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        // No PlatformSettings row seeded (auto-delete effectively off).
        var rec = await SeedRecording(db, userId, versions: 1);
        var controller = Build(db, userId, new FakeJobQueue());

        var dto = Assert.IsType<RecordingDetailDto>((await controller.Get(rec.Id)).Value);
        Assert.Null(dto.AudioScheduledDeletionAt);
    }

    [Fact]
    public async Task DeleteAudio_WhenProtected_ReturnsConflict_AndKeepsBlob()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var rec = await SeedRecording(db, userId, versions: 1);
        rec.AudioProtectedAt = DateTimeOffset.UtcNow;
        rec.SizeBytes = 500;
        storage.Objects[rec.BlobKey] = Encoding.UTF8.GetBytes("audio");
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.DeleteAudio(rec.Id);

        Assert.IsType<ConflictObjectResult>(result);
        var reloaded = (await db.Recordings.FindAsync(rec.Id))!;
        Assert.True(reloaded.HasAudio);                       // not deleted
        Assert.Equal(500, reloaded.SizeBytes);
        Assert.True(storage.Objects.ContainsKey(rec.BlobKey)); // blob kept
    }

    [Fact]
    public async Task DeleteAudioBulk_SkipsProtected()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        var unprotected = await SeedRecording(db, userId, versions: 1);
        var protectedRec = await SeedRecording(db, userId, versions: 1);
        unprotected.BlobKey = $"{userId}/unprotected.webm"; // SeedRecording reuses one key per user
        protectedRec.BlobKey = $"{userId}/protected.webm";
        protectedRec.AudioProtectedAt = DateTimeOffset.UtcNow;
        storage.Objects[unprotected.BlobKey] = Encoding.UTF8.GetBytes("a");
        storage.Objects[protectedRec.BlobKey] = Encoding.UTF8.GetBytes("b");
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        await controller.DeleteAudioBulk(new DeleteAudioRequest([unprotected.Id, protectedRec.Id]));

        Assert.False(storage.Objects.ContainsKey(unprotected.BlobKey)); // deleted
        Assert.True(storage.Objects.ContainsKey(protectedRec.BlobKey));  // protected, kept
        Assert.True((await db.Recordings.FindAsync(protectedRec.Id))!.HasAudio);
    }

    // ---- Merge transcripts (+ audio concatenation) ----

    private static async Task<Recording> SeedMergeable(
        DiarizDbContext db, Guid userId, DateTimeOffset createdAt, long durationMs, string text)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, BlobKey = $"{userId}/{Guid.NewGuid():N}.webm",
            Status = RecordingStatus.Transcribed, CreatedAt = createdAt, DurationMs = durationMs,
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = durationMs, Original = text, Ordinal = 0 });
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00" });
        await db.SaveChangesAsync();
        return rec;
    }

    [Fact]
    public async Task Merge_FewerThanTwo_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 1000, "a");
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.IsType<BadRequestObjectResult>(await controller.Merge(new MergeRecordingsRequest([rec.Id])));
    }

    [Fact]
    public async Task Merge_NotAllOwned_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var mine = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 1000, "a");
        var theirs = await SeedMergeable(db, Guid.NewGuid(), DateTimeOffset.UtcNow, 1000, "b");
        var controller = Build(db, userId, new FakeJobQueue());

        Assert.IsType<NotFoundResult>(await controller.Merge(new MergeRecordingsRequest([mine.Id, theirs.Id])));
    }

    [Fact]
    public async Task Merge_WhenOneSourceHasNoAudio_StillEnqueues_WithOnlyAudioPresentBlobs()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var early = await SeedMergeable(db, userId, DateTimeOffset.UtcNow.AddMinutes(-5), 1000, "Hello"); // has audio
        var later = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 2000, "World");
        later.AudioDeletedAt = DateTimeOffset.UtcNow; later.SizeBytes = 0; // audio gone — transcript only
        await db.SaveChangesAsync();
        var controller = Build(db, userId, queue);

        var result = await controller.Merge(new MergeRecordingsRequest([later.Id, early.Id]));

        Assert.IsType<AcceptedResult>(result);
        var survivor = (await db.Recordings.FindAsync(early.Id))!;
        Assert.Equal(RecordingStatus.Merging, survivor.Status);
        // The transcript still lays both end-to-end (offset by the audio-less source's retained duration)...
        var merged = await db.Transcriptions.Where(t => t.RecordingId == early.Id).OrderByDescending(t => t.Version).FirstAsync();
        var segs = await db.Segments.Where(s => s.TranscriptionId == merged.Id).OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal(["Hello", "World"], segs.Select(s => s.Original));
        // ...but only the audio-present source's blob is concatenated.
        var job = Assert.Single(queue.AudioMergeEnqueued);
        Assert.Equal([early.BlobKey], job.BlobKeys);
        Assert.Equal([later.Id], job.DeleteRecordingIds);
    }

    [Fact]
    public async Task Merge_WhenNoSourceHasAudio_FinishesSynchronously_AndDeletesSources()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var early = await SeedMergeable(db, userId, DateTimeOffset.UtcNow.AddMinutes(-5), 1000, "Hello");
        var later = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 2000, "World");
        foreach (var r in new[] { early, later }) { r.AudioDeletedAt = DateTimeOffset.UtcNow; r.SizeBytes = 0; }
        await db.SaveChangesAsync();
        var controller = Build(db, userId, queue);

        var result = await controller.Merge(new MergeRecordingsRequest([later.Id, early.Id]));

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(queue.AudioMergeEnqueued);                          // no audio to stitch
        var survivor = (await db.Recordings.FindAsync(early.Id))!;
        Assert.Equal(RecordingStatus.Transcribed, survivor.Status);      // settled immediately
        Assert.Null(await db.Recordings.FindAsync(later.Id));            // source deleted synchronously
        var merged = await db.Transcriptions.Where(t => t.RecordingId == early.Id).OrderByDescending(t => t.Version).FirstAsync();
        var segs = await db.Segments.Where(s => s.TranscriptionId == merged.Id).OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal(["Hello", "World"], segs.Select(s => s.Original));
    }

    [Fact]
    public async Task Merge_SyncPath_MovesSourceAttachmentsOntoSurvivor_WithoutFreeingBlobs()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var storage = new FakeAudioStorage();
        // No audio on either → synchronous merge that deletes the source row.
        var early = await SeedMergeable(db, userId, DateTimeOffset.UtcNow.AddMinutes(-5), 1000, "Hello");
        var later = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 2000, "World");
        foreach (var r in new[] { early, later }) { r.AudioDeletedAt = DateTimeOffset.UtcNow; r.SizeBytes = 0; }
        var sKey = $"{userId}/attachments/{Guid.NewGuid()}.pdf";  // survivor's own attachment
        var m1 = $"{userId}/attachments/{Guid.NewGuid()}.pdf";    // source file attachment
        db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), RecordingId = early.Id, Kind = AttachmentKind.File, Name = "survivor.pdf", BlobKey = sKey, Ordinal = 0 });
        db.Attachments.AddRange(
            new Attachment { Id = Guid.NewGuid(), RecordingId = later.Id, Kind = AttachmentKind.File, Name = "src1.pdf", BlobKey = m1, Ordinal = 0 },
            new Attachment { Id = Guid.NewGuid(), RecordingId = later.Id, Kind = AttachmentKind.Url, Name = "link", Url = "https://x.test", Ordinal = 1 });
        storage.Objects[sKey] = Encoding.UTF8.GetBytes("a");
        storage.Objects[m1] = Encoding.UTF8.GetBytes("b");
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue(), storage);

        var result = await controller.Merge(new MergeRecordingsRequest([later.Id, early.Id]));

        Assert.IsType<AcceptedResult>(result);
        Assert.Null(await db.Recordings.FindAsync(later.Id));                 // source removed
        // The source's attachments now hang off the survivor, appended after its own — nothing orphaned.
        var moved = await db.Attachments.Where(a => a.RecordingId == early.Id).OrderBy(a => a.Ordinal).ToListAsync();
        Assert.Equal(["survivor.pdf", "src1.pdf", "link"], moved.Select(a => a.Name));
        Assert.Equal([0, 1, 2], moved.Select(a => a.Ordinal));
        Assert.Empty(await db.Attachments.Where(a => a.RecordingId == later.Id).ToListAsync());
        Assert.True(storage.Objects.ContainsKey(sKey)); // blobs kept (still referenced by the survivor)
        Assert.True(storage.Objects.ContainsKey(m1));
    }

    [Fact]
    public async Task Merge_AsyncPath_MovesSourceAttachmentsOntoSurvivor_BeforeEnqueue()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        var early = await SeedMergeable(db, userId, DateTimeOffset.UtcNow.AddMinutes(-5), 1000, "Hello"); // has audio
        var later = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 2000, "World");                 // has audio
        var m1 = $"{userId}/attachments/{Guid.NewGuid()}.pdf";
        db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), RecordingId = later.Id, Kind = AttachmentKind.File, Name = "src1.pdf", BlobKey = m1, Ordinal = 0 });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, queue);

        var result = await controller.Merge(new MergeRecordingsRequest([later.Id, early.Id]));

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(queue.AudioMergeEnqueued); // async (worker still deletes the source row later)
        // The reassignment happens in the controller, so by callback time the source has no attachments to orphan.
        Assert.Equal("src1.pdf", (await db.Attachments.SingleAsync(a => a.RecordingId == early.Id)).Name);
        Assert.Empty(await db.Attachments.Where(a => a.RecordingId == later.Id).ToListAsync());
    }

    [Fact]
    public async Task Merge_AppendsActionItemsFromTheOtherSources()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var early = await SeedMergeable(db, userId, DateTimeOffset.UtcNow.AddMinutes(-5), 1000, "Hello");
        var later = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 2000, "World");
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = early.Id, Text = "Survivor task", Ordinal = 0 });
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = later.Id, Text = "Folded-in task", Ordinal = 0 });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, new FakeJobQueue());

        await controller.Merge(new MergeRecordingsRequest([later.Id, early.Id]));

        var actions = await db.RecordingActions.Where(a => a.RecordingId == early.Id).OrderBy(a => a.Ordinal).ToListAsync();
        Assert.Equal(["Survivor task", "Folded-in task"], actions.Select(a => a.Text));
        Assert.NotNull((await db.Recordings.FindAsync(early.Id))!.ActionsExtractedAt);
    }

    [Fact]
    public async Task Merge_IntoEarliest_BuildsMergedTranscript_SetsMerging_AndEnqueuesJob()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var queue = new FakeJobQueue();
        // 'later' is selected first but 'early' (older) must become the survivor.
        var later = await SeedMergeable(db, userId, DateTimeOffset.UtcNow, 2000, "World");
        var early = await SeedMergeable(db, userId, DateTimeOffset.UtcNow.AddMinutes(-5), 1000, "Hello");
        var controller = Build(db, userId, queue);

        var result = await controller.Merge(new MergeRecordingsRequest([later.Id, early.Id]));

        Assert.IsType<AcceptedResult>(result);

        // Survivor = earliest; gets a new (v2) transcription whose segments are laid end-to-end.
        var survivor = (await db.Recordings.FindAsync(early.Id))!;
        Assert.Equal(RecordingStatus.Merging, survivor.Status);
        var merged = await db.Transcriptions.Where(t => t.RecordingId == early.Id).OrderByDescending(t => t.Version).FirstAsync();
        Assert.Equal(2, merged.Version);
        var segs = await db.Segments.Where(s => s.TranscriptionId == merged.Id).OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal(["Hello", "World"], segs.Select(s => s.Original));
        Assert.Equal(0, segs[0].StartMs);
        Assert.Equal(1000, segs[1].StartMs); // shifted by the survivor's 1000 ms duration
        Assert.Equal(["S1-SPEAKER_00", "S2-SPEAKER_00"], segs.Select(s => s.SpeakerLabel));

        var job = Assert.Single(queue.AudioMergeEnqueued);
        Assert.Equal(early.Id, job.RecordingId);
        Assert.Equal([early.BlobKey, later.BlobKey], job.BlobKeys); // chronological
        Assert.Equal([later.Id], job.DeleteRecordingIds);           // only the non-survivors
        Assert.StartsWith($"{userId}/{early.Id}-merged-", job.OutputKey);
    }

    // ---- apply meeting type ----

    private static async Task<(Guid recId, Guid trId)> SeedRecWithTranscription(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k", Status = RecordingStatus.Summarized };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "w", Version = 1 };
        db.AddRange(rec, tr);
        await db.SaveChangesAsync();
        return (rec.Id, tr.Id);
    }

    private static Guid AddPlatformType(DiarizDbContext db)
    {
        var t = new MeetingType
        {
            Id = Guid.NewGuid(), UserId = null, GroupName = "Standard", Title = "Cadence",
            Icon = "refresh", Color = "#F09300", ContentJson = new MeetingTypeContent([]).Serialize(),
        };
        db.MeetingTypes.Add(t);
        db.SaveChanges();
        return t.Id;
    }

    [Fact]
    public async Task ApplyMeetingType_SetsType_AndEnqueuesMinutes()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (recId, trId) = await SeedRecWithTranscription(db, userId);
        var typeId = AddPlatformType(db);
        var queue = new FakeJobQueue();

        var result = await Build(db, userId, queue).ApplyMeetingType(recId, new ApplyMeetingTypeRequest(typeId));

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(typeId, (await db.Recordings.FindAsync(recId))!.MeetingTypeId);
        Assert.Equal(trId, Assert.Single(queue.MeetingMinutesEnqueued).TranscriptionId);
    }

    [Fact]
    public async Task ApplyMeetingType_Null_ResetsToDefault_AndEnqueues()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (recId, _) = await SeedRecWithTranscription(db, userId);
        (await db.Recordings.FindAsync(recId))!.MeetingTypeId = AddPlatformType(db);
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        var result = await Build(db, userId, queue).ApplyMeetingType(recId, new ApplyMeetingTypeRequest(null));

        Assert.IsType<AcceptedResult>(result);
        Assert.Null((await db.Recordings.FindAsync(recId))!.MeetingTypeId);
        Assert.Single(queue.MeetingMinutesEnqueued);
    }

    [Fact]
    public async Task ApplyMeetingType_UnusableType_ReturnsNotFound_AndDoesNotEnqueue()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (recId, _) = await SeedRecWithTranscription(db, userId);
        // A Personal type owned by someone else.
        var other = new MeetingType
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), GroupName = "G", Title = "T",
            Icon = "document", Color = "#5C6BC0", ContentJson = new MeetingTypeContent([]).Serialize(),
        };
        db.MeetingTypes.Add(other);
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        var result = await Build(db, userId, queue).ApplyMeetingType(recId, new ApplyMeetingTypeRequest(other.Id));

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(queue.MeetingMinutesEnqueued);
        Assert.Null((await db.Recordings.FindAsync(recId))!.MeetingTypeId);
    }

    [Fact]
    public async Task ApplyMeetingType_NotOwnedRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var (recId, _) = await SeedRecWithTranscription(db, Guid.NewGuid()); // owned by someone else
        var result = await Build(db, Guid.NewGuid(), new FakeJobQueue())
            .ApplyMeetingType(recId, new ApplyMeetingTypeRequest(null));
        Assert.IsType<NotFoundResult>(result);
    }
}
