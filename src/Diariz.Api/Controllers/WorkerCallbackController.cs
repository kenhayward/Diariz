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
using Pgvector;

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
    private readonly IJobQueue _queue;
    private readonly ISummarizationSettingsResolver _summarization;
    private readonly ISpeakerIdentifier _identifier;
    private readonly WorkerOptions _opts;

    public WorkerCallbackController(
        DiarizDbContext db, IHubContext<TranscriptionHub> hub, IJobQueue queue,
        ISummarizationSettingsResolver summarization, ISpeakerIdentifier identifier, IOptions<WorkerOptions> opts)
    {
        _db = db;
        _hub = hub;
        _queue = queue;
        _summarization = summarization;
        _identifier = identifier;
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
        var speakers = await _db.Speakers
            .Where(sp => sp.RecordingId == transcription.RecordingId)
            .ToListAsync();
        var byLabel = speakers.ToDictionary(sp => sp.Label);
        foreach (var label in body.Segments.Select(s => s.Speaker)
            .Where(l => !string.IsNullOrWhiteSpace(l)).Distinct())
        {
            if (byLabel.ContainsKey(label)) continue;
            var sp = new Speaker { Id = Guid.NewGuid(), RecordingId = transcription.RecordingId, Label = label, DisplayName = label };
            _db.Speakers.Add(sp);
            byLabel[label] = sp;
        }

        // Attach the worker's per-speaker embeddings, then auto-identify against the owner's voiceprints.
        foreach (var se in body.Speakers ?? [])
        {
            if (se.Embedding is not { Length: > 0 } || !byLabel.TryGetValue(se.Speaker, out var sp)) continue;
            sp.Embedding = new Vector(se.Embedding);
        }
        await SpeakerLabeling.ApplyAsync(byLabel.Values, transcription.Recording.UserId, _identifier);

        transcription.Recording.Error = null;  // clear any error from a prior failed attempt

        // Continue the pipeline: when summarisation is configured for the owner, kick it off
        // automatically (which also auto-names the recording when it has no name yet).
        var cfg = await _summarization.ResolveAsync(transcription.Recording.UserId);
        var autoSummarise = cfg.Enabled;
        transcription.Recording.Status = autoSummarise ? RecordingStatus.Summarizing : RecordingStatus.Transcribed;

        await _db.SaveChangesAsync();

        if (autoSummarise)
            await _queue.EnqueueSummarizationAsync(new SummarizationJob(transcription.RecordingId, transcription.Id));

        await _hub.NotifyStatusAsync(transcription.Recording.UserId, transcription.RecordingId,
            transcription.Recording.Status.ToString());
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
