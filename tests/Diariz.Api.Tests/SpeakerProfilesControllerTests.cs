using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Diariz.Api.Tests;

public class SpeakerProfilesControllerTests
{
    private static SpeakerProfilesController Build(
        DiarizDbContext db, Guid userId, PlatformPermission perms = PlatformPermission.ManagePeople)
    {
        Users.Ensure(db, userId); // create paths mint the owner's personal room, which needs a real user row
        // The directory is platform-wide, so reading and destructive writes need ManagePeople. Default it on
        // here so each test says what it is actually about; the gate itself is covered by
        // PeopleBiometricGateTests.
        Perms.Grant(db, userId, perms);
        return new(db, new Diariz.Api.Services.RoomScope(db), new Diariz.Api.Services.PeopleDirectory(db),
            new Diariz.Api.Services.UserPermissions(db))
        {
            ControllerContext = Http.Context(userId),
        };
    }

    // Voiceprints are scoped by their owner's personal room now; mint it so a seeded profile is findable.
    private static Guid RoomOf(DiarizDbContext db, Guid owner)
    {
        Users.Ensure(db, owner);
        return new Diariz.Api.Services.RoomScope(db).PersonalRoomIdAsync(owner).GetAwaiter().GetResult();
    }

    private static async Task<Recording> SeedRecording(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    // ---- List ----

    /// <summary>The directory is platform-wide: one human is one row, whoever enrolled them. This assertion
    /// is the inverse of the one it replaced (which required a profile to belong to the caller) - that
    /// inversion is the whole point of the scope change.</summary>
    [Fact]
    public async Task List_ReturnsPeopleEnrolledByAnyone()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.People.Add(new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice", SampleCount = 2 });
        db.People.Add(new Person { Id = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Someone else" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var profiles = Assert.IsAssignableFrom<IReadOnlyList<SpeakerProfileDto>>(
            Assert.IsType<OkObjectResult>((await controller.List()).Result).Value);

        Assert.Equal(2, profiles.Count);
        Assert.Contains(profiles, p => p.Name == "Alice" && p.SampleCount == 2);
        Assert.Contains(profiles, p => p.Name == "Someone else");
    }

    // ---- Create ----

    [Fact]
    public async Task Create_FromRecordingSpeaker_SeedsProfileContributionAndAssigns()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "SPEAKER_00", Embedding = new Vector(new float[] { 1f, 0f })
        };
        db.Speakers.Add(speaker);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.Create(new CreateSpeakerProfileRequest("Alice", rec.Id, "SPEAKER_00"));

        var dto = Assert.IsType<SpeakerProfileDto>(result.Value);
        Assert.Equal("Alice", dto.Name);
        Assert.Equal(1, dto.SampleCount);

        var profile = await db.People.SingleAsync();
        Assert.Equal(userId, profile.CreatedByUserId);
        Assert.Single(await db.VoiceSamples.Where(c => c.PersonId == profile.Id).ToListAsync());

        var sp = await db.Speakers.SingleAsync(s => s.Id == speaker.Id);
        Assert.Equal(profile.Id, sp.PersonId);
        Assert.Equal("Alice", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Create_WithoutEmbedding_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.Create(new CreateSpeakerProfileRequest("Alice", rec.Id, "SPEAKER_00"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await db.People.ToListAsync());
    }

    [Fact]
    public async Task Create_BlankName_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var controller = Build(db, userId);

        var result = await controller.Create(new CreateSpeakerProfileRequest("   ", rec.Id, "SPEAKER_00"));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_OnAnotherUsersRecording_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var rec = await SeedRecording(db, Guid.NewGuid());
        var controller = Build(db, Guid.NewGuid());

        var result = await controller.Create(new CreateSpeakerProfileRequest("Alice", rec.Id, "SPEAKER_00"));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Create_UnknownLabel_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var controller = Build(db, userId);

        var result = await controller.Create(new CreateSpeakerProfileRequest("Alice", rec.Id, "SPEAKER_99"));

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- Delete (GDPR erase) ----

    [Fact]
    public async Task Delete_RevertsAutoLabels_KeepsManualNames_AndUnlinks()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice" };
        db.People.Add(profile);
        // One auto-identified speaker (revert) and one manually-renamed-then-assigned speaker (keep name).
        var auto = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", PersonId = profile.Id, IdentifiedAuto = true
        };
        var manual = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01",
            DisplayName = "Alice", PersonId = profile.Id, IdentifiedAuto = false
        };
        db.Speakers.AddRange(auto, manual);
        db.VoiceSamples.Add(new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = profile.Id, SpeakerId = manual.Id, RecordingId = rec.Id
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.Delete(profile.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.People.ToListAsync());
        Assert.Empty(await db.VoiceSamples.ToListAsync()); // cascaded

        var autoReloaded = await db.Speakers.SingleAsync(s => s.Id == auto.Id);
        Assert.Null(autoReloaded.PersonId);
        Assert.Equal("SPEAKER_00", autoReloaded.DisplayName); // reverted to label
        Assert.False(autoReloaded.IdentifiedAuto);

        var manualReloaded = await db.Speakers.SingleAsync(s => s.Id == manual.Id);
        Assert.Null(manualReloaded.PersonId);
        Assert.Equal("Alice", manualReloaded.DisplayName); // hand-assigned name kept
    }

    [Fact]
    /// <summary>Inverted by platform scope: the directory is shared, so a person enrolled by someone
    /// else is a person you can act on. Authority now comes from the ManagePeople permission rather than
    /// from who happened to enrol them - see PeopleBiometricGateTests.</summary>
    public async Task Delete_OnAPersonEnrolledByAnotherUser_Succeeds()
    {
        using var db = TestDb.Create();
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Theirs" };
        db.People.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, Guid.NewGuid());

        var result = await controller.Delete(profile.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.People.ToListAsync());
    }

    // ---- Rename ----

    [Fact]
    public async Task Rename_UpdatesName_AndLinkedSpeakerDisplayNames()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice" };
        db.People.Add(profile);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", PersonId = profile.Id, IdentifiedAuto = true
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.Rename(profile.Id, new RenameSpeakerProfileRequest("Alice Smith"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Alice Smith", (await db.People.SingleAsync()).Name);
        Assert.Equal("Alice Smith", (await db.Speakers.SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task Rename_BlankName_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice" };
        db.People.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        Assert.IsType<BadRequestObjectResult>(await controller.Rename(profile.Id, new RenameSpeakerProfileRequest("  ")));
    }

    [Fact]
    /// <summary>Inverted by platform scope: the directory is shared, so a person enrolled by someone
    /// else is a person you can act on. Authority now comes from the ManagePeople permission rather than
    /// from who happened to enrol them - see PeopleBiometricGateTests.</summary>
    public async Task Rename_OnAPersonEnrolledByAnotherUser_Succeeds()
    {
        using var db = TestDb.Create();
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Theirs" };
        db.People.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, Guid.NewGuid());

        Assert.IsType<NoContentResult>(await controller.Rename(profile.Id, new RenameSpeakerProfileRequest("X")));
        Assert.Equal("X", (await db.People.SingleAsync()).Name);
    }

    // ---- Get (detail) ----

    [Fact]
    public async Task Get_ReturnsContributionsAndIdentifiedCount()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        rec.Name = "Team Sync";
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice", SampleCount = 1 };
        db.People.Add(profile);
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", PersonId = profile.Id
        };
        db.Speakers.Add(speaker);
        db.VoiceSamples.Add(new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = profile.Id, SpeakerId = speaker.Id, RecordingId = rec.Id
        });
        // The speaker's first segment is at 3s — that's the play offset surfaced to the UI.
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Transcriptions.Add(tr);
        db.Segments.AddRange(
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 5000, EndMs = 6000, Original = "later", Ordinal = 1 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 3000, EndMs = 4000, Original = "first", Ordinal = 0 });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var dto = Assert.IsType<SpeakerProfileDetailDto>((await controller.Get(profile.Id)).Value);

        Assert.Equal("Alice", dto.Name);
        Assert.Equal(1, dto.IdentifiedCount);
        var c = Assert.Single(dto.Contributions);
        Assert.Equal("Team Sync", c.RecordingName);
        Assert.Equal("SPEAKER_00", c.SpeakerLabel);
        Assert.Equal(3000, c.StartMs); // earliest segment for that speaker
    }

    [Fact]
    /// <summary>Inverted by platform scope: the directory is shared, so a person enrolled by someone
    /// else is a person you can act on. Authority now comes from the ManagePeople permission rather than
    /// from who happened to enrol them - see PeopleBiometricGateTests.</summary>
    public async Task Get_OnAPersonEnrolledByAnotherUser_Succeeds()
    {
        using var db = TestDb.Create();
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Theirs" };
        db.People.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, Guid.NewGuid());

        Assert.Equal("Theirs", (await controller.Get(profile.Id)).Value!.Name);
    }

    // ---- Remove contribution ----

    [Fact]
    public async Task RemoveContribution_RemovesIt_AndDecrementsSampleCount()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice", SampleCount = 2 };
        db.People.Add(profile);
        var c1 = new VoiceSample { Id = Guid.NewGuid(), PersonId = profile.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id };
        var c2 = new VoiceSample { Id = Guid.NewGuid(), PersonId = profile.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id };
        db.VoiceSamples.AddRange(c1, c2);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.RemoveContribution(profile.Id, c1.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Single(await db.VoiceSamples.Where(c => c.PersonId == profile.Id).ToListAsync());
        Assert.Equal(1, (await db.People.SingleAsync()).SampleCount);
    }

    [Fact]
    public async Task RemoveContribution_LastOne_ClearsTheVoiceprint()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice", SampleCount = 1 };
        db.People.Add(profile);
        var only = new VoiceSample { Id = Guid.NewGuid(), PersonId = profile.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id };
        db.VoiceSamples.Add(only);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.RemoveContribution(profile.Id, only.Id);

        // The 400 this used to return only existed because Embedding was NOT NULL. A person may now hold no
        // voiceprint, so removing the last sample simply clears it and leaves them in the directory.
        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.VoiceSamples.ToListAsync());
        var reloaded = await db.People.SingleAsync(p => p.Id == profile.Id);
        Assert.Equal(0, reloaded.SampleCount);
        Assert.NotNull(reloaded);
    }

    [Fact]
    public async Task RemoveContribution_OnAnotherUsersProfile_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Theirs" };
        db.People.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(await controller.RemoveContribution(profile.Id, Guid.NewGuid()));
    }

    // ---- Merge ----

    [Fact]
    public async Task Merge_MovesContributions_ReassignsSpeakers_AndDeletesSource()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var target = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice", SampleCount = 1 };
        var source = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Allie", SampleCount = 1 };
        db.People.AddRange(target, source);
        db.VoiceSamples.AddRange(
            new VoiceSample { Id = Guid.NewGuid(), PersonId = target.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id },
            new VoiceSample { Id = Guid.NewGuid(), PersonId = source.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id });
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01",
            DisplayName = "Allie", PersonId = source.Id, IdentifiedAuto = true
        };
        db.Speakers.Add(speaker);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.Merge(target.Id, new MergeSpeakerProfilesRequest(source.Id));

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.People.FirstOrDefaultAsync(p => p.Id == source.Id));
        Assert.Equal(2, await db.VoiceSamples.CountAsync(c => c.PersonId == target.Id));
        Assert.Equal(2, (await db.People.SingleAsync(p => p.Id == target.Id)).SampleCount);
        var sp = await db.Speakers.SingleAsync(s => s.Id == speaker.Id);
        Assert.Equal(target.Id, sp.PersonId);
        Assert.Equal("Alice", sp.DisplayName);
    }

    [Fact]
    public async Task Merge_IntoItself_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var profile = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice" };
        db.People.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        Assert.IsType<BadRequestObjectResult>(await controller.Merge(profile.Id, new MergeSpeakerProfilesRequest(profile.Id)));
    }

    [Fact]
    /// <summary>Inverted by platform scope: the directory is shared, so a person enrolled by someone
    /// else is a person you can act on. Authority now comes from the ManagePeople permission rather than
    /// from who happened to enrol them - see PeopleBiometricGateTests.</summary>
    public async Task Merge_FoldsInAPersonEnrolledByAnotherUser()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var target = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice" };
        var othersSource = new Person { Id = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Theirs" };
        db.People.AddRange(target, othersSource);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        // Two users independently enrolling the same human is precisely the duplicate that platform scope
        // surfaces, so merging across that boundary has to work.
        Assert.IsType<NoContentResult>(await controller.Merge(target.Id, new MergeSpeakerProfilesRequest(othersSource.Id)));
        Assert.Equal("Alice", (await db.People.SingleAsync()).Name);
    }

    // ---- Erase all ----

    /// <summary>Wiping the shared directory is a platform act, so it needs ManagePlatform rather than
    /// ManagePeople - and it now takes <em>everyone</em>, not just the people the caller enrolled. That last
    /// part is the inversion: this test used to assert another user's profile survived.</summary>
    [Fact]
    public async Task DeleteAll_RemovesEveryPerson_RevertsAutoLabels_KeepsManualNames()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var a = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Alice" };
        var b = new Person { Id = Guid.NewGuid(), CreatedByUserId = userId, RoomId = RoomOf(db, userId), Name = "Bob" };
        db.People.AddRange(a, b);
        db.Speakers.AddRange(
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice", PersonId = a.Id, IdentifiedAuto = true },
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Bob", PersonId = b.Id, IdentifiedAuto = false });
        // A person enrolled by someone else goes too - the directory is one shared thing.
        db.People.Add(new Person { Id = Guid.NewGuid(), CreatedByUserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Name = "Theirs" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId, PlatformPermission.ManagePlatform);

        var result = await controller.DeleteAll();

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.People.ToListAsync()); // everyone, including the person someone else enrolled

        var auto = await db.Speakers.SingleAsync(s => s.Label == "SPEAKER_00");
        Assert.Null(auto.PersonId);
        Assert.Equal("SPEAKER_00", auto.DisplayName); // reverted
        var manual = await db.Speakers.SingleAsync(s => s.Label == "SPEAKER_01");
        Assert.Null(manual.PersonId);
        Assert.Equal("Bob", manual.DisplayName); // kept
    }
}
