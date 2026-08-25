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
public class SpeakerSuggestionsController(DiarizDbContext db, ISpeakerAssignment assignment) : ControllerBase
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
            .Join(db.Recordings.Where(r => r.UserId == UserId),
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
