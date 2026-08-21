using Amazon.Runtime;
using Amazon.S3;
using Diariz.Api.Configuration;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Diariz.Api.IntegrationTests;

/// <summary>The vision attachment path against real MinIO and real Postgres.
///
/// <para>The unit tests use an in-memory storage fake, so they prove the encoder's logic but not that it
/// survives a genuine S3 round trip - and byte identity through MinIO is exactly the sort of thing a fake
/// cannot vouch for.</para></summary>
[Collection(IntegrationCollection.Name)]
public class VisionImageIntegrationTests(ContainersFixture fx)
{
    private AudioStorage CreateStorage()
    {
        var opts = new StorageOptions
        {
            Endpoint = fx.MinioEndpoint,
            AccessKey = fx.MinioAccessKey,
            SecretKey = fx.MinioSecretKey,
            Bucket = $"vision-{Guid.NewGuid():N}",
            ForcePathStyle = true,
        };
        var cfg = new AmazonS3Config
        {
            ServiceURL = opts.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
        };
        return new AudioStorage(new AmazonS3Client(new BasicAWSCredentials(opts.AccessKey, opts.SecretKey), cfg),
            Options.Create(opts));
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Crimson };
        canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (string MediaType, byte[] Bytes) Parse(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        var header = dataUrl[5..comma];
        return (header[..^";base64".Length], Convert.FromBase64String(dataUrl[(comma + 1)..]));
    }

    private async Task<(VisionImage Result, byte[] Stored)> EncodeStored(int width, int height)
    {
        var storage = CreateStorage();
        await storage.EnsureBucketAsync();
        var key = $"{Guid.NewGuid()}/shot.png";
        var stored = Png(width, height);
        using (var input = new MemoryStream(stored))
            await storage.UploadAsync(key, input, "image/png");

        var shot = new MeetingScreenshot { Id = Guid.NewGuid(), BlobKey = key, Width = width, Height = height };
        return (await new VisionImageEncoder(storage).EncodeAsync(shot), stored);
    }

    [Fact]
    public async Task OversizedCapture_ComesBackAsJpegInsideTheBox()
    {
        var (result, _) = await EncodeStored(3200, 2400);

        var (mediaType, bytes) = Parse(result.DataUrl);
        Assert.Equal("image/jpeg", mediaType);

        using var decoded = SKBitmap.Decode(bytes);
        Assert.True(decoded.Width <= VisionImageBounds.MaxWidth);
        Assert.True(decoded.Height <= VisionImageBounds.MaxHeight);
        Assert.Equal(decoded.Width, result.Width);
        Assert.Equal(decoded.Height, result.Height);
    }

    [Fact]
    public async Task SmallCapture_SurvivesTheRoundTripByteForByte()
    {
        var (result, stored) = await EncodeStored(1024, 768);

        var (mediaType, bytes) = Parse(result.DataUrl);
        Assert.Equal("image/png", mediaType);
        Assert.Equal(stored, bytes);
    }
}
