using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>Screen captures taken during a recording from the desktop client. Each capture stores two
/// blobs (full PNG plus JPEG thumbnail) and counts toward the owner's storage quota. The content and thumb
/// endpoints accept the bearer via <c>access_token</c> (see Program.cs) so an &lt;img&gt; tag can load them
/// directly - an image request cannot carry an Authorization header.</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}/screenshots")]
public class ScreenshotsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IAudioStorage _storage;
    private readonly IStorageUsage _usage;
    private readonly ScreenshotOptions _options;

    public ScreenshotsController(
        DiarizDbContext db, IAudioStorage storage, IStorageUsage usage, IOptions<ScreenshotOptions> options)
    {
        _db = db;
        _storage = storage;
        _usage = usage;
        _options = options.Value;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> OwnsAsync(Guid recordingId) =>
        _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);

    private static ScreenshotDto ToDto(MeetingScreenshot s) =>
        new(s.Id, s.CapturedAtMs, s.Width, s.Height, s.SizeBytes, s.Ordinal, s.CreatedAt);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ScreenshotDto>>> List(Guid recordingId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        return await _db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .OrderBy(s => s.CapturedAtMs)
            .Select(s => new ScreenshotDto(s.Id, s.CapturedAtMs, s.Width, s.Height, s.SizeBytes, s.Ordinal, s.CreatedAt))
            .ToListAsync();
    }

    /// <summary>Store one capture. The recorder uploads these after the recording row exists, so a capture
    /// taken mid-meeting arrives here only once its audio has landed.</summary>
    [HttpPost]
    [RequestSizeLimit(50L * 1024 * 1024)]
    public async Task<ActionResult<ScreenshotDto>> Create(
        Guid recordingId,
        [FromForm] IFormFile? full,
        [FromForm] IFormFile? thumb,
        [FromForm] long capturedAtMs,
        [FromForm] int width,
        [FromForm] int height)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        if (full is null || full.Length == 0 || thumb is null || thumb.Length == 0)
            return BadRequest("Both the full image and its thumbnail are required.");
        if (capturedAtMs < 0) return BadRequest("capturedAtMs must not be negative.");

        var total = full.Length + thumb.Length;
        if (total > _options.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Screenshot too large. The maximum is {_options.MaxBytes / (1024 * 1024)} MB.");

        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        if (used + total > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                "Storage quota exceeded. Delete some recordings, attachments or screenshots, or ask an administrator to raise your quota.");

        var id = Guid.NewGuid();
        var blobKey = $"{UserId}/screenshots/{id}.png";
        var thumbKey = $"{UserId}/screenshots/{id}.thumb.jpg";

        await using (var stream = full.OpenReadStream())
            await _storage.UploadAsync(blobKey, stream, "image/png");
        await using (var stream = thumb.OpenReadStream())
            await _storage.UploadAsync(thumbKey, stream, "image/jpeg");

        var next = (await _db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .Select(s => (int?)s.Ordinal)
            .MaxAsync() ?? -1) + 1;

        var shot = new MeetingScreenshot
        {
            Id = id,
            UserId = UserId,
            RecordingId = recordingId,
            CapturedAtMs = capturedAtMs,
            BlobKey = blobKey,
            ThumbBlobKey = thumbKey,
            Width = width,
            Height = height,
            SizeBytes = total,
            Ordinal = next,
        };
        _db.MeetingScreenshots.Add(shot);
        await _db.SaveChangesAsync();
        return ToDto(shot);
    }

    [HttpGet("{screenshotId:guid}/content")]
    public Task<IActionResult> Content(Guid recordingId, Guid screenshotId) =>
        StreamAsync(recordingId, screenshotId, thumbnail: false);

    [HttpGet("{screenshotId:guid}/thumb")]
    public Task<IActionResult> Thumb(Guid recordingId, Guid screenshotId) =>
        StreamAsync(recordingId, screenshotId, thumbnail: true);

    private async Task<IActionResult> StreamAsync(Guid recordingId, Guid screenshotId, bool thumbnail)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var shot = await _db.MeetingScreenshots
            .FirstOrDefaultAsync(s => s.Id == screenshotId && s.RecordingId == recordingId);
        if (shot is null) return NotFound();

        var stream = await _storage.OpenReadAsync(thumbnail ? shot.ThumbBlobKey : shot.BlobKey);
        return File(stream, thumbnail ? "image/jpeg" : "image/png");
    }

    /// <summary>Remove a capture. Blobs go first: a dangling row is safer (and retriable) than an orphaned blob.</summary>
    [HttpDelete("{screenshotId:guid}")]
    public async Task<IActionResult> Delete(Guid recordingId, Guid screenshotId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var shot = await _db.MeetingScreenshots
            .FirstOrDefaultAsync(s => s.Id == screenshotId && s.RecordingId == recordingId);
        if (shot is null) return NotFound();

        await _storage.DeleteAsync(shot.BlobKey);
        await _storage.DeleteAsync(shot.ThumbBlobKey);
        _db.MeetingScreenshots.Remove(shot);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
