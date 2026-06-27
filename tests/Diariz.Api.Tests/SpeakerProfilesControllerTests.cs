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
}
