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

        // Pending is derived from UsedMs being null. Zero is the honest value here: nothing was embedded
        // this time round, and the row stops spinning.
        sample.UsedMs = 0;
        await _db.SaveChangesAsync(CancellationToken.None);

        _log.LogWarning("Voiceprint re-embed failed for sample {SampleId}: {Error}", sample.Id, body.Error);
        return NoContent();
    }
}
