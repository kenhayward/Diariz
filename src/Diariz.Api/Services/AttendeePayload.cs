using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Builds the <c>attendees</c> array carried by every <c>recording.*</c> outbound webhook: who was
/// in the meeting, and enough about them for a workflow to route on it without a second call back.
///
/// <para>Members are written camelCase to match the anonymous objects at the publish sites (the envelope's
/// naming policy would handle it either way; this keeps the shapes readable side by side).</para></summary>
public static class AttendeePayload
{
    /// <summary>One entry per speaker, ordered by diarization label so the array is stable across events for
    /// the same recording.
    ///
    /// <para><paramref name="includeContacts"/> adds <c>title</c>, <c>companyName</c>, <c>email</c> and
    /// <c>phone</c>. When false those keys are <b>absent entirely</b> rather than null, so a subscriber
    /// cannot mistake "not permitted" for "not known".</para>
    ///
    /// <para>An <b>opted-out</b> person still appears, by name. Opting out concerns holding their voiceprint,
    /// not the fact that they attended a meeting - erasing them from the attendee list would misrepresent
    /// what happened.</para></summary>
    public static async Task<IReadOnlyList<object>> ForRecordingAsync(
        DiarizDbContext db, Guid recordingId, bool includeContacts, CancellationToken ct = default)
    {
        var speakers = await db.Speakers
            .Where(s => s.RecordingId == recordingId)
            .OrderBy(s => s.Label)
            .Select(s => new { s.Label, s.DisplayName, s.PersonId, s.IdentifiedAuto, s.IsMultiSpeaker })
            .ToListAsync(ct);
        if (speakers.Count == 0) return [];

        var personIds = speakers.Where(s => s.PersonId is not null).Select(s => s.PersonId!.Value).Distinct().ToList();
        var people = personIds.Count == 0
            ? new Dictionary<Guid, Person>()
            : await db.People.Where(p => personIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        var attendees = new List<object>(speakers.Count);
        foreach (var s in speakers)
        {
            // A "Multiple Speakers" slot is overlapping audio rather than one person, so it never carries
            // person details even if something has been linked to it.
            var person = !s.IsMultiSpeaker && s.PersonId is { } id && people.TryGetValue(id, out var p) ? p : null;

            if (includeContacts && person is not null)
            {
                attendees.Add(new
                {
                    label = s.Label, name = s.DisplayName, personId = s.PersonId,
                    isMultiSpeaker = s.IsMultiSpeaker, identifiedAuto = s.IdentifiedAuto,
                    isInternal = (bool?)person.IsInternal,
                    title = person.Title, companyName = person.CompanyName,
                    email = person.Email, phone = person.Phone,
                });
            }
            else
            {
                attendees.Add(new
                {
                    label = s.Label, name = s.DisplayName, personId = s.PersonId,
                    isMultiSpeaker = s.IsMultiSpeaker, identifiedAuto = s.IdentifiedAuto,
                    // Null, not false: nobody has said whether an unidentified speaker is internal.
                    isInternal = person?.IsInternal,
                });
            }
        }

        return attendees;
    }
}
