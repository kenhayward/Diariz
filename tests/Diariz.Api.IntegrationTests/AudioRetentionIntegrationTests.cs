using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Diariz.Api.Configuration;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>End-to-end audio-retention sweep against real Postgres + MinIO: the eligible recording's blob is
/// deleted (row kept, flagged), while a protected recording is left untouched.</summary>
[Collection(IntegrationCollection.Name)]
public class AudioRetentionIntegrationTests(ContainersFixture fx)
{
    private AudioStorage CreateStorage()
    {
        var opts = new StorageOptions
        {
            Endpoint = fx.MinioEndpoint,
            AccessKey = fx.MinioAccessKey,
            SecretKey = fx.MinioSecretKey,
            Bucket = $"recordings-{Guid.NewGuid():N}",
            ForcePathStyle = true
        };
        var cfg = new AmazonS3Config
        {
            ServiceURL = opts.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1"
        };
        var s3 = new AmazonS3Client(new BasicAWSCredentials(opts.AccessKey, opts.SecretKey), cfg);
        return new AudioStorage(s3, Options.Create(opts));
    }

    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Sweep_DeletesEligibleBlob_KeepsProtected()
    {
        var storage = CreateStorage();
        await storage.EnsureBucketAsync();
        var userId = await SeedUser();
        var now = DateTimeOffset.UtcNow;

        var eligibleKey = $"{userId}/{Guid.NewGuid():N}.webm";
        var protectedKey = $"{userId}/{Guid.NewGuid():N}.webm";
        foreach (var key in new[] { eligibleKey, protectedKey })
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("audio")))
                await storage.UploadAsync(key, input, "audio/webm");

        var eligible = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, BlobKey = eligibleKey, SizeBytes = 5,
            Status = RecordingStatus.Transcribed, CreatedAt = now.AddDays(-40),
        };
        var prot = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, BlobKey = protectedKey, SizeBytes = 5,
            Status = RecordingStatus.Transcribed, CreatedAt = now.AddDays(-40), AudioProtectedAt = now.AddDays(-1),
        };
        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.AddRange(eligible, prot);
            await db.SaveChangesAsync();
        }

        int deleted;
        await using (var db = fx.CreateDbContext())
            deleted = await AudioRetentionSweep.RunAsync(db, storage, now, retentionDays: 30, NullLogger.Instance);

        Assert.Equal(1, deleted);
        Assert.Null(await storage.OpenAsync(eligibleKey));        // blob deleted
        Assert.NotNull(await storage.OpenAsync(protectedKey));    // protected blob kept

        await using var read = fx.CreateDbContext();
        var savedEligible = await read.Recordings.SingleAsync(r => r.Id == eligible.Id);
        Assert.NotNull(savedEligible.AudioDeletedAt);
        Assert.Equal(0, savedEligible.SizeBytes);
        var savedProtected = await read.Recordings.SingleAsync(r => r.Id == prot.Id);
        Assert.Null(savedProtected.AudioDeletedAt);
        Assert.Equal(5, savedProtected.SizeBytes);
    }
}
