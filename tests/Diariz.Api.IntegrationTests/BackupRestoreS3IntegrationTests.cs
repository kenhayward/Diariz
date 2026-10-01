using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>The server move restores a backup taken from one object store into another. This runs the real
/// controller over the real S3 store in both directions, so a listing, streaming or upload incompatibility
/// shows up here and not at cutover.</summary>
[Collection(IntegrationCollection.Name)]
public class BackupRestoreS3IntegrationTests(ContainersFixture fx)
{
    private const string Migration = "20260615111923_InitialCreate";

    private AudioStorage Storage(string bucket)
    {
        var opts = new StorageOptions
        {
            Endpoint = fx.S3Endpoint, AccessKey = fx.S3AccessKey, SecretKey = fx.S3SecretKey,
            Bucket = bucket, ForcePathStyle = true,
        };
        var s3 = new AmazonS3Client(new BasicAWSCredentials(opts.AccessKey, opts.SecretKey),
            new AmazonS3Config { ServiceURL = opts.Endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        return new AudioStorage(s3, Options.Create(opts));
    }

    private static MaintenanceController Controller(IAudioStorage storage) =>
        new(storage, new FakeDatabaseBackup { DumpBytes = "DUMP"u8.ToArray() },
            new FakeSchemaVersion(Migration), new BackupProgress())
        { ControllerContext = Http.Context(Guid.NewGuid(), [Roles.PlatformAdministrator]) };

    [Fact]
    public async Task BackupFromOneBucket_RestoresEveryObjectByteForByte_IntoAnother()
    {
        var source = Storage($"src-{Guid.NewGuid():N}");
        var target = Storage($"dst-{Guid.NewGuid():N}");
        await source.EnsureBucketAsync();
        await target.EnsureBucketAsync();

        // One object larger than the SDK's default part and buffer sizes, plus nested keys.
        var big = new byte[12 * 1024 * 1024];
        new Random(42).NextBytes(big);
        var objects = new Dictionary<string, byte[]>
        {
            [$"{Guid.NewGuid()}/rec.webm"] = big,
            [$"{Guid.NewGuid()}/attachments/notes.pdf"] = "PDF"u8.ToArray(),
            [$"{Guid.NewGuid()}/deep/a/b/c.wav"] = "WAV"u8.ToArray(),
        };
        foreach (var (key, bytes) in objects)
            using (var ms = new MemoryStream(bytes)) await source.UploadAsync(key, ms, "application/octet-stream");

        // Backup from the source bucket.
        var file = Assert.IsType<FileStreamResult>(await Controller(source).Backup());
        using var zipCopy = new MemoryStream();
        await using (file.FileStream) await file.FileStream.CopyToAsync(zipCopy);

        // Restore into the target bucket, through the raw-body path the admin UI and curl both use.
        var restoring = Controller(target);
        zipCopy.Position = 0;
        restoring.ControllerContext.HttpContext.Request.Body = zipCopy;
        var ok = Assert.IsType<OkObjectResult>(await restoring.Restore());

        var body = JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(objects.Count, body.GetProperty("objectsRestored").GetInt32());
        Assert.Equal(objects.Values.Sum(b => (long)b.Length), body.GetProperty("bytesRestored").GetInt64());

        var restoredKeys = new List<string>();
        await foreach (var k in target.ListKeysAsync()) restoredKeys.Add(k);
        Assert.Equal(objects.Keys.Order(), restoredKeys.Order());
        foreach (var (key, bytes) in objects)
        {
            await using var read = await target.OpenReadAsync(key);
            using var got = new MemoryStream();
            await read.CopyToAsync(got);
            Assert.Equal(bytes, got.ToArray());
        }
    }
}
