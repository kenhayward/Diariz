using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class RecordingTagStatusIntegrationTests(ContainersFixture fx)
{
    private async Task<Recording> SeedRecordingAsync()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = $"k/{Guid.NewGuid()}" };
        db.AddRange(user, rec);
        await db.SaveChangesAsync();
        return rec;
    }

    [Fact]
    public async Task ALegacyRowWithNoStatusColumnValue_LandsAsSuggested_WhichIsTheDemotion()
    {
        var rec = await SeedRecordingAsync();

        // Insert the way a pre-migration row existed: without naming Status at all. The demotion of every
        // existing tag rests on the COLUMN DEFAULT, and only a raw insert proves it - going through EF would
        // send Status = 0 from the C# property initialiser and pass even if the migration forgot the default.
        await using (var db = fx.CreateDbContext())
        {
            var id = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "RecordingTags" ("Id", "RecordingId", "Tag", "Weight", "Ordinal", "CreatedAt")
                VALUES ({id}, {rec.Id}, 'legacy-tag', 0.7, 0, now())
                """);
        }

        await using (var db = fx.CreateDbContext())
        {
            var saved = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
            Assert.Equal(RecordingTagStatus.Suggested, saved.Status);
            Assert.Null(saved.AdoptedAt);
        }
    }

    [Fact]
    public async Task NewTag_FromTheApp_DefaultsToSuggested_WithNoAdoptedAt()
    {
        var rec = await SeedRecordingAsync();

        await using (var db = fx.CreateDbContext())
        {
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Roadmap", Weight = 0.8, Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var saved = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
            Assert.Equal(RecordingTagStatus.Suggested, saved.Status);
            Assert.Null(saved.AdoptedAt);
        }
    }

    [Fact]
    public async Task AdoptedAt_RoundTripsAsUtc()
    {
        var rec = await SeedRecordingAsync();
        var when = DateTimeOffset.UtcNow;

        await using (var db = fx.CreateDbContext())
        {
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Adopted", Weight = 1.0, Ordinal = 0,
                Status = RecordingTagStatus.Adopted, AdoptedAt = when,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var saved = await db.RecordingTags.SingleAsync(t => t.RecordingId == rec.Id);
            Assert.Equal(RecordingTagStatus.Adopted, saved.Status);
            Assert.Equal(when.ToUniversalTime(), saved.AdoptedAt!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task CaseVariantDuplicate_OnTheSameRecording_IsRejected()
    {
        var rec = await SeedRecordingAsync();

        await using (var db = fx.CreateDbContext())
        {
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "metadata", Weight = 1.0, Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.RecordingTags.Add(new RecordingTag
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Tag = "Metadata", Weight = 1.0, Ordinal = 1,
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task SameTag_OnDifferentRecordings_IsAllowed()
    {
        var a = await SeedRecordingAsync();
        var b = await SeedRecordingAsync();

        await using var db = fx.CreateDbContext();
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = a.Id, Tag = "shared-word", Weight = 1.0, Ordinal = 0,
        });
        db.RecordingTags.Add(new RecordingTag
        {
            Id = Guid.NewGuid(), RecordingId = b.Id, Tag = "shared-word", Weight = 1.0, Ordinal = 0,
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.RecordingTags.CountAsync(t => t.Tag == "shared-word"));
    }
}
