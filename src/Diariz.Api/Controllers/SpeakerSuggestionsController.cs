using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Voices Diariz thinks it recognises but is not confident enough to name unasked.
///
/// <para><b>Scoped to your own recordings, and needing no special permission.</b> A suggestion asks "is this
/// speaker in this recording that person?", and the people who can answer were in the meeting. A
/// platform-wide queue would tell whoever held it who appears in every meeting in the instance - the same
/// disclosure <see cref="PlatformPermission.ManagePeople"/> exists to gate on the directory itself.</para>
///
/// <para>Both decisions are recorded. Accepting is how the system learns a voice in a condition it was
/// unsure about; rejecting is the platform's <b>only</b> source of labelled negatives, since every manual
/// link is a positive.</para></summary>
[ApiController]
[Authorize]
[Route("api/speaker-suggestions")]
public class SpeakerSuggestionsController(
    DiarizDbContext db, ISpeakerAssignment assignment, IAudioClipper clipper, IAudioStorage storage)
    : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    [EndpointSummary("List voices waiting to be confirmed")]
    [EndpointDescription(
        "Speakers in **your** recordings that Diariz thinks it recognises, but not confidently enough to " +
        "name on its own. Each carries the person it suspects, how far apart the two voiceprints are, and " +
        "how much that speaker says - enough to judge without opening the recording.\n\n" +
        "Only your own recordings appear, and no permission is needed: the person who can answer whether a " +
        "voice belongs to someone is whoever was in the meeting.")]
    public async Task<ActionResult<IReadOnlyList<SpeakerSuggestionDto>>> Pending()
    {
        var rows = await db.Speakers
            .Where(s => s.SuggestedPersonId != null)
            // Audio still there, deliberately. "Is this speaker that person?" is answerable only by ear, so
            // once the retention sweep has taken the audio the row is not a question - it is a permanent
            // occupant of the queue. The exemption added in 0.257.0 protects audio behind an *enrolled*
            // sample; a pending suggestion is by definition not enrolled, so without this the queue fills
            // with rows nobody can ever clear.
            .Join(db.Recordings.Where(r => r.UserId == UserId && r.AudioDeletedAt == null),
                s => s.RecordingId, r => r.Id,
                (s, r) => new
                {
                    s.Id, s.RecordingId, s.Label, s.SuggestedPersonId, s.SuggestedDistance, s.SuggestedAt,
                    RecordingName = r.Name ?? r.Title,
                })
            .ToListAsync();

        if (rows.Count == 0) return Ok(Array.Empty<SpeakerSuggestionDto>());

        var personIds = rows.Select(r => r.SuggestedPersonId!.Value).Distinct().ToList();
        var names = await db.People
            .Where(p => personIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        // Speech per recording, read once per recording rather than once per row.
        var speech = new Dictionary<Guid, Dictionary<string, long>>();
        foreach (var recordingId in rows.Select(r => r.RecordingId).Distinct())
            speech[recordingId] = await SpeakerSpeech.ForRecordingAsync(db, recordingId);

        return Ok(rows
            .Select(r => new SpeakerSuggestionDto(
                r.Id, r.RecordingId, r.RecordingName, r.Label,
                r.SuggestedPersonId!.Value,
                names.GetValueOrDefault(r.SuggestedPersonId!.Value, ""),
                r.SuggestedDistance ?? 0,
                SpeakerSpeech.MsFor(speech.GetValueOrDefault(r.RecordingId) ?? [], r.Label),
                r.SuggestedAt ?? DateTimeOffset.UtcNow))
            // Closest first: the easiest calls to make, and the ones most likely to be right.
            .OrderBy(r => r.Distance)
            .ToList());
    }

    /// <summary>A speaker the caller may judge, and the transcription that says which audio is theirs.
    ///
    /// <para>Four rules, in one place because two endpoints enforcing them separately is how they come to
    /// disagree: the speaker exists, a suggestion is actually <b>pending</b> on it, the caller owns the
    /// recording, and the audio is still there. The pending check is what bounds these endpoints to the
    /// queue - without it they would read and play any of your own speakers through a route carrying none
    /// of the recording's own checks.</para></summary>
    private async Task<(ActionResult? Failure, Speaker? Speaker, Recording? Recording, Guid TranscriptionId)>
        ResolveAsync(Guid speakerId)
    {
        var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == speakerId);
        if (speaker is null || speaker.SuggestedPersonId is null) return (NotFound(), null, null, default);

        var rec = await db.Recordings.FirstOrDefaultAsync(
            r => r.Id == speaker.RecordingId && r.UserId == UserId && r.AudioDeletedAt == null);
        if (rec is null) return (NotFound(), null, null, default);

        var trId = await db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .OrderByDescending(t => t.Version)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync();
        if (trId is null) return (NotFound(), null, null, default);

        return (null, speaker, rec, trId.Value);
    }

    [HttpGet("{speakerId:guid}/segments")]
    [EndpointSummary("List what a suggested speaker said")]
    [EndpointDescription(
        "The segments this speaker spoke in the recording's current transcription - the evidence behind one " +
        "pending suggestion, so it can be judged without opening the recording.\n\n" +
        "**Only that speaker's segments**, never the recording's transcript: the suggestion names one " +
        "speaker, and everybody else in the meeting stays out of reach. Your own recordings only, and only " +
        "while a suggestion is actually pending.")]
    public async Task<ActionResult<IReadOnlyList<AttributionSegmentDto>>> Segments(Guid speakerId)
    {
        var (failure, speaker, _, trId) = await ResolveAsync(speakerId);
        if (failure is not null) return failure;

        var rows = await db.Segments
            .Where(s => s.TranscriptionId == trId && s.SpeakerLabel == speaker!.Label)
            .OrderBy(s => s.Ordinal)
            .Select(s => new AttributionSegmentDto(s.Id, s.StartMs, s.EndMs, s.Revised ?? s.Original))
            .ToListAsync();

        return Ok(rows);
    }

    [HttpGet("{speakerId:guid}/clip")]
    [EndpointSummary("Play a clip of a suggested speaker")]
    [EndpointDescription(
        "Serves a short WAV clip of one span, so the voice can be judged by ear - the only way the question " +
        "can honestly be answered.\n\n" +
        "The span **must fall inside a segment this speaker spoke** in the current transcription; arbitrary " +
        "offsets are refused with 404. Your own recordings only. Clips are capped at two minutes.")]
    [Produces("audio/wav")]
    public async Task<IActionResult> Clip(Guid speakerId, [FromQuery] long fromMs, [FromQuery] long toMs)
    {
        var (failure, speaker, rec, trId) = await ResolveAsync(speakerId);
        if (failure is not null) return failure;

        // The span must be audio this speaker actually produced. The suggestion unlocks one speaker's voice,
        // not a seek bar over someone's meeting.
        var covered = await db.Segments.AnyAsync(s =>
            s.TranscriptionId == trId
            && s.SpeakerLabel == speaker!.Label
            && s.StartMs <= fromMs && s.EndMs >= toMs);
        if (!covered) return NotFound();

        // Presigned and internal: ffmpeg range-seeks the object store rather than the API pulling a whole
        // recording to cut seconds out of it. The URL never leaves this process.
        var url = await storage.GetPresignedReadUrlAsync(rec!.BlobKey, TimeSpan.FromMinutes(5));
        return File(await clipper.ClipAsync(url, fromMs, toMs), "audio/wav");
    }

    [HttpPost("{speakerId:guid}/accept")]
    [EndpointSummary("Confirm a suggested identity")]
    [EndpointDescription(
        "Names the speaker as the suggested person and adds that speaker's voice to their voiceprint, so the " +
        "same voice in the same conditions is recognised outright next time.\n\n" +
        "The result is a **manual** identification, not an automatic one - re-running identification will " +
        "never take it away. Naming someone who has opted out of voice-printing is allowed; enrolling their " +
        "voice is not, and does not happen.")]
    public Task<IActionResult> Accept(Guid speakerId) => DecideAsync(speakerId, IdentityDecisionKind.Accepted);

    [HttpPost("{speakerId:guid}/reject")]
    [EndpointSummary("Decline a suggested identity")]
    [EndpointDescription(
        "Leaves the speaker anonymous and records that this voice is **not** that person, so the same pair " +
        "is never suggested again.\n\n" +
        "The rejection is kept as evidence: it is the only kind of labelled negative Diariz has, and " +
        "calibrating how confident identification should be depends on it. A later outright match is still " +
        "allowed to apply - that is new evidence, not the same question asked twice.")]
    public Task<IActionResult> Reject(Guid speakerId) => DecideAsync(speakerId, IdentityDecisionKind.Rejected);

    private async Task<IActionResult> DecideAsync(Guid speakerId, IdentityDecisionKind decision)
    {
        var speaker = await db.Speakers.FirstOrDefaultAsync(s => s.Id == speakerId);
        if (speaker is null) return NotFound();

        var owned = await db.Recordings.AnyAsync(r => r.Id == speaker.RecordingId && r.UserId == UserId);
        if (!owned) return NotFound();

        // Nothing pending. Two tabs, or a double click - the caller already has the state they wanted, so
        // answering with an error would be describing the server's surprise rather than a problem.
        if (speaker.SuggestedPersonId is not { } personId) return NoContent();

        var distance = speaker.SuggestedDistance ?? 0;

        db.SpeakerIdentityDecisions.Add(new SpeakerIdentityDecision
        {
            Id = Guid.NewGuid(),
            SpeakerId = speaker.Id,
            PersonId = personId,
            Decision = decision,
            // The distance as it was when the question was asked, never recomputed: the gallery moves, and a
            // later number would describe a different question than the one that was answered.
            Distance = distance,
            DecidedByUserId = UserId,
        });

        if (decision == IdentityDecisionKind.Accepted)
        {
            var person = await db.People.FirstOrDefaultAsync(p => p.Id == personId);
            if (person is null) return NotFound();
            // Through the shared assignment: the opt-out guard, the enrolment and the centroid rebuild are
            // the same rules a manual assignment follows, and must not diverge from them.
            await assignment.AssignAsync(speaker, person);
        }
        else
        {
            speaker.SuggestedPersonId = null;
            speaker.SuggestedDistance = null;
            speaker.SuggestedAt = null;
        }

        await db.SaveChangesAsync();
        return NoContent();
    }
}
