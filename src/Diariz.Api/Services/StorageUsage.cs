using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Computes a user's used storage as the total bytes of their recorded audio plus any uploaded
/// attachment files (DB rows/derived data don't count toward the quota).</summary>
public interface IStorageUsage
{
    Task<long> UsedBytesAsync(Guid userId, CancellationToken ct = default);
}

public class StorageUsage(DiarizDbContext db) : IStorageUsage
{
    public async Task<long> UsedBytesAsync(Guid userId, CancellationToken ct = default)
    {
        var audio = await db.Recordings.Where(r => r.UserId == userId).SumAsync(r => r.SizeBytes, ct);
        var attachments = await db.Recordings
            .Where(r => r.UserId == userId)
            .SelectMany(r => r.Attachments)
            .SumAsync(a => a.SizeBytes, ct); // URL attachments are 0 bytes
        return audio + attachments;
    }
}
