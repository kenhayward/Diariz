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
[Authorize(Policy = "ReadAdminSettings")]
[Route("api/platform/settings")]
public class PlatformSettingsController : ControllerBase
{
    private readonly IPlatformSettingsService _settings;
    private readonly DiarizDbContext _db;
    private readonly IAudioStorage _storage;
    private readonly IJobQueue _queue;
    private readonly ILogger<PlatformSettingsController> _logger;
    private readonly IdentificationRescan _rescan;

    public PlatformSettingsController(
        IPlatformSettingsService settings, DiarizDbContext db, IAudioStorage storage, IJobQueue queue,
        ILogger<PlatformSettingsController> logger, IdentificationRescan rescan)
    {
        _settings = settings;
        _db = db;
        _storage = storage;
        _queue = queue;
        _logger = logger;
        _rescan = rescan;
    }

    [HttpGet]
    public async Task<PlatformSettingsDto> Get()
    {
        var s = await _settings.GetAsync();
        return ToDto(s);
    }

    [HttpPut]
    [Authorize(Policy = "ManagePlatform")]
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
        if (req.LlmTimeoutSeconds < 5)
            return BadRequest("The LLM timeout must be at least 5 seconds.");
        if (req.IdentificationThreshold <= 0 || req.IdentificationThreshold > 2)
            return BadRequest("The identification threshold must be a cosine distance between 0 and 2.");
        // Inverted, nothing would ever be applied automatically - every match would arrive as a question.
        // Equal is allowed and simply means "no confirmation band": accept or ignore, nothing in between.
        if (req.IdentificationConfirmBand < req.IdentificationThreshold)
            return BadRequest("The confirmation band can't be stricter than the acceptance threshold.");
        if (req.IdentificationMargin < 0)
            return BadRequest("The margin can't be negative.");
        if (req.IdentificationMinSpeechMs < 0)
            return BadRequest("The minimum speech duration can't be negative.");

        var s = await _settings.GetAsync();
        s.StarterQuotaBytes = req.StarterQuotaBytes;
        s.MaxQuotaBytes = req.MaxQuotaBytes;
        s.MinutesGenerationMode = req.MinutesGenerationMode;
        s.AutoDeleteAudioEnabled = req.AutoDeleteAudioEnabled;
        s.AudioRetentionDays = req.AudioRetentionDays;
        s.AudioDeletionTimeOfDay = req.AudioDeletionTimeOfDay;
        s.ApiAccessEnabled = req.ApiAccessEnabled;
        s.LlmTimeoutSeconds = req.LlmTimeoutSeconds;
        s.McpAccessEnabled = req.McpAccessEnabled;
        s.WebhooksEnabled = req.WebhooksEnabled;
        s.LlmUsageLoggingEnabled = req.LlmUsageLoggingEnabled;
        s.LlmUsageRetentionDays = Math.Max(0, req.LlmUsageRetentionDays);
        s.LlmStreamUsageEnabled = req.LlmStreamUsageEnabled;
        s.IdentificationThreshold = req.IdentificationThreshold;
        s.IdentificationConfirmBand = req.IdentificationConfirmBand;
        s.IdentificationMargin = req.IdentificationMargin;
        s.IdentificationMinSpeechMs = req.IdentificationMinSpeechMs;
        await _db.SaveChangesAsync();
        return ToDto(s);
    }

    /// <summary>Re-run speaker identification across every speaker that could still be identified.
    ///
    /// <para>Identification otherwise happens once, at transcription time, so enrolling someone today never
    /// revisits yesterday's recordings. This collects the matches that already qualified and were never
    /// applied.</para>
    ///
    /// <para><b>Adds, never revokes.</b> Only anonymous, unlinked speakers are considered, so there is no
    /// existing label for it to take away.</para></summary>
    [HttpPost("rescan-identification")]
    [Authorize(Policy = "ManagePlatform")]
    [EndpointSummary("Re-run speaker identification across the platform")]
    [EndpointDescription(
        "Matches every anonymous speaker against the current voiceprints again, applying confident matches " +
        "and queueing borderline ones for confirmation. Uses the embeddings already stored, so it needs " +
        "neither the audio nor a re-transcription.\n\n" +
        "Pass `dryRun=true` to get the same counts **without writing anything** - what it would apply and " +
        "queue - before committing to it.\n\n" +
        "It never removes a name: speakers that already carry one, automatic or typed, are not considered.")]
    public async Task<RescanRunResult> RescanIdentification(
        [FromQuery] bool dryRun = false, CancellationToken ct = default)
    {
        var report = await _rescan.RunAsync(dryRun, ct);
        if (!dryRun)
            _logger.LogInformation(
                "Identification re-scan: scanned {Scanned}, applied {Applied}, suggested {Suggested}",
                report.Scanned, report.Applied, report.Suggested);
        return new RescanRunResult(report.Scanned, report.Applied, report.Suggested);
    }

    /// <summary>Run the audio-retention deletion pass immediately (manual trigger), using the persisted
    /// retention window - regardless of the auto-delete toggle. Returns how many recordings had audio deleted.</summary>
    [HttpPost("run-audio-retention")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<AudioRetentionRunResult> RunAudioRetentionNow(CancellationToken ct = default)
    {
        var s = await _settings.GetAsync(ct);
        var deleted = await AudioRetentionSweep.RunAsync(
            _db, _storage, DateTimeOffset.UtcNow, s.AudioRetentionDays, _logger, ct);
        return new AudioRetentionRunResult(deleted);
    }

    /// <summary>Backfill tag-cloud tags immediately (manual trigger): enqueue a tag-extraction job for every
    /// recording never tagged (unlike the retention pass, the work runs asynchronously on the tags worker).
    /// Useful when the LLM is configured per-user only, which the startup backfill can't see. Returns how
    /// many jobs were queued, not how many completed.</summary>
    [HttpPost("run-tag-backfill")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<TagBackfillRunResult> RunTagBackfillNow(CancellationToken ct = default)
    {
        var enqueued = await TagBackfill.RunAsync(_db, _queue, _logger, ct);
        return new TagBackfillRunResult(enqueued);
    }

    private static PlatformSettingsDto ToDto(PlatformSettings s) => new(
        s.StarterQuotaBytes, s.MaxQuotaBytes, s.MinutesGenerationMode,
        s.AutoDeleteAudioEnabled, s.AudioRetentionDays, s.AudioDeletionTimeOfDay, s.ApiAccessEnabled,
        s.LlmTimeoutSeconds, s.McpAccessEnabled, s.WebhooksEnabled,
        s.LlmUsageLoggingEnabled, s.LlmUsageRetentionDays, s.LlmStreamUsageEnabled,
        s.IdentificationThreshold, s.IdentificationConfirmBand, s.IdentificationMargin,
        s.IdentificationMinSpeechMs);
}
