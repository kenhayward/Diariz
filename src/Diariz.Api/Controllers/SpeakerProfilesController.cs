using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Enrolled voiceprints (per-user). Create a profile from a recording's speaker, list/rename/merge
/// them, manage their training contributions, and erase one or all (GDPR) — which also reverts any
/// auto-applied labels.</summary>
[ApiController]
[Authorize]
[Route("api/speaker-profiles")]
public class SpeakerProfilesController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly Services.IRoomScope _rooms;

    public SpeakerProfilesController(DiarizDbContext db, Services.IRoomScope rooms)
    {
        _db = db;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    [EndpointSummary("List your enrolled speakers")]
    [EndpointDescription(
        "The people you have enrolled a voiceprint for, with how many samples each has learned from. These " +
        "are what let Diariz recognise the same person across later recordings automatically.\n\n" +
        "Voiceprints are **biometric data** and strictly per-user: yours are never visible to anyone else, " +
        "even in a shared room.")]
    public async Task<IReadOnlyList<SpeakerProfileDto>> List()
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        return await _db.SpeakerProfiles
            .Where(p => p.RoomId == roomId)
            .OrderBy(p => p.Name)
            .Select(p => new SpeakerProfileDto(p.Id, p.Name, p.SampleCount))
            .ToListAsync();
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
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == id && p.RoomId == roomId);
        if (profile is null) return NotFound();

        var identifiedCount = await _db.Speakers.CountAsync(s => s.ProfileId == id);

        // Stitch recording display names + speaker labels in memory (provider-agnostic; no FK on RecordingId).
        var raw = await _db.ProfileContributions
            .Where(c => c.ProfileId == id)
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

        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == id && p.RoomId == roomId);
        if (profile is null) return NotFound();

        profile.Name = name;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        // Keep the linked recording-speakers' shown name in sync with the person's new name.
        foreach (var s in await _db.Speakers.Where(s => s.ProfileId == id).ToListAsync())
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

        var profile = new SpeakerProfile
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            RoomId = await _rooms.PersonalRoomIdAsync(UserId), // populated now; queries flip to it in Phase 4
            Name = name,
            Embedding = speaker.Embedding,
            SampleCount = 1,
        };
        _db.SpeakerProfiles.Add(profile);
        _db.ProfileContributions.Add(new ProfileContribution
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            SpeakerId = speaker.Id,
            RecordingId = req.RecordingId,
            Embedding = speaker.Embedding,
        });

        // Assign the source speaker to the new profile (manual, not auto).
        speaker.ProfileId = profile.Id;
        speaker.DisplayName = name;
        speaker.IdentifiedAuto = false;

        await _db.SaveChangesAsync();
        return new SpeakerProfileDto(profile.Id, profile.Name, profile.SampleCount);
    }

    /// <summary>Remove one training contribution and recompute the centroid from the remaining snapshots.
    /// The last contribution can't be removed — delete the person instead (a voiceprint needs a sample).</summary>
    [HttpDelete("{id:guid}/contributions/{contributionId:guid}")]
    [EndpointSummary("Remove a training sample")]
    [EndpointDescription(
        "Drops one sample from a voiceprint and **recomputes it from what remains** - the fix when a " +
        "misattributed speaker has been taught to the wrong person and recognition has started drifting.\n\n" +
        "The **last remaining sample cannot be removed** (400): a voiceprint with nothing to match against " +
        "is meaningless, so delete the person instead. Recordings already labelled keep their names.")]
    public async Task<IActionResult> RemoveContribution(Guid id, Guid contributionId)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == id && p.RoomId == roomId);
        if (profile is null) return NotFound();

        var contribution = await _db.ProfileContributions
            .FirstOrDefaultAsync(c => c.Id == contributionId && c.ProfileId == id);
        if (contribution is null) return NotFound();

        var remaining = await _db.ProfileContributions
            .Where(c => c.ProfileId == id && c.Id != contributionId).ToListAsync();
        if (remaining.Count == 0)
            return BadRequest("A voiceprint needs at least one sample. Delete the person instead.");

        _db.ProfileContributions.Remove(contribution);
        RecomputeCentroid(profile, remaining);
        await _db.SaveChangesAsync();
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
        "There is no un-merge, so check the direction: the profile in the path survives.")]
    public async Task<IActionResult> Merge(Guid id, MergeSpeakerProfilesRequest req)
    {
        if (req.SourceId == id) return BadRequest("Cannot merge a person into itself.");

        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var target = await _db.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == id && p.RoomId == roomId);
        var source = await _db.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == req.SourceId && p.RoomId == roomId);
        if (target is null || source is null) return NotFound();

        var targetContribs = await _db.ProfileContributions.Where(c => c.ProfileId == target.Id).ToListAsync();
        var sourceContribs = await _db.ProfileContributions.Where(c => c.ProfileId == source.Id).ToListAsync();
        foreach (var c in sourceContribs) c.ProfileId = target.Id;

        foreach (var s in await _db.Speakers.Where(s => s.ProfileId == source.Id).ToListAsync())
        {
            s.ProfileId = target.Id;
            s.DisplayName = target.Name;
        }

        RecomputeCentroid(target, targetContribs.Concat(sourceContribs).ToList());
        _db.SpeakerProfiles.Remove(source);
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
        "words rather than derived from the biometric. Transcripts are otherwise untouched.")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var profile = await _db.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == id && p.RoomId == roomId);
        if (profile is null) return NotFound();

        await UnlinkAndRevertAsync([id]);
        _db.SpeakerProfiles.Remove(profile); // cascades ProfileContributions
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>GDPR erase-all: delete every one of the caller's voiceprints + training data and revert
    /// all auto-applied labels (manual names kept).</summary>
    [HttpDelete]
    [EndpointSummary("Erase all enrolled speakers")]
    [EndpointDescription(
        "Deletes **every** voiceprint you have enrolled, with all their training data, in one call - the " +
        "wholesale GDPR erasure. Automatic speaker identification stops until you enrol again.\n\n" +
        "Same labelling rule as erasing one person: automatically applied names revert to the anonymous " +
        "label, hand-typed names are kept. There is no undo and no confirmation step, so gate it in your UI.")]
    public async Task<IActionResult> DeleteAll()
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var profiles = await _db.SpeakerProfiles.Where(p => p.RoomId == roomId).ToListAsync();
        if (profiles.Count == 0) return NoContent();

        await UnlinkAndRevertAsync(profiles.Select(p => p.Id).ToList());
        _db.SpeakerProfiles.RemoveRange(profiles); // cascades ProfileContributions
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Unlink every speaker pointing at the given profiles and revert auto-applied names to the
    /// anonymous label; hand-typed/assigned names are left intact.</summary>
    private async Task UnlinkAndRevertAsync(IReadOnlyCollection<Guid> profileIds)
    {
        var linked = await _db.Speakers
            .Where(s => s.ProfileId != null && profileIds.Contains(s.ProfileId.Value)).ToListAsync();
        foreach (var s in linked)
        {
            s.ProfileId = null;
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
    private static void RecomputeCentroid(SpeakerProfile profile, IReadOnlyCollection<ProfileContribution> contributions)
    {
        var snapshots = contributions.Where(c => c.Embedding is not null).Select(c => c.Embedding.ToArray()).ToList();
        var centroid = Voiceprints.Centroid(snapshots);
        if (centroid is not null) profile.Embedding = centroid;
        profile.SampleCount = contributions.Count;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
