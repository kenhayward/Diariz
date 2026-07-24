using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The signed-in user's storage usage vs quota (drives the account-menu storage line).</summary>
[ApiController]
[Authorize]
[Route("api/user/storage")]
public class StorageController : ControllerBase
{
    private readonly IStorageUsage _usage;
    private readonly DiarizDbContext _db;

    public StorageController(IStorageUsage usage, DiarizDbContext db)
    {
        _usage = usage;
        _db = db;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    [EndpointSummary("Get your storage usage")]
    [EndpointDescription(
        "How many bytes you are using against your quota, plus the total wall-clock time spent transcribing " +
        "your recordings.\n\n" +
        "Usage counts **stored bytes only** - audio, attachments and screenshots - so transcripts, summaries " +
        "and documents are free. Deleting a recording's audio while keeping its transcript therefore reclaims " +
        "space. Quotas are set by an administrator; this endpoint only reports.")]
    public async Task<StorageUsageDto> Get()
    {
        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        // Total wall-clock transcription time across all of the user's transcription versions.
        var transcriptionMs = await _db.Recordings
            .Where(r => r.UserId == UserId)
            .SelectMany(r => r.Transcriptions)
            .SumAsync(t => t.ProcessingMs ?? 0);
        return new StorageUsageDto(used, quota, transcriptionMs);
    }
}
