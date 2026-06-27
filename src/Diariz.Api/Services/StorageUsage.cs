using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Computes a user's used storage as the total bytes of their recorded audio
/// (DB rows/derived data don't count toward the quota).</summary>
public interface IStorageUsage
{
    Task<long> UsedBytesAsync(Guid userId, CancellationToken ct = default);
}

public class StorageUsage(DiarizDbContext db) : IStorageUsage
{
    public Task<long> UsedBytesAsync(Guid userId, CancellationToken ct = default) =>
        db.Recordings.Where(r => r.UserId == userId).SumAsync(r => r.SizeBytes, ct);
}
