using Amazon.Runtime;
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
    /// <summary>Removes the stored blob. Idempotent — succeeds even if the key is absent.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>Time-limited URL the client can use to download the original audio directly.
    /// When <paramref name="downloadFileName"/> is set, the URL forces a browser download with
    /// that filename (Content-Disposition: attachment) rather than inline playback.</summary>
    string GetPresignedDownloadUrl(string key, TimeSpan expiry, string? downloadFileName = null);
}

/// <summary>S3-compatible storage backed by MinIO. Stores original audio blobs.</summary>
public class AudioStorage : IAudioStorage
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonS3 _presignS3;
    private readonly Protocol _presignProtocol;
    private readonly StorageOptions _opts;

    public AudioStorage(IAmazonS3 s3, IOptions<StorageOptions> opts)
    {
        _s3 = s3;
        _opts = opts.Value;

        // Presigned URLs are consumed by the browser, which can't reach the internal `minio:9000`
        // host. When a distinct PublicEndpoint is configured, sign against it (the SigV4 signature is
        // host-specific, so a plain string replace on the URL would be rejected by MinIO).
        var publicEndpoint = string.IsNullOrWhiteSpace(_opts.PublicEndpoint) ? _opts.Endpoint : _opts.PublicEndpoint;
        // GetPreSignedURL defaults to https regardless of the ServiceURL scheme; MinIO is plain HTTP
        // locally, so pin the protocol explicitly from the endpoint.
        _presignProtocol = publicEndpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? Protocol.HTTP : Protocol.HTTPS;
        _presignS3 = publicEndpoint == _opts.Endpoint
            ? s3
            : new AmazonS3Client(
                new BasicAWSCredentials(_opts.AccessKey, _opts.SecretKey),
                new AmazonS3Config
                {
                    ServiceURL = publicEndpoint,
                    ForcePathStyle = _opts.ForcePathStyle,
                    AuthenticationRegion = "us-east-1"
                });
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

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        _s3.DeleteObjectAsync(_opts.Bucket, key, ct);

    public string GetPresignedDownloadUrl(string key, TimeSpan expiry, string? downloadFileName = null)
    {
        var req = new GetPreSignedUrlRequest
        {
            BucketName = _opts.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Protocol = _presignProtocol,
            Expires = DateTime.UtcNow.Add(expiry)
        };
        if (!string.IsNullOrEmpty(downloadFileName))
            req.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{downloadFileName}\"";
        return _presignS3.GetPreSignedURL(req);
    }
}
