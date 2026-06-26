using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/recordings")]
public class RecordingsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IAudioStorage _storage;
    private readonly IJobQueue _queue;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly string _defaultModel;

    public RecordingsController(
        DiarizDbContext db, IAudioStorage storage, IJobQueue queue,
        IHubContext<TranscriptionHub> hub, IConfiguration config)
    {
        _db = db;
        _storage = storage;
        _queue = queue;
        _hub = hub;
        _defaultModel = config["Transcription:DefaultModel"] ?? "whisperx-large-v3";
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IReadOnlyList<RecordingSummaryDto>> List() =>
        await _db.Recordings
            .Where(r => r.UserId == UserId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new RecordingSummaryDto(r.Id, r.Title, r.DurationMs, r.Status, r.CreatedAt))
            .ToListAsync();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RecordingDetailDto>> Get(Guid id)
    {
        var rec = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);

        if (rec is null) return NotFound();

        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        var current = rec.Transcriptions.FirstOrDefault();
        TranscriptionDto? tDto = current is null ? null : new(
            current.Id, current.Model, current.Version, current.Language, current.CreatedAt,
            current.Segments.Select(s => new SegmentDto(
                s.SpeakerLabel,
                names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.Text)).ToList());

        return new RecordingDetailDto(rec.Id, rec.Title, rec.DurationMs, rec.Status, rec.Error,
            rec.CreatedAt, names, tDto);
    }

    /// <summary>Upload an audio file and kick off transcription.</summary>
    [HttpPost]
    [RequestSizeLimit(1024L * 1024 * 1024)] // 1 GiB
    public async Task<ActionResult<RecordingSummaryDto>> Upload(
        [FromForm] IFormFile audio, [FromForm] string? title, [FromForm] long durationMs)
    {
        if (audio is null || audio.Length == 0) return BadRequest("Empty audio.");

        var rec = new Recording
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Title = string.IsNullOrWhiteSpace(title) ? $"Recording {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}" : title,
            ContentType = audio.ContentType,
            DurationMs = durationMs,
            Status = RecordingStatus.Uploaded
        };
        rec.BlobKey = $"{UserId}/{rec.Id}{Path.GetExtension(audio.FileName)}";

        await using (var stream = audio.OpenReadStream())
            await _storage.UploadAsync(rec.BlobKey, stream, rec.ContentType);

        _db.Recordings.Add(rec);
        await EnqueueTranscriptionAsync(rec);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = rec.Id },
            new RecordingSummaryDto(rec.Id, rec.Title, rec.DurationMs, rec.Status, rec.CreatedAt));
    }

    [HttpPost("{id:guid}/retranscribe")]
    public async Task<IActionResult> Retranscribe(Guid id, RetranscribeRequest req)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        await EnqueueTranscriptionAsync(rec, req.Model);
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpPut("{id:guid}/speakers")]
    public async Task<IActionResult> RenameSpeaker(Guid id, RenameSpeakerRequest req)
    {
        var rec = await _db.Recordings.Include(r => r.Speakers)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var speaker = rec.Speakers.FirstOrDefault(s => s.Label == req.Label);
        if (speaker is null)
        {
            // Add to the DbSet (not the loaded nav collection) so EF tracks it as Added/INSERT.
            // Adding via rec.Speakers leaves the new row in the wrong state and SaveChanges
            // emits an UPDATE that affects 0 rows -> DbUpdateConcurrencyException.
            speaker = new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = req.Label };
            _db.Speakers.Add(speaker);
        }
        speaker.DisplayName = req.DisplayName;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{id:guid}/audio-url")]
    public async Task<ActionResult<object>> AudioUrl(Guid id)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();
        var url = _storage.GetPresignedDownloadUrl(rec.BlobKey, TimeSpan.FromHours(1));
        return new { url };
    }

    private async Task EnqueueTranscriptionAsync(Recording rec, string? model = null)
    {
        var nextVersion = await _db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .Select(t => (int?)t.Version).MaxAsync() ?? 0;

        var transcription = new Transcription
        {
            Id = Guid.NewGuid(),
            RecordingId = rec.Id,
            Model = model ?? _defaultModel,
            Version = nextVersion + 1
        };
        _db.Transcriptions.Add(transcription);

        rec.Status = RecordingStatus.Queued;
        await _queue.EnqueueAsync(new TranscriptionJob(rec.Id, transcription.Id, rec.BlobKey, transcription.Model));
        await _hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());
    }
}
