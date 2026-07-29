using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The people directory: the humans who appear in meetings, with their contact details and an
/// <em>optional</em> voiceprint.
///
/// <para><b>Platform-wide.</b> One person is one row, however many people have recorded them, which is what
/// makes an erasure request a single delete. The consequences are deliberate: a voiceprint enrolled by one
/// user identifies that person in everyone's recordings, and the directory lists every external contact the
/// organisation has recorded - so <b>browsing</b> requires <see cref="PlatformPermission.ManagePeople"/>,
/// while <see cref="Search"/> stays open so anyone can label a speaker in their own recording.</para>
///
/// <para>Backed by the <c>SpeakerProfiles</c> table; see the naming note on <see cref="Person"/>.</para></summary>
[ApiController]
[Authorize]
[Route("api/people")]
public class PeopleController : ControllerBase
{
    /// <summary>Below this, a search returns nothing rather than most of the directory.</summary>
    private const int MinSearchLength = 2;
    private const int DefaultTake = 50;
    private const int MaxTake = 200;

    private readonly DiarizDbContext _db;
    private readonly IRoomScope _rooms;
    private readonly IPeopleDirectory _people;
    private readonly IUserPermissions _permissions;

    public PeopleController(
        DiarizDbContext db, IRoomScope rooms, IPeopleDirectory people, IUserPermissions permissions)
    {
        _db = db;
        _rooms = rooms;
        _people = people;
        _permissions = permissions;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> CanManagePeopleAsync() => _permissions.HasAsync(UserId, PlatformPermission.ManagePeople);

    /// <summary>Whether the caller may change this person's <b>biometric</b> state - opting them out, or
    /// erasing their voiceprint - or edit their details.
    ///
    /// <para>ManagePeople, <b>or the person is you</b>. The self exception is not an oversight: under GDPR,
    /// withdrawing consent to process your own biometric data is the data subject's right, and routing that
    /// through an administrator would be a weak posture. It is one predicate on purpose - both the endpoints
    /// and <see cref="PersonDto.CanManageBiometrics"/> (which clients render their controls from) resolve
    /// through here, so the server and the UI cannot disagree.</para></summary>
    private async Task<bool> CanManageBiometricsAsync(Person person) =>
        person.LinkedUserId == UserId || await CanManagePeopleAsync();

    private PersonDto ToDto(Person p, bool canManagePeople) => new(
        p.Id, p.Name, p.Title, p.CompanyName, p.Email, p.Phone,
        p.IsInternal, p.VoiceprintOptOut, p.Embedding is not null || p.SampleCount > 0, p.SampleCount,
        p.LinkedUserId, p.LinkedUserId == UserId, canManagePeople || p.LinkedUserId == UserId,
        p.CreatedAt, p.UpdatedAt);

    // ---- Reading ----

    [HttpGet]
    [EndpointSummary("List the people directory")]
    [EndpointDescription(
        "Everyone in the directory, newest names first by name order, with optional filters. A person may " +
        "have no voiceprint at all - `hasVoiceprint` is then false and Diariz will not recognise them by " +
        "voice.\n\n" +
        "Requires the **Manage people** permission, because the directory is platform-wide and therefore " +
        "lists every external contact the organisation has recorded. To find someone in order to label a " +
        "speaker, use the search endpoint instead - that one is open to everyone.\n\n" +
        "`q` matches the name, the email address **and** the company, case-insensitively - so you can find " +
        "everyone from one client without knowing their names.")]
    public async Task<ActionResult<IReadOnlyList<PersonDto>>> List(
        string? q = null, bool? isInternal = null, bool? hasVoiceprint = null, int? take = null, int? skip = null)
    {
        if (!await CanManagePeopleAsync()) return Forbid();

        // Self-heal: a user created by a path that forgot to provision still appears in their own directory.
        await _people.EnsureForUserAsync(UserId);

        var query = _db.People.AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(p => p.Name.ToLower().Contains(needle)
                                     || (p.Email != null && p.Email.ToLower().Contains(needle))
                                     || (p.CompanyName != null && p.CompanyName.ToLower().Contains(needle)));
        }

        if (isInternal is { } internalOnly) query = query.Where(p => p.IsInternal == internalOnly);
        if (hasVoiceprint is { } wantsPrint)
            query = wantsPrint ? query.Where(p => p.SampleCount > 0) : query.Where(p => p.SampleCount == 0);

        var page = await query
            .OrderBy(p => p.Name)
            .Skip(skip is > 0 ? skip.Value : 0)
            .Take(Math.Clamp(take ?? DefaultTake, 1, MaxTake))
            .ToListAsync();

        return Ok(page.Select(p => ToDto(p, canManagePeople: true)).ToList());
    }

    [HttpGet("search")]
    [EndpointSummary("Search people by name")]
    [EndpointDescription(
        "Finds people by name or email for a picker - what the speaker-assignment control on a transcript " +
        "uses. **Open to every signed-in user**, because naming a speaker in your own recording is not a " +
        "privileged act; listing the whole directory is, and has its own endpoint.\n\n" +
        "Returns nothing for a query shorter than two characters, so a picker does not pull most of the " +
        "directory on the first keystroke. Capped at 20 results.")]
    public async Task<ActionResult<IReadOnlyList<PersonDto>>> Search(string q)
    {
        var needle = (q ?? "").Trim().ToLower();
        if (needle.Length < MinSearchLength) return Ok(Array.Empty<PersonDto>());

        var canManage = await CanManagePeopleAsync();
        var hits = await _db.People
            .Where(p => p.Name.ToLower().Contains(needle) || (p.Email != null && p.Email.ToLower().Contains(needle)))
            .OrderBy(p => p.Name)
            .Take(20)
            .ToListAsync();

        return Ok(hits.Select(p => ToDto(p, canManage)).ToList());
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Get a person")]
    [EndpointDescription(
        "One person in detail: their contact fields, the **voice samples** training their voiceprint - which " +
        "recording and speaker each came from - and how many recording-speakers they currently label.\n\n" +
        "Use it to audit what a voiceprint has learned from: a sample from a misattributed speaker is why " +
        "recognition drifts, and can be removed individually. The embedding vector itself is never returned.")]
    public async Task<ActionResult<PersonDetailDto>> Get(Guid id)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();

        var identifiedCount = await _db.Speakers.CountAsync(s => s.PersonId == id);

        // Stitch recording display names + speaker labels in memory (provider-agnostic; no FK on RecordingId).
        var raw = await _db.VoiceSamples
            .Where(v => v.PersonId == id)
            .OrderBy(v => v.CreatedAt)
            .Select(v => new { v.Id, v.RecordingId, v.SpeakerId, v.CreatedAt })
            .ToListAsync();
        var recIds = raw.Select(v => v.RecordingId).ToList();
        var spIds = raw.Select(v => v.SpeakerId).ToList();
        var recMap = (await _db.Recordings.Where(r => recIds.Contains(r.Id))
                .Select(r => new { r.Id, Display = r.Name ?? r.Title }).ToListAsync())
            .ToDictionary(r => r.Id, r => r.Display);
        var spMap = (await _db.Speakers.Where(s => spIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Label }).ToListAsync())
            .ToDictionary(s => s.Id, s => s.Label);

        // Earliest segment start (ms) for each contributing speaker in its recording's current transcription,
        // so the UI can play a sample of that voice. Computed in memory (provider-agnostic).
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

        var samples = raw.Select(v =>
        {
            var label = spMap.TryGetValue(v.SpeakerId, out var l) ? l : "";
            return new VoiceSampleDto(
                v.Id, v.RecordingId,
                recMap.TryGetValue(v.RecordingId, out var d) ? d : "(deleted recording)",
                label, StartFor(v.RecordingId, label), v.CreatedAt);
        }).ToList();

        return new PersonDetailDto(ToDto(person, await CanManagePeopleAsync()), identifiedCount, samples);
    }

    [HttpGet("duplicates")]
    [EndpointSummary("Find likely duplicate people")]
    [EndpointDescription(
        "Groups of people who look like the same human, matched by email address or by name once case and " +
        "spacing are normalised. A shared directory surfaces these: two colleagues who each enrolled the " +
        "same client privately now both appear.\n\n" +
        "This **only reports**. Merging is never automatic, because it deletes the source person, cannot be " +
        "undone, and in a shared directory a wrong merge affects everyone's recordings.")]
    public async Task<ActionResult<IReadOnlyList<PersonDuplicateGroupDto>>> Duplicates()
    {
        if (!await CanManagePeopleAsync()) return Forbid();

        var groups = PersonDuplicates.Find(await _db.People.ToListAsync());
        return Ok(groups
            .Select(g => new PersonDuplicateGroupDto(
                g.Reason, g.People.Select(p => ToDto(p, canManagePeople: true)).ToList()))
            .ToList());
    }

    // ---- Writing ----

    [HttpPost]
    [EndpointSummary("Add a person")]
    [EndpointDescription(
        "Adds someone to the directory. Only a name is required: a person with no voiceprint and no contact " +
        "details is a perfectly ordinary record, and is what you get when adding a client you have not " +
        "recorded yet.\n\n" +
        "Supplying `recordingId` and `label` **enrols a voiceprint in the same call**, from that recording's " +
        "diarized speaker - which is what the \"new person\" control on a transcript does. That speaker must " +
        "already have an embedding (one is computed during transcription), otherwise 400.")]
    public async Task<ActionResult<PersonDto>> Create(CreatePersonRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("A name is required.");

        var person = new Person
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = UserId,
            RoomId = await _rooms.PersonalRoomIdAsync(UserId), // provenance only; nothing filters on it
            Name = name,
            Title = Blank(req.Title),
            CompanyName = Blank(req.CompanyName),
            Email = Blank(req.Email),
            Phone = Blank(req.Phone),
            IsInternal = req.IsInternal ?? false,
            VoiceprintOptOut = req.VoiceprintOptOut ?? false,
        };

        if (req.RecordingId is { } recordingId && !string.IsNullOrWhiteSpace(req.Label))
        {
            if (person.VoiceprintOptOut)
                return Conflict("This person has opted out of voice-printing.");

            var owned = await _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);
            if (!owned) return NotFound();

            var speaker = await _db.Speakers
                .FirstOrDefaultAsync(s => s.RecordingId == recordingId && s.Label == req.Label);
            if (speaker is null) return NotFound();
            if (speaker.Embedding is null)
                return BadRequest("This speaker has no voice embedding yet (re-transcribe to compute one).");

            person.Embedding = speaker.Embedding;
            person.SampleCount = 1;
            _db.People.Add(person);
            _db.VoiceSamples.Add(new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id,
                RecordingId = recordingId, Embedding = speaker.Embedding,
            });
            speaker.PersonId = person.Id;
            speaker.DisplayName = name;
            speaker.IdentifiedAuto = false;
        }
        else
        {
            _db.People.Add(person);
        }

        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = person.Id }, ToDto(person, await CanManagePeopleAsync()));
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Edit a person")]
    [EndpointDescription(
        "Updates a person's details. Every field is optional and **omitting one leaves it alone** - send only " +
        "what changed.\n\n" +
        "Editing anyone other than yourself requires the **Manage people** permission. **Name and email " +
        "cannot be set on a person linked to a user account** (400): those follow the account, and a change " +
        "here would be undone by the next sync.\n\n" +
        "Setting `voiceprintOptOut` to true **erases the person's voiceprint** and every voice sample behind " +
        "it, and stops them being matched from then on. Labels that identification applied revert to the " +
        "anonymous speaker label; names typed by hand are kept, and stay linked to the person. Setting it " +
        "back to false restores nothing.")]
    public async Task<IActionResult> Update(Guid id, UpdatePersonRequest req)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();

        if (person.LinkedUserId is not null && (req.Name is not null || req.Email is not null))
            return BadRequest("Name and email follow the linked user account and cannot be set here.");

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (string.IsNullOrWhiteSpace(name)) return BadRequest("A name is required.");
            person.Name = name;
            // DisplayName is denormalised onto every speaker identified as them, so a rename must fan out.
            foreach (var speaker in await _db.Speakers.Where(s => s.PersonId == id).ToListAsync())
                speaker.DisplayName = name;
        }

        if (req.Title is not null) person.Title = Blank(req.Title);
        if (req.CompanyName is not null) person.CompanyName = Blank(req.CompanyName);
        if (req.Email is not null) person.Email = Blank(req.Email);
        if (req.Phone is not null) person.Phone = Blank(req.Phone);
        if (req.IsInternal is { } isInternal) person.IsInternal = isInternal;

        var erasing = req.VoiceprintOptOut is true && !person.VoiceprintOptOut;
        if (req.VoiceprintOptOut is { } optOut) person.VoiceprintOptOut = optOut;

        person.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        if (erasing) await _people.EraseVoiceprintAsync(id);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete a person")]
    [EndpointDescription(
        "Removes the person, their voiceprint and all its training data, and unlinks them from every " +
        "recording - the **GDPR erasure** path, so nothing recognisable is retained.\n\n" +
        "Labels are handled by origin: names applied **automatically** revert to the anonymous speaker " +
        "label, while names you typed by hand are kept, since those are your words rather than derived from " +
        "the biometric. Transcripts are otherwise untouched.\n\n" +
        "Requires **Manage people**: the directory is shared, so this removes them from everyone's " +
        "recordings. To erase only the biometric and keep the person, erase their voiceprint instead.")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManagePeopleAsync()) return Forbid();

        await UnlinkAndRevertAsync([id]);
        _db.People.Remove(person); // cascades VoiceSamples
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/merge")]
    [EndpointSummary("Merge two people")]
    [EndpointDescription(
        "Folds `sourceId` into the person in the path when the same human is in the directory twice - say " +
        "once as \"Sam\" and once as \"Samantha\", or once per colleague who enrolled them. The source's " +
        "voice samples move across, every recording labelled with it is relabelled, the voiceprint is " +
        "recomputed from the combined samples, and the **source person is deleted**.\n\n" +
        "There is no un-merge, so check the direction: the person in the path survives. The directory is " +
        "shared, so a wrong merge affects everyone's recordings - hence **Manage people**.")]
    public async Task<IActionResult> Merge(Guid id, MergePeopleRequest req)
    {
        if (req.SourceId == id) return BadRequest("Cannot merge a person into itself.");
        if (!await CanManagePeopleAsync()) return Forbid();

        var target = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        var source = await _db.People.FirstOrDefaultAsync(p => p.Id == req.SourceId);
        if (target is null || source is null) return NotFound();

        foreach (var sample in await _db.VoiceSamples.Where(v => v.PersonId == source.Id).ToListAsync())
            sample.PersonId = target.Id;

        foreach (var speaker in await _db.Speakers.Where(s => s.PersonId == source.Id).ToListAsync())
        {
            speaker.PersonId = target.Id;
            speaker.DisplayName = target.Name;
        }

        _db.People.Remove(source);
        await _db.SaveChangesAsync();
        await _people.RecomputeVoiceprintAsync(target.Id);
        return NoContent();
    }

    // ---- Voiceprints ----

    [HttpPost("{id:guid}/voiceprint")]
    [EndpointSummary("Enrol a voiceprint")]
    [EndpointDescription(
        "Teaches this person's voice from one recording's diarized speaker: that speaker's embedding is " +
        "added as a voice sample and the voiceprint is recomputed. From then on the same voice is " +
        "recognised automatically in later recordings.\n\n" +
        "The speaker must already **have an embedding** - one is computed during transcription, so a " +
        "recording made before voiceprints were enabled needs re-transcribing first (400). **409 when the " +
        "person has opted out** of voice-printing.")]
    public async Task<IActionResult> EnrolVoiceprint(Guid id, EnrolVoiceprintRequest req)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (person.VoiceprintOptOut) return Conflict("This person has opted out of voice-printing.");

        var owned = await _db.Recordings.AnyAsync(r => r.Id == req.RecordingId && r.UserId == UserId);
        if (!owned) return NotFound();

        var speaker = await _db.Speakers
            .FirstOrDefaultAsync(s => s.RecordingId == req.RecordingId && s.Label == req.Label);
        if (speaker is null) return NotFound();
        if (speaker.Embedding is null)
            return BadRequest("This speaker has no voice embedding yet (re-transcribe to compute one).");

        var already = await _db.VoiceSamples.AnyAsync(v => v.PersonId == id && v.SpeakerId == speaker.Id);
        if (!already)
        {
            _db.VoiceSamples.Add(new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = id, SpeakerId = speaker.Id,
                RecordingId = req.RecordingId, Embedding = speaker.Embedding,
            });
        }

        speaker.PersonId = id;
        speaker.DisplayName = person.Name;
        speaker.IdentifiedAuto = false;
        speaker.IsMultiSpeaker = false;
        await _db.SaveChangesAsync();

        await _people.RecomputeVoiceprintAsync(id);
        return NoContent();
    }

    [HttpDelete("{id:guid}/voiceprint")]
    [EndpointSummary("Erase a person's voiceprint")]
    [EndpointDescription(
        "Destroys the voiceprint and every voice sample behind it, **keeping the person** and their contact " +
        "details. Use it when the biometric should go but the record of who attended should not - the " +
        "narrower half of the GDPR erasure path.\n\n" +
        "Labels that identification applied revert to the anonymous speaker label; names typed by hand are " +
        "kept. Unlike opting out, this does not stop them being enrolled again later.\n\n" +
        "Requires **Manage people**, except on the person linked to your own account.")]
    public async Task<IActionResult> DeleteVoiceprint(Guid id)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();

        await _people.EraseVoiceprintAsync(id);
        return NoContent();
    }

    [HttpDelete("{id:guid}/voiceprint/samples/{sampleId:guid}")]
    [EndpointSummary("Remove a voice sample")]
    [EndpointDescription(
        "Drops one sample from a person's voiceprint and **recomputes it from what remains** - the fix when " +
        "a misattributed speaker has been taught to the wrong person and recognition has started drifting.\n\n" +
        "Removing the **last** sample clears the voiceprint and leaves the person in the directory without " +
        "one, so Diariz stops recognising them by voice. It does not delete them. Recordings already " +
        "labelled keep their names either way.")]
    public async Task<IActionResult> RemoveVoiceSample(Guid id, Guid sampleId)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();

        var sample = await _db.VoiceSamples.FirstOrDefaultAsync(v => v.Id == sampleId && v.PersonId == id);
        if (sample is null) return NotFound();

        _db.VoiceSamples.Remove(sample);
        await _db.SaveChangesAsync();
        await _people.RecomputeVoiceprintAsync(id);
        return NoContent();
    }

    [HttpDelete("voiceprints")]
    [EndpointSummary("Erase every voiceprint")]
    [EndpointDescription(
        "Deletes **every** person in the directory, with all their voiceprints and training data, in one " +
        "call - the wholesale GDPR erasure. Automatic speaker identification stops platform-wide until " +
        "people are enrolled again.\n\n" +
        "Same labelling rule as deleting one person: automatically applied names revert to the anonymous " +
        "label, hand-typed names are kept. There is no undo and no confirmation step, so gate it in your " +
        "UI.\n\n" +
        "Requires **Manage platform**: the directory is shared, so this wipes everyone's, not just yours.")]
    public async Task<IActionResult> DeleteAllVoiceprints()
    {
        if (!await _permissions.HasAsync(UserId, PlatformPermission.ManagePlatform)) return Forbid();

        var people = await _db.People.ToListAsync();
        if (people.Count == 0) return NoContent();

        await UnlinkAndRevertAsync(people.Select(p => p.Id).ToList());
        _db.People.RemoveRange(people); // cascades VoiceSamples
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Unlink every speaker pointing at the given people and revert auto-applied names to the
    /// anonymous label; hand-typed names are left intact.
    ///
    /// <para>Deliberately broader than <c>IPeopleDirectory.EraseVoiceprintAsync</c>, which keeps hand-typed
    /// names <em>linked</em>. Here the person is going away entirely, so the link cannot survive - but the
    /// name still can, because it was the user's word rather than the biometric's.</para></summary>
    private async Task UnlinkAndRevertAsync(IReadOnlyCollection<Guid> personIds)
    {
        var linked = await _db.Speakers
            .Where(s => s.PersonId != null && personIds.Contains(s.PersonId.Value)).ToListAsync();
        foreach (var speaker in linked)
        {
            speaker.PersonId = null;
            if (speaker.IdentifiedAuto)
            {
                speaker.DisplayName = speaker.Label;
                speaker.IdentifiedAuto = false;
            }
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
