using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Diariz.Api.Configuration;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class AudioStorageIntegrationTests(ContainersFixture fx)
{
    // Mirrors the S3 client wiring in Program.cs (path-style, us-east-1) against the MinIO container.
    private AudioStorage CreateStorage(out StorageOptions opts)
    {
        opts = new StorageOptions
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

    [Fact]
    public async Task EnsureBucket_ThenUpload_ThenOpenRead_RoundTripsBytes()
    {
        var storage = CreateStorage(out _);
        await storage.EnsureBucketAsync();

        var key = $"{Guid.NewGuid()}/audio.webm";
        var payload = Encoding.UTF8.GetBytes("pretend this is webm audio");

        using (var input = new MemoryStream(payload))
            await storage.UploadAsync(key, input, "audio/webm");

        await using var read = await storage.OpenReadAsync(key);
        using var output = new MemoryStream();
        await read.CopyToAsync(output);

        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task EnsureBucket_IsIdempotent()
    {
        var storage = CreateStorage(out _);
        await storage.EnsureBucketAsync();
        await storage.EnsureBucketAsync(); // must not throw when the bucket already exists
    }

    [Fact]
    public async Task OpenAsync_WithByteRange_ReturnsThatSlice()
    {
        var storage = CreateStorage(out _);
        await storage.EnsureBucketAsync();

        var key = $"{Guid.NewGuid()}/audio.webm";
        var payload = Encoding.UTF8.GetBytes("0123456789");
        using (var input = new MemoryStream(payload))
            await storage.UploadAsync(key, input, "audio/webm");

        var blob = await storage.OpenAsync(key, 2, 5);
        Assert.NotNull(blob);
        using var output = new MemoryStream();
        await blob!.Content.CopyToAsync(output);

        Assert.Equal("2345", Encoding.UTF8.GetString(output.ToArray())); // inclusive byte range 2..5
        Assert.Equal(4, blob.Length);
    }

    [Fact]
    public async Task OpenAsync_MissingKey_ReturnsNull()
    {
        var storage = CreateStorage(out _);
        await storage.EnsureBucketAsync();
        Assert.Null(await storage.OpenAsync($"{Guid.NewGuid()}/missing.webm"));
    }
}
