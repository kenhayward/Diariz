using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>Internal callbacks for the audio-merge worker job (see <see cref="RecordingsController.Merge"/>).
/// Authenticated by the shared <c>X-Worker-Secret</c> header, not JWT.</summary>
[ApiController]
[Route("internal/recordings")]
public class WorkerMergeCallbackController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly IAudioStorage _storage;
    private readonly WorkerOptions _opts;

    public WorkerMergeCallbackController(
        DiarizDbContext db, IHubContext<TranscriptionHub> hub, IAudioStorage storage, IOptions<WorkerOptions> opts)
    {
        _db = db;
        _hub = hub;
        _storage = storage;
        _opts = opts.Value;
    }

    private bool SecretOk =>
        Request.Headers.TryGetValue("X-Worker-Secret", out var v) && v == _opts.CallbackSecret;

    /// <summary>The concatenated audio is ready: swap it onto the survivor and remove the merged sources
    /// (rows + blobs) and the survivor's now-superseded original blob.</summary>
    [HttpPost("merge-result")]
    public async Task<IActionResult> Result(AudioMergeResult body)
    {
        if (!SecretOk) return Unauthorized();

        var survivor = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == body.RecordingId);
        if (survivor is null) return NotFound();

        var oldKey = survivor.BlobKey;
        survivor.BlobKey = body.BlobKey;
        survivor.ContentType = body.ContentType;
        survivor.SizeBytes = body.SizeBytes;
        survivor.DurationMs = body.DurationMs;
        survivor.AudioDeletedAt = null;
        survivor.Error = null;
        survivor.Status = RecordingStatus.Transcribed;

        // Remove the now-merged source recordings (cascade clears their transcriptions/segments/speakers).
        var others = await _db.Recordings
            .Where(r => body.DeleteRecordingIds.Contains(r.Id) && r.UserId == survivor.UserId)
            .ToListAsync();
        foreach (var other in others)
        {
            await _storage.DeleteAsync(other.BlobKey);
            // The controller reassigns source attachments to the survivor before enqueuing, so normally
            // there are none here; delete any that slipped in (e.g. attached during the merge window) so
            // their blobs aren't orphaned when the row is removed.
            var attachmentKeys = await _db.Attachments
                .Where(a => a.RecordingId == other.Id && a.BlobKey != null)
                .Select(a => a.BlobKey!)
                .ToListAsync();
            foreach (var key in attachmentKeys)
                await _storage.DeleteAsync(key);
            _db.Recordings.Remove(other);
        }
        // Drop the survivor's original blob — its audio now lives inside the combined file.
        if (!string.IsNullOrEmpty(oldKey) && oldKey != body.BlobKey) await _storage.DeleteAsync(oldKey);

        await _db.SaveChangesAsync();
        await _hub.NotifyStatusAsync(survivor.UserId, survivor.Id, survivor.Status.ToString());
        return Ok();
    }

    /// <summary>The merge failed: flag the survivor and keep the source recordings intact (nothing deleted).</summary>
    [HttpPost("merge-failure")]
    public async Task<IActionResult> Failure(AudioMergeFailure body)
    {
        if (!SecretOk) return Unauthorized();

        var survivor = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == body.RecordingId);
        if (survivor is null) return NotFound();

        survivor.Status = RecordingStatus.Failed;
        survivor.Error = body.Error;
        await _db.SaveChangesAsync();
        await _hub.NotifyStatusAsync(survivor.UserId, survivor.Id, RecordingStatus.Failed.ToString());
        return Ok();
    }
}
