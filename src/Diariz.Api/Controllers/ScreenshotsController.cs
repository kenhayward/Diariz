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
/// directly - an image request cannot carry an Authorization header.
///
/// Read routes (list/content/thumb) are open to anyone who can read the recording - the owner, or a member
/// of a room it is placed in (see <see cref="IRoomScope.CanReadRecordingAsync"/>), so a room co-viewer sees
/// the same captures woven into the transcript as the owner does. Mutating routes (create/delete) stay
/// strictly owner-only.</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}/screenshots")]
public class ScreenshotsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IAudioStorage _storage;
    private readonly IStorageUsage _usage;
    private readonly ScreenshotOptions _options;
    private readonly IRoomScope _rooms;

    public ScreenshotsController(
        DiarizDbContext db, IAudioStorage storage, IStorageUsage usage, IOptions<ScreenshotOptions> options,
        IRoomScope rooms)
    {
        _db = db;
        _storage = storage;
        _usage = usage;
        _options = options.Value;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Owner-only gate for the mutating routes (create/delete).</summary>
    private Task<bool> OwnsAsync(Guid recordingId) =>
        _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);

    /// <summary>Read gate for list/content/thumb: the owner, or a member of a room the recording is placed
    /// in.</summary>
    private Task<bool> CanReadAsync(Guid recordingId) => _rooms.CanReadRecordingAsync(UserId, recordingId);

    private static ScreenshotDto ToDto(MeetingScreenshot s) =>
        new(s.Id, s.CapturedAtMs, s.Width, s.Height, s.SizeBytes, s.Ordinal, s.CreatedAt);

    [HttpGet]
    [EndpointSummary("List a recording's screenshots")]
    [EndpointDescription(
        "The screen captures taken during the meeting, ordered by when they were taken. Metadata only - " +
        "dimensions, byte size, and the moment of capture (milliseconds into the recording), which is what " +
        "places each one in the transcript. Fetch the images themselves from the content or thumb endpoints.\n\n" +
        "Readable by **anyone who can read the recording**, owner or room member.")]
    public async Task<ActionResult<IReadOnlyList<ScreenshotDto>>> List(Guid recordingId)
    {
        if (!await CanReadAsync(recordingId)) return NotFound();
        return await _db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .OrderBy(s => s.CapturedAtMs)
            .Select(s => new ScreenshotDto(s.Id, s.CapturedAtMs, s.Width, s.Height, s.SizeBytes, s.Ordinal, s.CreatedAt))
            .ToListAsync();
    }

    /// <summary>Store one capture. The recorder uploads these after the recording row exists, so a capture
    /// taken mid-meeting arrives here only once its audio has landed.</summary>
    [HttpPost]
    [EndpointSummary("Add a screenshot")]
    [EndpointDescription(
        "Stores one capture as a multipart upload. **Both images are required**: the full-size PNG and a JPEG " +
        "thumbnail, which the client generates - the server does not resize. `capturedAtMs` positions it in " +
        "the transcript, so send the point in the recording rather than a wall-clock time.\n\n" +
        "Both blobs count against your storage quota, and the pair is capped by the platform's screenshot " +
        "limit; either being exceeded returns 413. Captures can only be attached once the recording exists, " +
        "which is why the desktop client holds mid-meeting captures until the audio has uploaded. Owner only.")]
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
    [EndpointSummary("Get a screenshot image")]
    [EndpointDescription(
        "Streams the full-size PNG at its captured resolution - what the viewer shows when you zoom in to read " +
        "a slide. Like the audio endpoints, the bearer may be supplied as an `access_token` query parameter so " +
        "an `<img>` tag can load it directly; **treat such a URL as a credential**. Readable by anyone who can " +
        "read the recording.")]
    public Task<IActionResult> Content(Guid recordingId, Guid screenshotId) =>
        StreamAsync(recordingId, screenshotId, thumbnail: false);

    [HttpGet("{screenshotId:guid}/thumb")]
    [EndpointSummary("Get a screenshot thumbnail")]
    [EndpointDescription(
        "Streams the small JPEG preview - use it for strips and transcript rows, where fetching full-size PNGs " +
        "would be wasteful. Same access rules and the same `access_token` support as the full image.")]
    public Task<IActionResult> Thumb(Guid recordingId, Guid screenshotId) =>
        StreamAsync(recordingId, screenshotId, thumbnail: true);

    private async Task<IActionResult> StreamAsync(Guid recordingId, Guid screenshotId, bool thumbnail)
    {
        if (!await CanReadAsync(recordingId)) return NotFound();
        var shot = await _db.MeetingScreenshots
            .FirstOrDefaultAsync(s => s.Id == screenshotId && s.RecordingId == recordingId);
        if (shot is null) return NotFound();

        var stream = await _storage.OpenReadAsync(thumbnail ? shot.ThumbBlobKey : shot.BlobKey);
        return File(stream, thumbnail ? "image/jpeg" : "image/png");
    }

    /// <summary>Remove a capture. Blobs go first: a dangling row is safer (and retriable) than an orphaned blob.</summary>
    [HttpDelete("{screenshotId:guid}")]
    [EndpointSummary("Delete a screenshot")]
    [EndpointDescription(
        "Removes the capture and both its images permanently, releasing their bytes against your storage " +
        "quota. It also disappears from the transcript. Owner only - a room member who can view the capture " +
        "cannot delete it.")]
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
