using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>The one place a diarized speaker becomes a named person.
///
/// <para>Extracted because two callers now do it - a manual assignment on a transcript, and accepting a
/// suggestion - and the rules underneath are ones that must not drift apart: whether the person opted out of
/// voice-printing, whether the speaker has an embedding to enrol at all, and how the resulting centroid is
/// rebuilt.</para></summary>
public interface ISpeakerAssignment
{
    /// <summary>Names <paramref name="speaker"/> as <paramref name="person"/>, enrolling that speaker's
    /// voice as a training sample where it is permitted to.
    ///
    /// <para><b>Does not save.</b> The caller owns the transaction, and the centroid recompute that follows
    /// reads what was saved.</para></summary>
    Task AssignAsync(Speaker speaker, Person person, CancellationToken ct = default);

    /// <summary>Reverts a speaker to its anonymous diarization label, and out of "Multiple Speakers".</summary>
    void Unassign(Speaker speaker);
}

public class SpeakerAssignment(DiarizDbContext db, IPeopleDirectory people) : ISpeakerAssignment
{
    public async Task AssignAsync(Speaker speaker, Person person, CancellationToken ct = default)
    {
        speaker.PersonId = person.Id;
        speaker.DisplayName = person.Name;
        speaker.IdentifiedAuto = false; // an explicit assignment, not a guess
        speaker.IsMultiSpeaker = false; // naming a single person exits "Multiple Speakers" mode

        // A pending suggestion has been answered by the act of assigning.
        speaker.SuggestedPersonId = null;
        speaker.SuggestedDistance = null;
        speaker.SuggestedAt = null;

        // Train "by whole speakers": record this speaker as a voice sample, once. Needs the speaker
        // embedding, which only exists once the worker has run - skip gracefully when it has not.
        //
        // Naming an opted-out person is fine and still happens above; what must not happen is building a
        // voiceprint for them. Saying "that was Alice" is your assertion about the meeting; holding Alice's
        // biometric after she asked you not to is the thing she opted out of.
        if (speaker.Embedding is null || person.VoiceprintOptOut) return;

        var already = await db.VoiceSamples
            .AnyAsync(v => v.PersonId == person.Id && v.SpeakerId == speaker.Id, ct);
        if (already) return;

        db.VoiceSamples.Add(new VoiceSample
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            SpeakerId = speaker.Id,
            RecordingId = speaker.RecordingId,
            Embedding = speaker.Embedding,
        });

        // Saved before recomputing, then rebuilt through the shared recompute rather than with arithmetic of
        // its own. The inline version this replaces never learned about excluded samples, so assigning any
        // speaker quietly pulled every previously-dropped sample back into the centroid.
        await db.SaveChangesAsync(ct);
        await people.RecomputeVoiceprintAsync(person.Id, ct);
    }

    public void Unassign(Speaker speaker)
    {
        speaker.PersonId = null;
        speaker.DisplayName = speaker.Label;
        speaker.IdentifiedAuto = false;
        speaker.IsMultiSpeaker = false;
        speaker.SuggestedPersonId = null;
        speaker.SuggestedDistance = null;
        speaker.SuggestedAt = null;
    }
}
