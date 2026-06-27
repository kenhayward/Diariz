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
    private static SpeakerProfilesController Build(DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

    private static async Task<Recording> SeedRecording(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    // ---- List ----

    [Fact]
    public async Task List_ReturnsOnlyCallersProfiles()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.SpeakerProfiles.Add(new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice", SampleCount = 2 });
        db.SpeakerProfiles.Add(new SpeakerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Someone else" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var profiles = await controller.List();

        var dto = Assert.Single(profiles);
        Assert.Equal("Alice", dto.Name);
        Assert.Equal(2, dto.SampleCount);
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

        var profile = await db.SpeakerProfiles.SingleAsync();
        Assert.Equal(userId, profile.UserId);
        Assert.Single(await db.ProfileContributions.Where(c => c.ProfileId == profile.Id).ToListAsync());

        var sp = await db.Speakers.SingleAsync(s => s.Id == speaker.Id);
        Assert.Equal(profile.Id, sp.ProfileId);
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
        Assert.Empty(await db.SpeakerProfiles.ToListAsync());
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
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        db.SpeakerProfiles.Add(profile);
        // One auto-identified speaker (revert) and one manually-renamed-then-assigned speaker (keep name).
        var auto = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", ProfileId = profile.Id, IdentifiedAuto = true
        };
        var manual = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01",
            DisplayName = "Alice", ProfileId = profile.Id, IdentifiedAuto = false
        };
        db.Speakers.AddRange(auto, manual);
        db.ProfileContributions.Add(new ProfileContribution
        {
            Id = Guid.NewGuid(), ProfileId = profile.Id, SpeakerId = manual.Id, RecordingId = rec.Id
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.Delete(profile.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.SpeakerProfiles.ToListAsync());
        Assert.Empty(await db.ProfileContributions.ToListAsync()); // cascaded

        var autoReloaded = await db.Speakers.SingleAsync(s => s.Id == auto.Id);
        Assert.Null(autoReloaded.ProfileId);
        Assert.Equal("SPEAKER_00", autoReloaded.DisplayName); // reverted to label
        Assert.False(autoReloaded.IdentifiedAuto);

        var manualReloaded = await db.Speakers.SingleAsync(s => s.Id == manual.Id);
        Assert.Null(manualReloaded.ProfileId);
        Assert.Equal("Alice", manualReloaded.DisplayName); // hand-assigned name kept
    }

    [Fact]
    public async Task Delete_OnAnotherUsersProfile_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, Guid.NewGuid());

        var result = await controller.Delete(profile.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.Single(await db.SpeakerProfiles.ToListAsync());
    }

    // ---- Rename ----

    [Fact]
    public async Task Rename_UpdatesName_AndLinkedSpeakerDisplayNames()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        db.SpeakerProfiles.Add(profile);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", ProfileId = profile.Id, IdentifiedAuto = true
        });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.Rename(profile.Id, new RenameSpeakerProfileRequest("Alice Smith"));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("Alice Smith", (await db.SpeakerProfiles.SingleAsync()).Name);
        Assert.Equal("Alice Smith", (await db.Speakers.SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task Rename_BlankName_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        Assert.IsType<BadRequestObjectResult>(await controller.Rename(profile.Id, new RenameSpeakerProfileRequest("  ")));
    }

    [Fact]
    public async Task Rename_OnAnotherUsersProfile_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(await controller.Rename(profile.Id, new RenameSpeakerProfileRequest("X")));
    }

    // ---- Get (detail) ----

    [Fact]
    public async Task Get_ReturnsContributionsAndIdentifiedCount()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        rec.Name = "Team Sync";
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice", SampleCount = 1 };
        db.SpeakerProfiles.Add(profile);
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", ProfileId = profile.Id
        };
        db.Speakers.Add(speaker);
        db.ProfileContributions.Add(new ProfileContribution
        {
            Id = Guid.NewGuid(), ProfileId = profile.Id, SpeakerId = speaker.Id, RecordingId = rec.Id
        });
        // The speaker's first segment is at 3s — that's the play offset surfaced to the UI.
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Transcriptions.Add(tr);
        db.Segments.AddRange(
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 5000, EndMs = 6000, Text = "later", Ordinal = 1 },
            new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 3000, EndMs = 4000, Text = "first", Ordinal = 0 });
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
    public async Task Get_OnAnotherUsersProfile_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>((await controller.Get(profile.Id)).Result);
    }

    // ---- Remove contribution ----

    [Fact]
    public async Task RemoveContribution_RemovesIt_AndDecrementsSampleCount()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice", SampleCount = 2 };
        db.SpeakerProfiles.Add(profile);
        var c1 = new ProfileContribution { Id = Guid.NewGuid(), ProfileId = profile.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id };
        var c2 = new ProfileContribution { Id = Guid.NewGuid(), ProfileId = profile.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id };
        db.ProfileContributions.AddRange(c1, c2);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.RemoveContribution(profile.Id, c1.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Single(await db.ProfileContributions.Where(c => c.ProfileId == profile.Id).ToListAsync());
        Assert.Equal(1, (await db.SpeakerProfiles.SingleAsync()).SampleCount);
    }

    [Fact]
    public async Task RemoveContribution_LastOne_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice", SampleCount = 1 };
        db.SpeakerProfiles.Add(profile);
        var only = new ProfileContribution { Id = Guid.NewGuid(), ProfileId = profile.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id };
        db.ProfileContributions.Add(only);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.RemoveContribution(profile.Id, only.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Single(await db.ProfileContributions.ToListAsync()); // not removed
    }

    [Fact]
    public async Task RemoveContribution_OnAnotherUsersProfile_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.SpeakerProfiles.Add(profile);
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
        var target = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice", SampleCount = 1 };
        var source = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Allie", SampleCount = 1 };
        db.SpeakerProfiles.AddRange(target, source);
        db.ProfileContributions.AddRange(
            new ProfileContribution { Id = Guid.NewGuid(), ProfileId = target.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id },
            new ProfileContribution { Id = Guid.NewGuid(), ProfileId = source.Id, SpeakerId = Guid.NewGuid(), RecordingId = rec.Id });
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01",
            DisplayName = "Allie", ProfileId = source.Id, IdentifiedAuto = true
        };
        db.Speakers.Add(speaker);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.Merge(target.Id, new MergeSpeakerProfilesRequest(source.Id));

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await db.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == source.Id));
        Assert.Equal(2, await db.ProfileContributions.CountAsync(c => c.ProfileId == target.Id));
        Assert.Equal(2, (await db.SpeakerProfiles.SingleAsync(p => p.Id == target.Id)).SampleCount);
        var sp = await db.Speakers.SingleAsync(s => s.Id == speaker.Id);
        Assert.Equal(target.Id, sp.ProfileId);
        Assert.Equal("Alice", sp.DisplayName);
    }

    [Fact]
    public async Task Merge_IntoItself_ReturnsBadRequest()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var profile = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        db.SpeakerProfiles.Add(profile);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        Assert.IsType<BadRequestObjectResult>(await controller.Merge(profile.Id, new MergeSpeakerProfilesRequest(profile.Id)));
    }

    [Fact]
    public async Task Merge_WhenSourceOwnedByAnotherUser_ReturnsNotFound()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var target = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        var othersSource = new SpeakerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" };
        db.SpeakerProfiles.AddRange(target, othersSource);
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        Assert.IsType<NotFoundResult>(await controller.Merge(target.Id, new MergeSpeakerProfilesRequest(othersSource.Id)));
        Assert.Equal(2, await db.SpeakerProfiles.CountAsync());
    }

    // ---- Erase all ----

    [Fact]
    public async Task DeleteAll_RemovesAllProfiles_RevertsAutoLabels_KeepsManualNames()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecording(db, userId);
        var a = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Alice" };
        var b = new SpeakerProfile { Id = Guid.NewGuid(), UserId = userId, Name = "Bob" };
        db.SpeakerProfiles.AddRange(a, b);
        db.Speakers.AddRange(
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice", ProfileId = a.Id, IdentifiedAuto = true },
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Bob", ProfileId = b.Id, IdentifiedAuto = false });
        // Another user's profile must be untouched.
        db.SpeakerProfiles.Add(new SpeakerProfile { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Name = "Theirs" });
        await db.SaveChangesAsync();
        var controller = Build(db, userId);

        var result = await controller.DeleteAll();

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.SpeakerProfiles.Where(p => p.UserId == userId).ToListAsync());
        Assert.Single(await db.SpeakerProfiles.ToListAsync()); // the other user's profile remains

        var auto = await db.Speakers.SingleAsync(s => s.Label == "SPEAKER_00");
        Assert.Null(auto.ProfileId);
        Assert.Equal("SPEAKER_00", auto.DisplayName); // reverted
        var manual = await db.Speakers.SingleAsync(s => s.Label == "SPEAKER_01");
        Assert.Null(manual.ProfileId);
        Assert.Equal("Bob", manual.DisplayName); // kept
    }
}
