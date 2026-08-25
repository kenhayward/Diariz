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
    private readonly IJobQueue _queue;
    private readonly IAudioClipper _clipper;
    private readonly IAudioStorage _storage;
    private readonly ILogger<PeopleController> _logger;
    private readonly IPlatformSettingsService _settings;

    public PeopleController(
        DiarizDbContext db, IRoomScope rooms, IPeopleDirectory people, IUserPermissions permissions,
        IJobQueue queue, IAudioClipper clipper, IAudioStorage storage, ILogger<PeopleController> logger,
        IPlatformSettingsService settings)
    {
        _db = db;
        _rooms = rooms;
        _people = people;
        _permissions = permissions;
        _queue = queue;
        _clipper = clipper;
        _storage = storage;
        _logger = logger;
        _settings = settings;
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
            .Select(v => new { v.Id, v.RecordingId, v.SpeakerId, v.CreatedAt, v.SpansJson, v.UsedMs })
            .ToListAsync();
        var recIds = raw.Select(v => v.RecordingId).ToList();
        var spIds = raw.Select(v => v.SpeakerId).ToList();
        var recMap = (await _db.Recordings.Where(r => recIds.Contains(r.Id))
                .Select(r => new { r.Id, Display = r.Name ?? r.Title }).ToListAsync())
            .ToDictionary(r => r.Id, r => r.Display);
        var spMap = (await _db.Speakers.Where(s => spIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Label, s.EmbeddingStale }).ToListAsync())
            .ToDictionary(s => s.Id, s => s);

        // Earliest segment start (ms) for each contributing speaker in its recording's current transcription,
        // so the UI can play a sample of that voice. Computed in memory (provider-agnostic).
        var currentTrByRecording = (await _db.Transcriptions
                .Where(t => recIds.Contains(t.RecordingId))
                .Select(t => new { t.Id, t.RecordingId, t.Version }).ToListAsync())
            .GroupBy(t => t.RecordingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Version).First().Id);
        var trIds = currentTrByRecording.Values.ToList();
        var segs = (await _db.Segments
                .Where(s => trIds.Contains(s.TranscriptionId))
                .Select(s => new { s.TranscriptionId, s.SpeakerLabel, s.StartMs, s.EndMs }).ToListAsync())
            .GroupBy(s => (s.TranscriptionId, s.SpeakerLabel))
            .ToDictionary(g => g.Key, g => g.ToList());

        long StartFor(Guid recordingId, string label) =>
            currentTrByRecording.TryGetValue(recordingId, out var trId)
            && segs.TryGetValue((trId, label), out var rows) && rows.Count > 0 ? rows.Min(s => s.StartMs) : 0;

        // With nothing selected the sample trains on the whole speaker, so "selected" is everything that
        // speaker says. Reporting 0 there would read as "trains on nothing".
        long WholeSpeakerMs(Guid recordingId, string label) =>
            currentTrByRecording.TryGetValue(recordingId, out var trId)
            && segs.TryGetValue((trId, label), out var rows)
                ? rows.Sum(s => Math.Max(0, s.EndMs - s.StartMs))
                : 0;

        var samples = raw.Select(v =>
        {
            var speaker = spMap.TryGetValue(v.SpeakerId, out var sp) ? sp : null;
            var label = speaker?.Label ?? "";
            var spans = VoiceprintSpans.Parse(v.SpansJson);
            return new VoiceSampleDto(
                v.Id, v.RecordingId,
                recMap.TryGetValue(v.RecordingId, out var d) ? d : "(deleted recording)",
                label, StartFor(v.RecordingId, label), v.CreatedAt,
                spans.Count > 0 ? VoiceprintSpans.TotalMs(spans) : WholeSpeakerMs(v.RecordingId, label),
                v.UsedMs,
                // Derived from the speaker rather than stored on the sample: two columns saying the same
                // thing would eventually disagree.
                speaker?.EmbeddingStale ?? false,
                v.SpansJson is not null && v.UsedMs is null,
                spans);
        }).ToList();

        return new PersonDetailDto(ToDto(person, await CanManagePeopleAsync()), identifiedCount, samples);
    }

    [HttpGet("diagnostics")]
    [EndpointSummary("Rank the directory by voiceprint health")]
    [EndpointDescription(
        "Everyone whose training set contains a sample resembling none of their others, worst first. A " +
        "directory of any size makes the per-person view unusable on its own - knowing **which** people to " +
        "look at is most of the work.\n\n" +
        "People with one sample or none are omitted rather than ranked last: they have nothing wrong, only " +
        "nothing to say.")]
    public async Task<ActionResult<IReadOnlyList<PersonDiagnosticsSummaryDto>>> DirectoryDiagnostics()
    {
        if (!await CanManagePeopleAsync()) return Forbid();

        var thresholds = IdentificationThresholds.From(await _settings.GetAsync());

        // Every training sample in one read. The whole directory is a few hundred 192-d vectors, and a query
        // per person would be a query per person.
        var samples = await _db.VoiceSamples
            .Where(v => v.ExcludedAt == null && v.Embedding != null)
            .Select(v => new { v.Id, v.PersonId, v.Embedding })
            .ToListAsync();

        var byPerson = samples
            .GroupBy(v => v.PersonId)
            .Where(g => g.Count() > 1)
            .ToList();
        if (byPerson.Count == 0) return Ok(Array.Empty<PersonDiagnosticsSummaryDto>());

        var personIds = byPerson.Select(g => g.Key).ToList();
        var names = await _db.People
            .Where(p => personIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        var rows = byPerson.Select(g =>
        {
            var vectors = g.Select(v => (v.Id, v.Embedding!.ToArray())).ToList();
            var diagnosed = VoiceprintDiagnosis.Diagnose(vectors, thresholds);
            return new PersonDiagnosticsSummaryDto(
                g.Key,
                names.GetValueOrDefault(g.Key, ""),
                vectors.Count,
                diagnosed.Count(d => d.Verdict == SampleVerdict.Alone),
                Widest(vectors.Select(v => v.Item2).ToList()));
        })
        // Worst first: most samples resembling nothing else, then most scattered. Anyone with no outlier at
        // all is dropped - an empty problem list is noise, and a list that includes healthy people is worse
        // than useless because the real ones stop standing out.
        .Where(r => r.AloneCount > 0)
        .OrderByDescending(r => r.AloneCount)
        .ThenByDescending(r => r.WidestPair ?? 0)
        .ToList();

        return Ok(rows);
    }

    private static double? Widest(IReadOnlyList<float[]> vectors)
    {
        double? widest = null;
        for (var i = 0; i < vectors.Count; i++)
        for (var j = i + 1; j < vectors.Count; j++)
        {
            var d = Voiceprints.CosineDistance(vectors[i], vectors[j]);
            if (widest is null || d > widest) widest = d;
        }
        return widest;
    }

    [HttpGet("{id:guid}/diagnostics")]
    [EndpointSummary("Check whether a person's samples resemble each other")]
    [EndpointDescription(
        "Scores every sample training this person's voiceprint against the others, to show which ones do " +
        "not belong. Two distances per sample: to its **closest companion**, and to the centre of **the " +
        "person's other samples** - the second is a true leave-one-out, and they disagree when a pair sits " +
        "together but away from everything else.\n\n" +
        "A verdict of `Alone` does not mean wrong. It means that sample resembles none of the others, which " +
        "is either a recording condition nothing else covers - a phone, a car - or a **different person " +
        "enrolled under this name**. Only listening tells you which, so the answer says where to look.\n\n" +
        "Needs no re-transcription and no audio: it reads embeddings that already exist.")]
    public async Task<ActionResult<VoiceprintDiagnosticsDto>> Diagnostics(Guid id)
    {
        if (!await CanManagePeopleAsync()) return Forbid();
        if (!await _db.People.AnyAsync(p => p.Id == id)) return NotFound();

        var samples = await _db.VoiceSamples
            .Where(v => v.PersonId == id)
            .OrderBy(v => v.CreatedAt)
            .ToListAsync();

        // Diagnosed against the training set only. An excluded sample is not teaching the voiceprint
        // anything, so letting it define what the others are measured against would mean dropping an outlier
        // made everything else look like one instead.
        var training = samples.Where(v => v.ExcludedAt is null && v.Embedding is not null).ToList();

        var thresholds = IdentificationThresholds.From(await _settings.GetAsync());
        var diagnosed = VoiceprintDiagnosis
            .Diagnose(training.Select(v => (v.Id, v.Embedding.ToArray())).ToList(), thresholds)
            .ToDictionary(d => d.SampleId);

        // Recording names and speaker labels, stitched in memory - VoiceSample deliberately has no FK to its
        // recording, and a deleted one must not take the sample with it.
        var recIds = samples.Select(v => v.RecordingId).Distinct().ToList();
        var spIds = samples.Select(v => v.SpeakerId).Distinct().ToList();
        var recMap = (await _db.Recordings.Where(r => recIds.Contains(r.Id))
                .Select(r => new { r.Id, Display = r.Name ?? r.Title }).ToListAsync())
            .ToDictionary(r => r.Id, r => r.Display);
        var spMap = (await _db.Speakers.Where(sp => spIds.Contains(sp.Id))
                .Select(sp => new { sp.Id, sp.Label }).ToListAsync())
            .ToDictionary(sp => sp.Id, sp => sp.Label);

        var rows = samples.Select(v =>
        {
            var d = diagnosed.GetValueOrDefault(v.Id);
            return new SampleDiagnosisDto(
                v.Id, v.SpeakerId, v.RecordingId,
                recMap.TryGetValue(v.RecordingId, out var name) ? name : "(deleted recording)",
                spMap.TryGetValue(v.SpeakerId, out var label) ? label : "",
                d?.NearestSiblingDistance,
                d?.DistanceToOthers,
                // An excluded sample was not diagnosed, so it has no verdict of its own to report.
                (d?.Verdict ?? SampleVerdict.Only).ToString(),
                v.ExcludedAt is null);
        }).ToList();

        return Ok(new VoiceprintDiagnosticsDto(
            rows,
            diagnosed.Values.Count(d => d.Verdict == SampleVerdict.Alone),
            WidestPair(training)));
    }

    /// <summary>The largest distance between any two of a person's training samples - one number for "how
    /// scattered is this person", which is what the directory ranking sorts on. Null when there is no pair to
    /// measure.</summary>
    private static double? WidestPair(IReadOnlyList<VoiceSample> training) =>
        Widest(training.Select(v => v.Embedding.ToArray()).ToList());

    [HttpGet("{id:guid}/attributions")]
    [EndpointSummary("List the speakers attributed to a person")]
    [EndpointDescription(
        "Every recording-speaker currently identified as this person, whether or not it trains their " +
        "voiceprint. Automatic identification links a speaker **without** creating a voice sample, so this " +
        "is a strictly larger set than the samples returned by `GET /api/people/{id}` - which is why a list " +
        "built from samples alone looked arbitrary.\n\n" +
        "`canAccessRecording` is false when you neither own the recording nor hold **Manage voiceprints**. " +
        "The row is still listed, because it is part of what the voiceprint learned from, but its " +
        "transcript and audio are not available to you.")]
    public async Task<ActionResult<IReadOnlyList<PersonAttributionDto>>> Attributions(Guid id)
    {
        if (!await CanManagePeopleAsync()) return Forbid();
        if (!await _db.People.AnyAsync(p => p.Id == id)) return NotFound();

        var speakers = await _db.Speakers
            .Where(s => s.PersonId == id)
            .Select(s => new { s.Id, s.RecordingId, s.Label, s.IdentifiedAuto, s.IsMultiSpeaker })
            .ToListAsync();

        var recIds = speakers.Select(s => s.RecordingId).Distinct().ToList();

        var recordings = await _db.Recordings
            .Where(r => recIds.Contains(r.Id))
            .Select(r => new { r.Id, r.UserId, Display = r.Name ?? r.Title })
            .ToListAsync();

        // Cross-owner access is exactly what ManageVoiceprints grants. Without it the row is still listed -
        // it is part of the training provenance - but inert.
        var canAssess = await _permissions.HasAsync(UserId, PlatformPermission.ManageVoiceprints);
        var accessible = recordings
            .Where(r => canAssess || r.UserId == UserId)
            .Select(r => r.Id)
            .ToHashSet();

        // Speech per speaker comes from the current transcription's segments, which the API already stores -
        // no worker involvement, and the same figure a minimum-duration gate would read.
        var currentTr = (await _db.Transcriptions
                .Where(t => recIds.Contains(t.RecordingId))
                .Select(t => new { t.Id, t.RecordingId, t.Version })
                .ToListAsync())
            .GroupBy(t => t.RecordingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Version).First().Id);

        var trIds = currentTr.Values.ToList();
        var speech = (await _db.Segments
                .Where(s => trIds.Contains(s.TranscriptionId))
                .Select(s => new { s.TranscriptionId, s.SpeakerLabel, s.StartMs, s.EndMs })
                .ToListAsync())
            .GroupBy(s => (s.TranscriptionId, s.SpeakerLabel))
            .ToDictionary(g => g.Key, g => g.Sum(s => Math.Max(0, s.EndMs - s.StartMs)));

        long SpeechFor(Guid recordingId, string label) =>
            currentTr.TryGetValue(recordingId, out var trId) && speech.TryGetValue((trId, label), out var ms)
                ? ms
                : 0;

        var samples = await _db.VoiceSamples.Where(v => v.PersonId == id).ToListAsync();

        return Ok(PersonAttributions.Build(
            speakers
                .Select(s => new AttributionInput(
                    s.Id, s.RecordingId, s.Label, s.IdentifiedAuto, s.IsMultiSpeaker,
                    SpeechFor(s.RecordingId, s.Label)))
                .ToList(),
            samples,
            recordings.ToDictionary(r => r.Id, r => r.Display),
            accessible));
    }

    [HttpPut("{id:guid}/attributions/{speakerId:guid}/training")]
    [EndpointSummary("Include or exclude a speaker from a person's voiceprint")]
    [EndpointDescription(
        "Adds a speaker already attributed to this person into their voiceprint training set, or removes " +
        "it. Adding needs no re-transcription: the speaker's embedding was computed when the recording was " +
        "transcribed.\n\n" +
        "Removing **excludes rather than deletes** the sample, so the record that someone identified this " +
        "speaker as this person survives and re-including it is a toggle. **409 when the person has opted " +
        "out** of voice-printing, or when the speaker is marked as overlapping speech.")]
    public async Task<IActionResult> SetTraining(Guid id, Guid speakerId, SetTrainingRequest req)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();

        var speaker = await _db.Speakers.FirstOrDefaultAsync(s => s.Id == speakerId && s.PersonId == id);
        if (speaker is null) return NotFound();

        var sample = await _db.VoiceSamples
            .FirstOrDefaultAsync(v => v.PersonId == id && v.SpeakerId == speakerId);

        if (req.Training)
        {
            if (person.VoiceprintOptOut) return Conflict("This person has opted out of voice-printing.");
            if (speaker.IsMultiSpeaker)
                return Conflict("Overlapping speech cannot train a single person's voiceprint.");

            if (sample is null)
            {
                if (speaker.Embedding is null)
                    return BadRequest("This speaker has no voice embedding yet (re-transcribe to compute one).");
                _db.VoiceSamples.Add(new VoiceSample
                {
                    Id = Guid.NewGuid(),
                    PersonId = id,
                    SpeakerId = speakerId,
                    RecordingId = speaker.RecordingId,
                    Embedding = speaker.Embedding,
                });
            }
            else
            {
                sample.ExcludedAt = null;
            }
        }
        else
        {
            if (sample is null) return NoContent(); // already not training; nothing to record
            // UTC, or Npgsql rejects it for the timestamptz column at SaveChanges.
            sample.ExcludedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync();
        await _people.RecomputeVoiceprintAsync(id);
        return NoContent();
    }

    /// <summary>A speaker whose audio the caller is allowed to assess, and the transcription that says which
    /// audio is theirs.</summary>
    private sealed record AssessmentTarget(Speaker Speaker, Recording Recording, Guid TranscriptionId);

    /// <summary>The gate every assessment surface passes through, in one place because two endpoints
    /// enforcing the same four rules separately is how they come to disagree.
    ///
    /// <list type="number">
    /// <item>The caller may act on this person at all.</item>
    /// <item>The speaker really is attributed to them.</item>
    /// <item>The caller owns the recording, <b>or</b> holds ManageVoiceprints.</item>
    /// <item>A current transcription exists to say which audio is this speaker's.</item>
    /// </list>
    ///
    /// <para>Returns the failure result to return verbatim, or the resolved target.</para></summary>
    private async Task<(ActionResult? Failure, AssessmentTarget? Target)> ResolveAssessmentTargetAsync(
        Guid personId, Guid speakerId, bool requireAudio)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == personId);
        if (person is null) return (NotFound(), null);
        if (!await CanManageBiometricsAsync(person)) return (Forbid(), null);

        var speaker = await _db.Speakers.FirstOrDefaultAsync(s => s.Id == speakerId && s.PersonId == personId);
        if (speaker is null) return (NotFound(), null);

        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == speaker.RecordingId);
        if (rec is null) return (NotFound(), null);
        if (requireAudio && rec.AudioDeletedAt is not null) return (NotFound(), null);

        if (rec.UserId != UserId
            && !await _permissions.HasAsync(UserId, PlatformPermission.ManageVoiceprints))
            return (Forbid(), null);

        var trId = await _db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .OrderByDescending(t => t.Version)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync();
        if (trId is null) return (NotFound(), null);

        return (null, new AssessmentTarget(speaker, rec, trId.Value));
    }

    [HttpGet("{id:guid}/attributions/{speakerId:guid}/segments")]
    [EndpointSummary("List what an attributed speaker said")]
    [EndpointDescription(
        "The segments this speaker spoke in the recording's current transcription, for choosing which audio " +
        "trains a voiceprint.\n\n" +
        "**Only that speaker's segments** - never the recording's transcript. Reading a recording you do not " +
        "own requires **Manage voiceprints**, and that grant is deliberately limited to the person under " +
        "assessment: everybody else's words in the same meeting stay out of reach.")]
    public async Task<ActionResult<IReadOnlyList<AttributionSegmentDto>>> AttributionSegments(
        Guid id, Guid speakerId)
    {
        // Audio may be gone while the transcript remains, and reading what someone said does not need it.
        var (failure, target) = await ResolveAssessmentTargetAsync(id, speakerId, requireAudio: false);
        if (failure is not null) return failure;

        var rows = await _db.Segments
            .Where(s => s.TranscriptionId == target!.TranscriptionId
                        && s.SpeakerLabel == target.Speaker.Label)
            .OrderBy(s => s.Ordinal)
            .Select(s => new AttributionSegmentDto(s.Id, s.StartMs, s.EndMs, s.Revised ?? s.Original))
            .ToListAsync();

        return Ok(rows);
    }

    [HttpGet("{id:guid}/clip")]
    [EndpointSummary("Play a clip of a person's speech")]
    [EndpointDescription(
        "Serves a short WAV clip of one span of audio, for judging by ear whether a voice really is this " +
        "person.\n\n" +
        "The span **must fall inside a segment this speaker spoke** in the recording's current " +
        "transcription - arbitrary offsets are refused with 404. Clips from a recording you do not own " +
        "additionally require **Manage voiceprints**, and every such access is logged. Clips are capped at " +
        "two minutes.")]
    [Produces("audio/wav")]
    public async Task<IActionResult> Clip(
        Guid id, [FromQuery] Guid speakerId, [FromQuery] long fromMs, [FromQuery] long toMs)
    {
        var (failure, target) = await ResolveAssessmentTargetAsync(id, speakerId, requireAudio: true);
        if (failure is not null) return failure;
        var (speaker, rec, currentTrId) = target!;

        // The span must be audio this speaker actually produced. Without this, ManageVoiceprints would grant
        // arbitrary offsets into someone else's meeting - which is precisely what it must not do. It applies
        // to owners too: it costs nothing and keeps one rule rather than two.
        var covered = await _db.Segments.AnyAsync(s =>
            s.TranscriptionId == currentTrId
            && s.SpeakerLabel == speaker.Label
            && s.StartMs <= fromMs && s.EndMs >= toMs);
        if (!covered) return NotFound();

        if (rec.UserId != UserId)
            _logger.LogInformation(
                "Cross-owner assessment clip: user {UserId} played {FromMs}-{ToMs} of recording {RecordingId} as person {PersonId}",
                UserId, fromMs, toMs, rec.Id, id);

        // Presigned and internal: ffmpeg range-seeks the object store rather than the API pulling a whole
        // recording to cut seconds out of it. The URL never leaves this process.
        var url = await _storage.GetPresignedReadUrlAsync(rec.BlobKey, TimeSpan.FromMinutes(5));
        return File(await _clipper.ClipAsync(url, fromMs, toMs), "audio/wav");
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
        "There is no un-merge, so check the direction: the person in the path survives - though it inherits " +
        "the source's account link, and any contact detail it was missing. The directory is " +
        "shared, so a wrong merge affects everyone's recordings - hence **Manage people**.\n\n" +
        "Two people who each have a Diariz account cannot be merged: they are two different humans, and one " +
        "of them would be detached from their account.")]
    public async Task<IActionResult> Merge(Guid id, MergePeopleRequest req)
    {
        if (req.SourceId == id) return BadRequest("Cannot merge a person into itself.");
        if (!await CanManagePeopleAsync()) return Forbid();

        var target = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        var source = await _db.People.FirstOrDefaultAsync(p => p.Id == req.SourceId);
        if (target is null || source is null) return NotFound();

        // Two accounts are two humans whatever their names look like, and there is no right answer to which
        // link survives. Refuse instead of silently picking one.
        if (target.LinkedUserId is not null && source.LinkedUserId is not null)
            return BadRequest(
                "Both of these people have a Diariz account, so they are two different people. Merging them " +
                "would detach one of them from their account. Rename one instead, or delete it.");

        foreach (var sample in await _db.VoiceSamples.Where(v => v.PersonId == source.Id).ToListAsync())
            sample.PersonId = target.Id;

        foreach (var speaker in await _db.Speakers.Where(s => s.PersonId == source.Id).ToListAsync())
        {
            speaker.PersonId = target.Id;
            speaker.DisplayName = target.Name;
        }

        // The source row is about to be deleted, so anything only it holds is destroyed unless it moves now.
        //
        // The account link matters most. Losing it detaches a real user from the directory, and because the
        // biometric self-exception resolves through LinkedUserId, that user silently loses the ability to opt
        // themselves out of voice-printing - a GDPR right, failing closed and with nothing on screen to say
        // so. It happened in production before this guard existed.
        var carriedLink = target.LinkedUserId ?? source.LinkedUserId;

        // Contact details are salvage, not a takeover: the target is the record being kept, so its own values
        // win and the source only fills the gaps. A merge should never lose a detail that existed a moment
        // ago, nor overwrite one the user just typed.
        target.Email = Fill(target.Email, source.Email);
        target.Title = Fill(target.Title, source.Title);
        target.CompanyName = Fill(target.CompanyName, source.CompanyName);
        target.Phone = Fill(target.Phone, source.Phone);

        // Both rows briefly hold the same LinkedUserId here, and one account is one person - the filtered
        // unique index would reject that if the UPDATE landed before the DELETE. EF Core orders the delete
        // first, so a single SaveChanges is safe today; it is not a documented guarantee, which is why
        // PeopleSchemaTests proves it against real Postgres rather than the in-memory provider, which
        // enforces no index at all.
        target.LinkedUserId = carriedLink;
        _db.People.Remove(source);
        await _db.SaveChangesAsync();

        await _people.RecomputeVoiceprintAsync(target.Id);
        return NoContent();
    }

    /// <summary>Keeps what the surviving record already has, falling back to the record being absorbed.
    /// Whitespace counts as absent - a field the user cleared is not a value worth preserving.</summary>
    private static string? Fill(string? keep, string? salvage) =>
        string.IsNullOrWhiteSpace(keep) ? salvage : keep;

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

    [HttpPut("{id:guid}/voiceprint/samples/{sampleId:guid}/spans")]
    [EndpointSummary("Choose which audio trains one voice sample")]
    [EndpointDescription(
        "Replaces the spans of the contributing recording's audio that this sample is embedded from, and " +
        "queues a re-embed. Send an **empty list** to go back to the whole speaker, which is what every " +
        "sample does by default. Adjacent spans are merged, so you can send one per segment the user " +
        "ticked.\n\n" +
        "Returns **202**: the worker does the work, and it shares a process with transcription, so it can " +
        "queue behind one. Until it reports back the sample reads as pending (`usedMs` is null). The " +
        "worker still caps how much audio it pools, so `usedMs` may be less than the total selected - the " +
        "UI states both.\n\n" +
        "**409** when the person has opted out of voice-printing, or the recording's audio has been " +
        "deleted. **403** without permission to manage this person's biometrics (Manage people, or it is " +
        "you).")]
    public async Task<IActionResult> SetVoiceSampleSpans(
        Guid id, Guid sampleId, SetVoiceSampleSpansRequest req)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();
        if (person.VoiceprintOptOut) return Conflict("This person has opted out of voice-printing.");

        var sample = await _db.VoiceSamples.FirstOrDefaultAsync(v => v.Id == sampleId && v.PersonId == id);
        if (sample is null) return NotFound();

        // Queueing a job the worker can only fail is worse than saying so here.
        var recording = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == sample.RecordingId);
        if (recording is null || recording.AudioDeletedAt is not null)
            return Conflict("This recording's audio is no longer available to re-embed from.");

        var spans = VoiceprintSpans.FromSegments(req.Spans.Select(s => (s.StartMs, s.EndMs)));
        sample.SpansJson = VoiceprintSpans.Serialize(spans);
        // Clearing this is what makes "pending" survive a page reload: it is derived, not held in the
        // component. The callback sets it again.
        sample.UsedMs = null;
        await _db.SaveChangesAsync();

        await _queue.EnqueueVoiceprintAsync(
            new VoiceprintJob(sample.Id, recording.Id, recording.BlobKey, spans));
        return Accepted();
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
