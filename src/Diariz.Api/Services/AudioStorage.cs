using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Diariz.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>A (possibly partial) audio blob opened for streaming back to a client.</summary>
public sealed record AudioBlob(Stream Content, long Length, string ContentType);

public interface IAudioStorage
{
    Task EnsureBucketAsync(CancellationToken ct = default);
    Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    /// <summary>Open the blob (optionally a byte range, inclusive) for streaming. Null when the key is absent.</summary>
    Task<AudioBlob?> OpenAsync(string key, long? from = null, long? to = null, CancellationToken ct = default);
    /// <summary>Size in bytes of the stored blob (a HEAD), or null when the key is absent.</summary>
    Task<long?> GetSizeAsync(string key, CancellationToken ct = default);
    /// <summary>Removes the stored blob. Idempotent — succeeds even if the key is absent.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>Enumerate every object key in the bucket (paginated). Used by the platform backup.</summary>
    IAsyncEnumerable<string> ListKeysAsync(CancellationToken ct = default);
}

/// <summary>S3-compatible storage backed by MinIO. Stores original audio blobs. The API streams audio
/// back to clients itself (same-origin) rather than handing out presigned URLs, so MinIO never needs to
/// be reachable from the browser.</summary>
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

    public async Task<AudioBlob?> OpenAsync(string key, long? from = null, long? to = null, CancellationToken ct = default)
    {
        var req = new GetObjectRequest { BucketName = _opts.Bucket, Key = key };
        if (from is not null)
            req.ByteRange = new ByteRange(from.Value, to ?? long.MaxValue); // S3 clamps the end to the object size

        try
        {
            var resp = await _s3.GetObjectAsync(req, ct);
            var contentType = string.IsNullOrEmpty(resp.Headers.ContentType) ? "application/octet-stream" : resp.Headers.ContentType;
            return new AudioBlob(resp.ResponseStream, resp.ContentLength, contentType);
        }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return null;
        }
    }

    public async Task<long?> GetSizeAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var meta = await _s3.GetObjectMetadataAsync(_opts.Bucket, key, ct);
            return meta.ContentLength;
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        _s3.DeleteObjectAsync(_opts.Bucket, key, ct);

    public async IAsyncEnumerable<string> ListKeysAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var req = new ListObjectsV2Request { BucketName = _opts.Bucket };
        ListObjectsV2Response resp;
        do
        {
            resp = await _s3.ListObjectsV2Async(req, ct);
            // AWS SDK v4 returns a null S3Objects list (not empty) when the bucket has no objects.
            foreach (var o in resp.S3Objects ?? [])
                yield return o.Key;
            req.ContinuationToken = resp.NextContinuationToken;
        } while (resp.IsTruncated == true);
    }
}
