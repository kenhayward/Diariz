using Amazon.S3;
using Amazon.S3.Model;
using Diariz.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

public interface IAudioStorage
{
    Task EnsureBucketAsync(CancellationToken ct = default);
    Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    /// <summary>Time-limited URL the client can use to download the original audio directly.</summary>
    string GetPresignedDownloadUrl(string key, TimeSpan expiry);
}

/// <summary>S3-compatible storage backed by MinIO. Stores original audio blobs.</summary>
public class AudioStorage : IAudioStorage
{
    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _opts;

    public AudioStorage(IAmazonS3 s3, IOptions<StorageOptions> opts)
    {
        _s3 = s3;
        _opts = opts.Value;
    }

    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        var buckets = await _s3.ListBucketsAsync(ct);
        if (!buckets.Buckets.Any(b => b.BucketName == _opts.Bucket))
            await _s3.PutBucketAsync(_opts.Bucket, ct);
    }

    public async Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opts.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            DisablePayloadSigning = true // required for MinIO over plain HTTP
        }, ct);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var resp = await _s3.GetObjectAsync(_opts.Bucket, key, ct);
        return resp.ResponseStream;
    }

    public string GetPresignedDownloadUrl(string key, TimeSpan expiry) =>
        _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _opts.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry)
        });
}
