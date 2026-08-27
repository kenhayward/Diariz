using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Diariz.Api.Controllers;

/// <summary>Internal callbacks for the on-demand voiceprint re-embed job (see
/// <see cref="PeopleController.SetVoiceSampleSpans"/>). Authenticated by the shared
/// <c>X-Worker-Secret</c> header, not JWT.</summary>
[ApiController]
[Route("internal/people")]
public class WorkerVoiceprintCallbackController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IPeopleDirectory _people;
    private readonly WorkerOptions _opts;
    private readonly ILogger<WorkerVoiceprintCallbackController> _log;

    public WorkerVoiceprintCallbackController(
        DiarizDbContext db, IPeopleDirectory people, IOptions<WorkerOptions> opts,
        ILogger<WorkerVoiceprintCallbackController> log)
    {
        _db = db;
        _people = people;
        _opts = opts.Value;
        _log = log;
    }

    private bool SecretOk =>
        Request.Headers.TryGetValue("X-Worker-Secret", out var v) && v == _opts.CallbackSecret;

    /// <summary>The re-embed is done: store the vector, stop the sample reading as pending, clear the
    /// contributing speaker's stale flag, and rebuild the person's centroid - the average of the samples
    /// changed the moment one of them did.</summary>
    [HttpPost("voiceprint-result")]
    public async Task<IActionResult> Result(VoiceprintResult body)
    {
        if (!SecretOk) return Unauthorized();

        var sample = await _db.VoiceSamples.FirstOrDefaultAsync(v => v.Id == body.VoiceSampleId);
        if (sample is null) return NotFound();

        if (body.Embedding is { Length: > 0 }) sample.Embedding = new Vector(body.Embedding);
        sample.UsedMs = body.UsedMs;
        sample.RecomputeQueuedAt = null;
        // A retry that worked clears the warning, or a row that failed once carries it for ever.
        sample.RecomputeFailedAt = null;

        var speaker = await _db.Speakers.FirstOrDefaultAsync(s => s.Id == sample.SpeakerId);
        if (speaker is not null) speaker.EmbeddingStale = false;

        // No CancellationToken from the request on purpose: saving with a cancelled token in a callback
        // path is how a row gets stranded in its in-flight status forever.
        await _db.SaveChangesAsync(CancellationToken.None);
        await _people.RecomputeVoiceprintAsync(sample.PersonId, CancellationToken.None);

        _log.LogInformation(
            "Voiceprint sample {SampleId} re-embedded ({UsedMs}ms of {SelectedMs}ms selected)",
            sample.Id, body.UsedMs, body.SelectedMs);
        return NoContent();
    }

    /// <summary>The re-embed failed. The sample keeps whatever vector it had - a failed recompute must not
    /// destroy a working voiceprint - but it must stop reading as pending, or a dead job is
    /// indistinguishable from a slow one.</summary>
    [HttpPost("voiceprint-failure")]
    public async Task<IActionResult> Failure(VoiceprintFailure body)
    {
        if (!SecretOk) return Unauthorized();

        var sample = await _db.VoiceSamples.FirstOrDefaultAsync(v => v.Id == body.VoiceSampleId);
        if (sample is null) return NotFound();

        // UsedMs is left alone. Writing zero here is what the row used to do to stop itself spinning, and
        // it rendered as "trains on 0:00" - a confident figure for audio that was never measured. The
        // vector is untouched on failure, so the duration describing it must be too.
        sample.RecomputeQueuedAt = null;
        sample.RecomputeFailedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);

        // The worker's text is an arbitrary exception message. Through LogSanitizer like every other
        // externally-influenced value we log, so a newline in it cannot write a second line that
        // reads like one of ours.
        _log.LogWarning(
            "Voiceprint re-embed failed for sample {SampleId}: {Error}",
            sample.Id, LogSanitizer.Clean(body.Error));
        return NoContent();
    }
}
