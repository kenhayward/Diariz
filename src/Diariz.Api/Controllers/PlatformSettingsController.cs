using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    private readonly IAudioStorage _storage;
    private readonly ILogger<PlatformSettingsController> _logger;

    public PlatformSettingsController(
        IPlatformSettingsService settings, DiarizDbContext db, IAudioStorage storage,
        ILogger<PlatformSettingsController> logger)
    {
        _settings = settings;
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    [HttpGet]
    public async Task<PlatformSettingsDto> Get()
    {
        var s = await _settings.GetAsync();
        return ToDto(s);
    }

    [HttpPut]
    [Authorize(Roles = Roles.PlatformAdministrator)]
    public async Task<ActionResult<PlatformSettingsDto>> Update(UpdatePlatformSettingsRequest req)
    {
        if (req.StarterQuotaBytes <= 0 || req.MaxQuotaBytes <= 0)
            return BadRequest("Quota values must be greater than zero.");
        if (req.StarterQuotaBytes > req.MaxQuotaBytes)
            return BadRequest("The starter quota can't exceed the maximum quota.");
        if (!Enum.IsDefined(req.MinutesGenerationMode))
            return BadRequest("Unknown minutes generation mode.");
        if (req.AudioRetentionDays < 1)
            return BadRequest("The audio retention window must be at least 1 day.");

        var s = await _settings.GetAsync();
        s.StarterQuotaBytes = req.StarterQuotaBytes;
        s.MaxQuotaBytes = req.MaxQuotaBytes;
        s.MinutesGenerationMode = req.MinutesGenerationMode;
        s.AutoDeleteAudioEnabled = req.AutoDeleteAudioEnabled;
        s.AudioRetentionDays = req.AudioRetentionDays;
        s.AudioDeletionTimeOfDay = req.AudioDeletionTimeOfDay;
        s.ApiAccessEnabled = req.ApiAccessEnabled;
        await _db.SaveChangesAsync();
        return ToDto(s);
    }

    /// <summary>Run the audio-retention deletion pass immediately (manual trigger), using the persisted
    /// retention window - regardless of the auto-delete toggle. Returns how many recordings had audio deleted.</summary>
    [HttpPost("run-audio-retention")]
    [Authorize(Roles = Roles.PlatformAdministrator)]
    public async Task<AudioRetentionRunResult> RunAudioRetentionNow(CancellationToken ct = default)
    {
        var s = await _settings.GetAsync(ct);
        var deleted = await AudioRetentionSweep.RunAsync(
            _db, _storage, DateTimeOffset.UtcNow, s.AudioRetentionDays, _logger, ct);
        return new AudioRetentionRunResult(deleted);
    }

    private static PlatformSettingsDto ToDto(PlatformSettings s) => new(
        s.StarterQuotaBytes, s.MaxQuotaBytes, s.MinutesGenerationMode,
        s.AutoDeleteAudioEnabled, s.AudioRetentionDays, s.AudioDeletionTimeOfDay, s.ApiAccessEnabled);
}
