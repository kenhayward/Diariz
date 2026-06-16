using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>
/// Internal endpoint the Python worker calls back to with results. Not user-facing;
/// authenticated by a shared secret header (X-Worker-Secret) rather than JWT.
/// </summary>
[ApiController]
[Route("internal/transcriptions")]
public class WorkerCallbackController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly WorkerOptions _opts;

    public WorkerCallbackController(DiarizDbContext db, IHubContext<TranscriptionHub> hub, IOptions<WorkerOptions> opts)
    {
        _db = db;
        _hub = hub;
        _opts = opts.Value;
    }

    private bool SecretOk =>
        Request.Headers.TryGetValue("X-Worker-Secret", out var v) && v == _opts.CallbackSecret;

    [HttpPost("result")]
    public async Task<IActionResult> Result(TranscriptionResult body)
    {
        if (!SecretOk) return Unauthorized();

        var transcription = await _db.Transcriptions
            .Include(t => t.Recording)
            .FirstOrDefaultAsync(t => t.Id == body.TranscriptionId);
        if (transcription?.Recording is null) return NotFound();

        transcription.Language = body.Language;

        var ordinal = 0;
        foreach (var s in body.Segments)
        {
            _db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcription.Id,
                SpeakerLabel = string.IsNullOrWhiteSpace(s.Speaker) ? "UNKNOWN" : s.Speaker,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Text = s.Text,
                Ordinal = ordinal++
            });
        }

        // Seed Speaker rows for any new labels (default display = label), preserving renames.
        var existing = await _db.Speakers
            .Where(sp => sp.RecordingId == transcription.RecordingId)
            .Select(sp => sp.Label).ToListAsync();
        foreach (var label in body.Segments.Select(s => s.Speaker).Distinct())
        {
            if (string.IsNullOrWhiteSpace(label) || existing.Contains(label)) continue;
            _db.Speakers.Add(new Speaker
            {
                Id = Guid.NewGuid(),
                RecordingId = transcription.RecordingId,
                Label = label,
                DisplayName = label
            });
        }

        transcription.Recording.Status = RecordingStatus.Transcribed;
        await _db.SaveChangesAsync();
        await _hub.NotifyStatusAsync(transcription.Recording.UserId, transcription.RecordingId,
            RecordingStatus.Transcribed.ToString());
        return Ok();
    }

    [HttpPost("failure")]
    public async Task<IActionResult> Failure(TranscriptionFailure body)
    {
        if (!SecretOk) return Unauthorized();

        var transcription = await _db.Transcriptions
            .Include(t => t.Recording)
            .FirstOrDefaultAsync(t => t.Id == body.TranscriptionId);
        if (transcription?.Recording is null) return NotFound();

        transcription.Recording.Status = RecordingStatus.Failed;
        transcription.Recording.Error = body.Error;
        await _db.SaveChangesAsync();
        await _hub.NotifyStatusAsync(transcription.Recording.UserId, transcription.RecordingId,
            RecordingStatus.Failed.ToString());
        return Ok();
    }
}
