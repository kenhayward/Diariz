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
    public async Task<StorageUsageDto> Get()
    {
        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        return new StorageUsageDto(used, quota);
    }
}
