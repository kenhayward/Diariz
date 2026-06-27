using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>Platform-wide settings. Any administrator may read them (e.g. to learn the quota ceiling);
/// only the Platform Administrator may change them.</summary>
[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/platform/settings")]
public class PlatformSettingsController : ControllerBase
{
    private readonly IPlatformSettingsService _settings;
    private readonly DiarizDbContext _db;

    public PlatformSettingsController(IPlatformSettingsService settings, DiarizDbContext db)
    {
        _settings = settings;
        _db = db;
    }

    [HttpGet]
    public async Task<PlatformSettingsDto> Get()
    {
        var s = await _settings.GetAsync();
        return new PlatformSettingsDto(s.StarterQuotaBytes, s.MaxQuotaBytes);
    }

    [HttpPut]
    [Authorize(Roles = Roles.PlatformAdministrator)]
    public async Task<ActionResult<PlatformSettingsDto>> Update(UpdatePlatformSettingsRequest req)
    {
        if (req.StarterQuotaBytes <= 0 || req.MaxQuotaBytes <= 0)
            return BadRequest("Quota values must be greater than zero.");
        if (req.StarterQuotaBytes > req.MaxQuotaBytes)
            return BadRequest("The starter quota can't exceed the maximum quota.");

        var s = await _settings.GetAsync();
        s.StarterQuotaBytes = req.StarterQuotaBytes;
        s.MaxQuotaBytes = req.MaxQuotaBytes;
        await _db.SaveChangesAsync();
        return new PlatformSettingsDto(s.StarterQuotaBytes, s.MaxQuotaBytes);
    }
}
