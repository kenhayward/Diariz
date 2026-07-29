using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The people directory. Create a person from a recording's speaker, list/rename/merge them,
/// manage the voice samples training their voiceprint, and erase one or all (GDPR) — which also reverts any
/// auto-applied labels.
///
/// <para><b>Platform-wide.</b> One human is one row, which is what makes an erasure request a single delete
/// rather than a hunt through every user's private set. The consequence is that a voiceprint enrolled by one
/// user identifies that person in everyone's recordings, and that the directory lists every external contact
/// the organisation has recorded - so <b>browsing</b> it requires <see cref="PlatformPermission.ManagePeople"/>,
/// while searching by name to label a speaker stays open to everyone.</para></summary>
[ApiController]
[Authorize]
[Route("api/speaker-profiles")]
public class SpeakerProfilesController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly Services.IRoomScope _rooms;
    private readonly IPeopleDirectory _people;
    private readonly IUserPermissions _permissions;

    public SpeakerProfilesController(
        DiarizDbContext db, Services.IRoomScope rooms, IPeopleDirectory people, IUserPermissions permissions)
    {
        _db = db;
        _rooms = rooms;
        _people = people;
        _permissions = permissions;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> CanManagePeopleAsync() => _permissions.HasAsync(UserId, PlatformPermission.ManagePeople);

    /// <summary>Whether the caller may change this person's <b>biometric</b> state - opting them out, or
    /// erasing their voiceprint.
    ///
    /// <para>ManagePeople, <b>or the person is you</b>. The self exception is not an oversight: under GDPR,
    /// withdrawing consent to process your own biometric data is the data subject's right, and routing that
    /// through an administrator would be a weak posture. Keep it as one predicate - both endpoints and the
    /// DTO flag the UI renders from must agree, or they will drift the first time one of them is edited.</para></summary>
    private async Task<bool> CanManageBiometricsAsync(Person person) =>
        person.LinkedUserId == UserId || await CanManagePeopleAsync();

    [HttpGet]
    [EndpointSummary("List your enrolled speakers")]
    [EndpointDescription(
        "Everyone in the people directory, with how many voice samples each voiceprint has learned from. A " +
        "person may have no voiceprint at all - the sample count is then zero, and Diariz simply will not " +
        "recognise them by voice.\n\n" +
        "The directory is **platform-wide**: one person is one record, however many people have recorded " +
        "them. That is what makes an erasure request a single deletion. Because it therefore lists every " +
        "external contact the organisation has recorded, browsing it requires the **Manage people** " +
        "permission. Labelling a speaker does not - anyone can search for a person by name to assign them.")]
    public async Task<ActionResult<IReadOnlyList<SpeakerProfileDto>>> List()
    {
        if (!await CanManagePeopleAsync()) return Forbid();

        return Ok(await _db.People
            .OrderBy(p => p.Name)
            .Select(p => new SpeakerProfileDto(p.Id, p.Name, p.SampleCount))
            .ToListAsync());
    }

    /// <summary>A voiceprint's training contributions (which recording-speakers feed it) and how many
    /// recording-speakers it currently labels.</summary>
    [HttpGet("{id:guid}")]
    [EndpointSummary("Get an enrolled speaker")]
    [EndpointDescription(
        "One person's voiceprint in detail: the **training contributions** feeding it - which recording and " +
        "speaker each sample came from - and how many recording-speakers it currently labels.\n\n" +
        "Use it to audit what a voiceprint has learned from: a contribution from a misattributed speaker is " +
        "why recognition drifts, and can be removed individually. The embedding vector itself is never " +
        "returned.")]
    public async Task<ActionResult<SpeakerProfileDetailDto>> Get(Guid id)
    {
        var profile = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null) return NotFound();

        var identifiedCount = await _db.Speakers.CountAsync(s => s.PersonId == id);

        // Stitch recording display names + speaker labels in memory (provider-agnostic; no FK on RecordingId).
        var raw = await _db.VoiceSamples
            .Where(c => c.PersonId == id)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.RecordingId, c.SpeakerId, c.CreatedAt })
            .ToListAsync();
        var recIds = raw.Select(c => c.RecordingId).ToList();
        var spIds = raw.Select(c => c.SpeakerId).ToList();
        var recMap = (await _db.Recordings.Where(r => recIds.Contains(r.Id))
            .Select(r => new { r.Id, Display = r.Name ?? r.Title }).ToListAsync())
            .ToDictionary(r => r.Id, r => r.Display);
        var spMap = (await _db.Speakers.Where(s => spIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Label }).ToListAsync())
            .ToDictionary(s => s.Id, s => s.Label);

        // Earliest segment start (ms) for each contributed speaker in its recording's current
        // transcription, so the UI can play a sample of that voice. Computed in memory (provider-agnostic).
        var currentTrByRecording = (await _db.Transcriptions
                .Where(t => recIds.Contains(t.RecordingId))
                .Select(t => new { t.Id, t.RecordingId, t.Version }).ToListAsync())
            .GroupBy(t => t.RecordingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Version).First().Id);
        var trIds = currentTrByRecording.Values.ToList();
        var minStart = (await _db.Segments
                .Where(s => trIds.Contains(s.TranscriptionId))
                .Select(s => new { s.TranscriptionId, s.SpeakerLabel, s.StartMs }).ToListAsync())
            .GroupBy(s => (s.TranscriptionId, s.SpeakerLabel))
            .ToDictionary(g => g.Key, g => g.Min(s => s.StartMs));

        long StartFor(Guid recordingId, string label) =>
            currentTrByRecording.TryGetValue(recordingId, out var trId)
            && minStart.TryGetValue((trId, label), out var ms) ? ms : 0;

        var contributions = raw.Select(c =>
        {
            var label = spMap.TryGetValue(c.SpeakerId, out var l) ? l : "";
            return new ProfileContributionDto(
                c.Id, c.RecordingId,
                recMap.TryGetValue(c.RecordingId, out var d) ? d : "(deleted recording)",
                label, StartFor(c.RecordingId, label), c.CreatedAt);
        }).ToList();

        return new SpeakerProfileDetailDto(profile.Id, profile.Name, profile.SampleCount, identifiedCount, contributions);
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Rename an enrolled speaker")]
    [EndpointDescription(
        "Corrects the person's name - for a spelling fix or a change of surname. The voiceprint is unchanged, " +
        "so recognition is unaffected, and every recording labelled with this person picks up the new name.")]
    public async Task<IActionResult> Rename(Guid id, RenameSpeakerProfileRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("A name is required.");

        var profile = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null) return NotFound();

        profile.Name = name;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        // Keep the linked recording-speakers' shown name in sync with the person's new name.
        foreach (var s in await _db.Speakers.Where(s => s.PersonId == id).ToListAsync())
            s.DisplayName = name;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Create a voiceprint from a recording's diarized speaker (its embedding becomes the centroid).</summary>
    [HttpPost]
    [EndpointSummary("Enrol a speaker")]
    [EndpointDescription(
        "Creates a voiceprint from one recording's diarized speaker: that speaker's embedding becomes the " +
        "starting point, and the speaker is named and linked to the new person. From then on the same voice " +
        "is recognised automatically in later recordings.\n\n" +
        "The speaker must already **have an embedding** - one is computed during transcription, so a " +
        "recording made before voiceprints were enabled needs re-transcribing first (400). 404 when the " +
        "recording is not yours or the label does not exist; 400 for an empty name.")]
    public async Task<ActionResult<SpeakerProfileDto>> Create(CreateSpeakerProfileRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("A name is required.");

        // Ownership: the recording (and thus its speaker) must belong to the caller.
        var owned = await _db.Recordings.AnyAsync(r => r.Id == req.RecordingId && r.UserId == UserId);
        if (!owned) return NotFound();

        var speaker = await _db.Speakers
            .FirstOrDefaultAsync(s => s.RecordingId == req.RecordingId && s.Label == req.Label);
        if (speaker is null) return NotFound();
        if (speaker.Embedding is null)
            return BadRequest("This speaker has no voice embedding yet (re-transcribe to compute one).");

        var profile = new Person
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = UserId,
            RoomId = await _rooms.PersonalRoomIdAsync(UserId), // provenance only; nothing filters on it
            Name = name,
            Embedding = speaker.Embedding,
            SampleCount = 1,
        };
        _db.People.Add(profile);
        _db.VoiceSamples.Add(new VoiceSample
        {
            Id = Guid.NewGuid(),
            PersonId = profile.Id,
            SpeakerId = speaker.Id,
            RecordingId = req.RecordingId,
            Embedding = speaker.Embedding,
        });

        // Assign the source speaker to the new profile (manual, not auto).
        speaker.PersonId = profile.Id;
        speaker.DisplayName = name;
        speaker.IdentifiedAuto = false;

        await _db.SaveChangesAsync();
        return new SpeakerProfileDto(profile.Id, profile.Name, profile.SampleCount);
    }

    /// <summary>Remove one voice sample and recompute the centroid from what remains. Removing the last one
    /// simply clears the voiceprint - the person stays, without one.</summary>
    [HttpDelete("{id:guid}/contributions/{contributionId:guid}")]
    [EndpointSummary("Remove a training sample")]
    [EndpointDescription(
        "Drops one sample from a voiceprint and **recomputes it from what remains** - the fix when a " +
        "misattributed speaker has been taught to the wrong person and recognition has started drifting.\n\n" +
        "Removing the **last** sample clears the voiceprint and leaves the person in the directory without " +
        "one, so Diariz stops recognising them by voice. It does not delete them. Recordings already " +
        "labelled keep their names either way.")]
    public async Task<IActionResult> RemoveContribution(Guid id, Guid contributionId)
    {
        var profile = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null) return NotFound();

        var contribution = await _db.VoiceSamples
            .FirstOrDefaultAsync(c => c.Id == contributionId && c.PersonId == id);
        if (contribution is null) return NotFound();

        // Removing the last sample used to be a 400. That guard only existed because Embedding was NOT NULL;
        // now a person can legitimately hold no voiceprint, so this just clears it.
        _db.VoiceSamples.Remove(contribution);
        await _db.SaveChangesAsync();
        await _people.RecomputeVoiceprintAsync(id);
        return NoContent();
    }

    /// <summary>Opt a person in or out of voice-printing. Opting out erases any voiceprint they have.</summary>
    [HttpPut("{id:guid}/voiceprint-opt-out")]
    [EndpointSummary("Opt a person out of voice-printing")]
    [EndpointDescription(
        "Records that this person does not want a voiceprint held for them. Turning it **on erases the one " +
        "they have**, along with every voice sample behind it, and stops them being matched automatically " +
        "from then on.\n\n" +
        "Labels that automatic identification applied revert to the anonymous speaker label. Names typed by " +
        "hand are kept, and stay pointing at the person: those are your statement about who was in the room, " +
        "not something derived from their voice.\n\n" +
        "**Turning it back off does not restore anything** - the biometric is gone, and they would have to " +
        "be enrolled again from a recording.\n\n" +
        "Requires the **Manage people** permission, except on the person linked to your own account: " +
        "withdrawing consent to hold your own biometric data is always yours to do.")]
    public async Task<IActionResult> SetVoiceprintOptOut(Guid id, SetVoiceprintOptOutRequest req)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();

        person.VoiceprintOptOut = req.OptOut;
        person.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        if (req.OptOut) await _people.EraseVoiceprintAsync(id);
        return NoContent();
    }

    /// <summary>Erase a person's voiceprint but keep the person.</summary>
    [HttpDelete("{id:guid}/voiceprint")]
    [EndpointSummary("Erase a person's voiceprint")]
    [EndpointDescription(
        "Destroys the voiceprint and every voice sample behind it, **keeping the person** and their contact " +
        "details. Use it when the biometric should go but the record of who attended should not - the " +
        "narrower half of the GDPR erasure path.\n\n" +
        "Labels that automatic identification applied revert to the anonymous speaker label; names typed by " +
        "hand are kept. Unlike opting out, this does not stop them being enrolled again later.\n\n" +
        "Requires the **Manage people** permission, except on the person linked to your own account.")]
    public async Task<IActionResult> DeleteVoiceprint(Guid id)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();

        await _people.EraseVoiceprintAsync(id);
        return NoContent();
    }

    /// <summary>Merge <c>sourceId</c> into this profile: move its training contributions, reassign its
    /// linked recording-speakers, recompute the centroid, and delete the source.</summary>
    [HttpPost("{id:guid}/merge")]
    [EndpointSummary("Merge two enrolled speakers")]
    [EndpointDescription(
        "Folds `sourceId` into the person in the path when the same human has been enrolled twice - say once " +
        "as \"Sam\" and once as \"Samantha\". The source's training samples move across, every recording " +
        "labelled with it is relabelled, the voiceprint is recomputed from the combined samples, and the " +
        "**source person is deleted**.\n\n" +
        "There is no un-merge, so check the direction: the person in the path survives. The directory is " +
        "shared, so a bad merge affects everyone's recordings, not just yours - hence the **Manage people** " +
        "permission.")]
    public async Task<IActionResult> Merge(Guid id, MergeSpeakerProfilesRequest req)
    {
        if (req.SourceId == id) return BadRequest("Cannot merge a person into itself.");
        if (!await CanManagePeopleAsync()) return Forbid();

        var target = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        var source = await _db.People.FirstOrDefaultAsync(p => p.Id == req.SourceId);
        if (target is null || source is null) return NotFound();

        var targetContribs = await _db.VoiceSamples.Where(c => c.PersonId == target.Id).ToListAsync();
        var sourceContribs = await _db.VoiceSamples.Where(c => c.PersonId == source.Id).ToListAsync();
        foreach (var c in sourceContribs) c.PersonId = target.Id;

        foreach (var s in await _db.Speakers.Where(s => s.PersonId == source.Id).ToListAsync())
        {
            s.PersonId = target.Id;
            s.DisplayName = target.Name;
        }

        RecomputeCentroid(target, targetContribs.Concat(sourceContribs).ToList());
        _db.People.Remove(source);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>GDPR erase: delete the voiceprint + training data, unlink it from recordings, and revert
    /// auto-applied names to the anonymous label (manual names are kept).</summary>
    [HttpDelete("{id:guid}")]
    [EndpointSummary("Erase an enrolled speaker")]
    [EndpointDescription(
        "Deletes the person's voiceprint and all its training data, and unlinks it from every recording - the " +
        "**GDPR erasure** path for biometric data, so nothing recognisable is retained.\n\n" +
        "Labels are handled by origin: names this voiceprint applied **automatically** revert to the " +
        "anonymous speaker label, while names you typed or assigned by hand are kept, since those are your " +
        "words rather than derived from the biometric. Transcripts are otherwise untouched.\n\n" +
        "Requires the **Manage people** permission: the directory is shared, so deleting a person removes " +
        "them from everyone's recordings. To erase only the biometric and keep the person, erase their " +
        "voiceprint instead.")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var profile = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (profile is null) return NotFound();
        if (!await CanManagePeopleAsync()) return Forbid();

        await UnlinkAndRevertAsync([id]);
        _db.People.Remove(profile); // cascades VoiceSamples
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>GDPR erase-all: delete every one of the caller's voiceprints + training data and revert
    /// all auto-applied labels (manual names kept).</summary>
    [HttpDelete]
    [EndpointSummary("Erase all enrolled speakers")]
    [EndpointDescription(
        "Deletes **every** person in the directory, with all their voiceprints and training data, in one call - " +
        "the wholesale GDPR erasure. Automatic speaker identification stops platform-wide until people are " +
        "enrolled again.\n\n" +
        "Same labelling rule as erasing one person: automatically applied names revert to the anonymous " +
        "label, hand-typed names are kept. There is no undo and no confirmation step, so gate it in your UI.\n\n" +
        "Requires **Manage platform**: the directory is shared, so this wipes the voiceprints of everyone on " +
        "the platform, not just the ones you enrolled.")]
    public async Task<IActionResult> DeleteAll()
    {
        if (!await _permissions.HasAsync(UserId, PlatformPermission.ManagePlatform)) return Forbid();

        var profiles = await _db.People.ToListAsync();
        if (profiles.Count == 0) return NoContent();

        await UnlinkAndRevertAsync(profiles.Select(p => p.Id).ToList());
        _db.People.RemoveRange(profiles); // cascades VoiceSamples
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Unlink every speaker pointing at the given profiles and revert auto-applied names to the
    /// anonymous label; hand-typed/assigned names are left intact.</summary>
    private async Task UnlinkAndRevertAsync(IReadOnlyCollection<Guid> profileIds)
    {
        var linked = await _db.Speakers
            .Where(s => s.PersonId != null && profileIds.Contains(s.PersonId.Value)).ToListAsync();
        foreach (var s in linked)
        {
            s.PersonId = null;
            if (s.IdentifiedAuto)
            {
                s.DisplayName = s.Label; // revert the auto label
                s.IdentifiedAuto = false;
            }
        }
    }

    /// <summary>Set the profile's centroid to the L2-normalised mean of the given contributions and
    /// update its sample count. Embeddings are only present under the real provider (vector(192)); when
    /// absent (the in-memory unit provider) the centroid is left unchanged but the count is still updated.</summary>
    private static void RecomputeCentroid(Person profile, IReadOnlyCollection<VoiceSample> contributions)
    {
        var snapshots = contributions.Where(c => c.Embedding is not null).Select(c => c.Embedding.ToArray()).ToList();
        var centroid = Voiceprints.Centroid(snapshots);
        if (centroid is not null) profile.Embedding = centroid;
        profile.SampleCount = contributions.Count;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
