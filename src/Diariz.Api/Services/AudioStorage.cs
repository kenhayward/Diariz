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
        // AWS SDK v4 returns a null Buckets list (not an empty one) when none exist.
        var exists = buckets.Buckets?.Any(b => b.BucketName == _opts.Bucket) ?? false;
        if (!exists)
            await _s3.PutBucketAsync(_opts.Bucket, ct);
    }

    public async Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _opts.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType
            // NB: do NOT set DisablePayloadSigning here — AWS SDK v4 rejects it over
            // plain HTTP ("must be sent over HTTPS"). Normal SigV4 payload signing works
            // fine against MinIO over HTTP.
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
