using System.Security.Claims;
using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/recordings")]
public class RecordingsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IAudioStorage _storage;
    private readonly IJobQueue _queue;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly ISummarizationSettingsResolver _summarization;
    private readonly IEmailSender _email;
    private readonly ISpeakerIdentifier _identifier;
    private readonly UploadOptions _uploads;
    private readonly IRoomScope _rooms;
    private readonly IWebhookPublisher _webhooks;
    private readonly IOptions<AppPublicOptions> _appOpts;
    private readonly IExportLocalizer? _exportLocalizer;
    private readonly IGoogleCalendarClient? _calendar;
    private readonly string _defaultModel;

    public RecordingsController(
        DiarizDbContext db, IAudioStorage storage, IJobQueue queue,
        IHubContext<TranscriptionHub> hub, IConfiguration config,
        ISummarizationSettingsResolver summarization, IEmailSender email, ISpeakerIdentifier identifier,
        IOptions<UploadOptions> uploads, IRoomScope rooms, IWebhookPublisher webhooks,
        IOptions<AppPublicOptions> appOpts, IExportLocalizer? exportLocalizer = null,
        IGoogleCalendarClient? calendar = null)
    {
        _db = db;
        _storage = storage;
        _queue = queue;
        _hub = hub;
        _summarization = summarization;
        _email = email;
        _identifier = identifier;
        _uploads = uploads.Value;
        _rooms = rooms;
        _webhooks = webhooks;
        _appOpts = appOpts;
        _exportLocalizer = exportLocalizer;
        _calendar = calendar;
        _defaultModel = config["Transcription:DefaultModel"] ?? "whisperx-large-v3";
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>The export/email labels in the caller's UI language (falls back to English).</summary>
    private async Task<ExportStrings> ExportLabelsAsync()
    {
        if (_exportLocalizer is null) return ExportStrings.English;
        var uiLang = (await _db.UserSettings.FindAsync(UserId))?.UiLanguage;
        return _exportLocalizer.For(uiLang);
    }

    [HttpGet]
    [EndpointSummary("List recordings")]
    [EndpointDescription(
        "Every recording placed in one room, in the order it appears in the app. Defaults to your personal " +
        "room; pass `roomId` for a shared room you belong to. Each entry carries its folder, duration, " +
        "transcription status, and whether its audio is still stored. Returns 404 if you are not a member of " +
        "the room, rather than revealing that it exists.")]
    public async Task<ActionResult<IReadOnlyList<RecordingSummaryDto>>> List([FromQuery] Guid? roomId = null)
    {
        // The recordings of the room being viewed (its personal room by default). A folder is a property of the
        // placement in THAT room. Membership is the read gate - a non-member 404s rather than learning it exists.
        var room = roomId ?? await _rooms.PersonalRoomIdAsync(UserId);
        if (!await _rooms.IsMemberAsync(UserId, room)) return NotFound();

        var folderOf = await _db.RoomRecordings
            .Where(p => p.RoomId == room)
            .Select(p => new { p.RecordingId, p.SectionId, SectionName = p.Section != null ? p.Section.Name : null })
            .ToDictionaryAsync(x => x.RecordingId, x => (x.SectionId, x.SectionName));

        // Recordings placed in this room (main + shared), ordered by the placement's per-room position (ties:
        // newest first). Project server-side, then splice the folder in memory.
        var rows = await (
            from p in _db.RoomRecordings
            where p.RoomId == room
            join r in _db.Recordings on p.RecordingId equals r.Id
            orderby p.Position, r.CreatedAt descending
            select new
            {
                r.Id, r.Title, r.Name, r.Source, r.DurationMs, r.Status, r.CreatedAt,
                HasActions = r.Actions.Any(), HasAudio = r.AudioDeletedAt == null,
                EventId = r.CalendarLink != null ? r.CalendarLink.EventId : null,
                Color = r.CalendarLink != null ? r.CalendarLink.Color : null,
                r.MeetingTypeId,
            })
            .ToListAsync();

        return rows
            .Select(r =>
            {
                var folder = folderOf.TryGetValue(r.Id, out var f) ? f : (SectionId: (Guid?)null, SectionName: (string?)null);
                return new RecordingSummaryDto(r.Id, r.Title, r.Name, r.Source, r.DurationMs, r.Status, r.CreatedAt,
                    folder.SectionId, folder.SectionName, r.HasActions, r.HasAudio, r.EventId, r.Color, r.MeetingTypeId);
            })
            .ToList();
    }

    /// <summary>Drag-and-drop within a room: set the section + 0-based position of each listed recording in one
    /// call (both within-group sequencing and cross-group moves). Order and folder are per-placement, so this
    /// only touches the placements in <paramref name="req"/>'s room (the caller's personal room by default).
    /// Needs <c>ManageContents</c> in that room; the personal-room owner holds it, so personal reorder is
    /// unchanged. A non-member 404s; a member without the permission gets 403.</summary>
    [HttpPut("reorder")]
    [EndpointSummary("Reorder recordings in a room")]
    [EndpointDescription(
        "Sets the folder and 0-based position of each listed recording in one call, covering both resequencing " +
        "within a folder and moving between folders. Order and folder belong to the placement, so only the " +
        "given room is affected (your personal room by default). Requires the `ManageContents` permission in " +
        "that room; you always hold it in your own personal room. Ids you do not list keep their position.")]
    public async Task<IActionResult> Reorder(ReorderRecordingsRequest req)
    {
        var ids = (req.OrderedIds ?? []).ToList();
        if (ids.Count == 0) return NoContent();

        var roomId = req.RoomId ?? await _rooms.PersonalRoomIdAsync(UserId);
        if (!await _rooms.IsMemberAsync(UserId, roomId)) return NotFound();
        if (!(await _rooms.PermissionsAsync(UserId, roomId)).HasFlag(RoomPermission.ManageContents)) return Forbid();

        if (req.SectionId is { } sectionId &&
            !await _db.Sections.AnyAsync(s => s.Id == sectionId && s.RoomId == roomId))
            return NotFound(); // can't move into a section that isn't in this room

        // Every listed recording must actually be placed in this room; order + folder live on the placement.
        var placements = await _db.RoomRecordings
            .Where(p => p.RoomId == roomId && ids.Contains(p.RecordingId))
            .ToDictionaryAsync(p => p.RecordingId);
        if (placements.Count != ids.Count) return NotFound();

        for (var i = 0; i < ids.Count; i++)
        {
            var placement = placements[ids[i]];
            placement.Position = i;
            placement.SectionId = req.SectionId;
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Get a recording")]
    [EndpointDescription(
        "The full detail view: metadata, speakers, action items, calendar link, the rooms you can see it in, " +
        "and the **current transcription only** (the highest version) with its segments, summary, and meeting " +
        "minutes. Earlier versions left behind by a re-transcribe are not returned. Visible to the person who " +
        "recorded it and to members of any room it is placed in; anyone else gets 404.")]
    public async Task<ActionResult<RecordingDetailDto>> Get(Guid id)
    {
        var rec = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.CalendarLink)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (rec is null) return NotFound();

        // Visible to the recorder, or to a member of any room it is placed in - the same "can read" rule
        // IRoomScope.CanReadRecordingAsync codifies for per-recording sub-resources (screenshots, meeting
        // notes, ...). ReadAccessForRecordingAsync is the single walk that answers both this gate and the
        // room list below (a member should not learn about rooms they are not in) - one call, one walk.
        var access = await _rooms.ReadAccessForRecordingAsync(UserId, id);
        if (!access.CanRead) return NotFound();
        var visibleRooms = access.VisibleRooms
            .Select(p => new RecordingRoomDto(p.RoomId, p.Name, p.Icon, p.Color, p.IsMainRoom))
            .ToList();

        var recordedByName = await _db.Users
            .Where(u => u.Id == rec.UserId)
            .Select(u => u.FullName ?? u.Email)
            .FirstOrDefaultAsync();

        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        var actions = rec.Actions
            .OrderBy(a => a.Ordinal)
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal, a.Completed, a.CompletedAt))
            .ToList();
        var speakers = rec.Speakers
            .OrderBy(s => s.Label)
            .Select(s => new SpeakerInfoDto(s.Label, s.DisplayName, s.ProfileId, s.IdentifiedAuto, s.IsMultiSpeaker))
            .ToList();
        var current = rec.Transcriptions.FirstOrDefault();
        TranscriptionDto? tDto = current is null ? null : new(
            current.Id, current.Model, current.Version, current.Language, current.CreatedAt,
            current.Segments.Select(s => new SegmentDto(
                s.Id,
                s.SpeakerLabel,
                names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.Original, s.Revised)).ToList(),
            current.ProcessingMs);
        SummaryDto? sDto = current?.Summary is null ? null
            : new(current.Summary.Model, current.Summary.Text, current.Summary.CreatedAt, current.Summary.IsUserEdited);
        MeetingMinutesDto? mDto = current?.MeetingMinutes is null ? null
            : new(current.MeetingMinutes.Model, current.MeetingMinutes.Text, current.MeetingMinutes.CreatedAt,
                current.MeetingMinutes.IsUserEdited);

        // Projected auto-deletion date: only when the policy is on and this recording is a live, unprotected,
        // eligible candidate (matches the nightly sweep's predicate). Read the singleton settings row directly
        // (no lazy-create in a GET); absent/off -> no projection.
        var platform = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == Diariz.Domain.Entities.PlatformSettings.SingletonId);
        DateTimeOffset? scheduledDeletion =
            platform is { AutoDeleteAudioEnabled: true }
            && rec.HasAudio && !rec.IsAudioProtected
            && AudioRetentionSweep.IsTranscribedStatus(rec.Status)
                ? rec.CreatedAt.AddDays(platform.AudioRetentionDays)
                : null;

        return new RecordingDetailDto(rec.Id, rec.Title, rec.Name, rec.Source, rec.DurationMs, rec.SizeBytes,
            rec.Status, rec.Error, rec.CreatedAt, rec.MinSpeakers, rec.MaxSpeakers, names, speakers, tDto, sDto,
            mDto, actions, rec.ActionsExtractedAt != null, rec.HasAudio, ToLinkDto(rec.CalendarLink),
            rec.MeetingTypeId, rec.AudioProtectedAt, rec.AudioDeletedAt, scheduledDeletion,
            rec.UserId, recordedByName, visibleRooms);
    }

    private static CalendarLinkDto? ToLinkDto(RecordingCalendarLink? link) => link is null
        ? null
        : new CalendarLinkDto(link.EventId, link.CalendarId, link.Summary, link.StartsAt, link.EndsAt, link.HtmlLink, link.LinkedManually, link.Color);

    /// <summary>Upload an audio file and kick off transcription.</summary>
    [HttpPost]
    [EndpointSummary("Upload a recording")]
    [EndpointDescription(
        "Multipart form upload that stores the audio and immediately queues transcription; the response comes " +
        "back before any transcribing has happened, with status `Uploaded`. Poll the recording or subscribe to " +
        "the `recording.transcribed` webhook for the result.\n\n" +
        "`source=Upload` (a file the user picked, rather than a browser recording) is additionally checked: the " +
        "format is sniffed from the actual bytes rather than trusted from the extension or MIME type " +
        "(415 if unsupported), and the file is capped by the platform's upload limit (413). Every source counts " +
        "against your storage quota (413 when it would be exceeded).\n\n" +
        "The recording is always filed in your personal room. Passing `roomId` for a shared room also shares it " +
        "there and needs `CreateRecording` in that room; `sectionId` files it in a folder of your personal room.")]
    [RequestSizeLimit(1024L * 1024 * 1024)] // 1 GiB
    public async Task<ActionResult<RecordingSummaryDto>> Upload(
        [FromForm] IFormFile audio, [FromForm] string? title, [FromForm] long durationMs,
        [FromForm] RecordingSource source = RecordingSource.Microphone, [FromForm] Guid? sectionId = null,
        [FromForm] Guid? roomId = null)
    {
        if (audio is null || audio.Length == 0) return BadRequest("Empty audio.");

        // User-uploaded files (vs. browser recordings) are size-capped and format-gated by their actual
        // bytes — never trust the client extension/MIME.
        if (source == RecordingSource.Upload && audio.Length > _uploads.MaxBytes)
            return StatusCode(413, $"File too large. The maximum upload size is {_uploads.MaxBytes / (1024 * 1024)} MB.");

        // Enforce the owner's storage quota (audio bytes only — DB rows don't count).
        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _db.Recordings.Where(r => r.UserId == UserId).SumAsync(r => r.SizeBytes);
        if (used + audio.Length > quota)
            return StatusCode(413,
                "Storage quota exceeded. Delete some recordings or ask an administrator to raise your quota.");

        // Recording into a shared room: verify the caller may record there before storing anything. The main
        // placement is still the recorder's personal room; the room gets a second, shared placement below.
        var personalRoomId = await _rooms.PersonalRoomIdAsync(UserId);
        var intoSharedRoom = roomId is { } rid && rid != personalRoomId;
        if (intoSharedRoom &&
            !(await _rooms.PermissionsAsync(UserId, roomId!.Value)).HasFlag(RoomPermission.CreateRecording))
            return StatusCode(StatusCodes.Status403Forbidden, "You can't add recordings to that room.");

        await using var stream = audio.OpenReadStream();

        if (source == RecordingSource.Upload)
        {
            var head = new byte[16];
            var read = await stream.ReadAsync(head);
            if (stream.CanSeek) stream.Position = 0;
            var (ok, _, reason) = AudioFormats.Validate(head.AsSpan(0, read), _uploads.AllowAac);
            if (!ok) return StatusCode(StatusCodes.Status415UnsupportedMediaType, reason);
        }

        var rec = new Recording
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Title = string.IsNullOrWhiteSpace(title) ? $"Recording {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}" : title,
            Source = source,
            ContentType = audio.ContentType,
            SizeBytes = audio.Length,
            DurationMs = durationMs,
            Status = RecordingStatus.Uploaded
        };
        rec.BlobKey = $"{UserId}/{rec.Id}{Path.GetExtension(audio.FileName)}";

        await _storage.UploadAsync(rec.BlobKey, stream, rec.ContentType);

        _db.Recordings.Add(rec);
        await EnqueueTranscriptionAsync(rec);
        await _db.SaveChangesAsync();

        var publicUrl = string.IsNullOrWhiteSpace(_appOpts.Value.PublicUrl)
            ? $"{Request.Scheme}://{Request.Host}" : _appOpts.Value.PublicUrl;
        await _webhooks.PublishAsync(WebhookEventTypes.RecordingCreated, UserId, new
        {
            recordingId = rec.Id, name = rec.Name ?? rec.Title, source = rec.Source.ToString(),
            status = rec.Status.ToString(), links = WebhookPayload.For(publicUrl, rec.Id),
        });

        // The folder is a property of the placement, not the recording: create the main placement in the
        // uploader's personal room. When recording into a shared room the main placement is Ungrouped (the
        // shared link is ungrouped for now); otherwise honour a requested folder only if it belongs to that
        // room, so a stale or alien section id can never misfile the recording.
        var placementSection = intoSharedRoom
            ? null
            : sectionId is { } sid && await _db.Sections.AnyAsync(s => s.Id == sid && s.RoomId == personalRoomId)
                ? sectionId
                : null;
        await _rooms.PlaceInMainRoomAsync(rec.Id, UserId, placementSection);

        // Recording into a shared room also shares it there (a second, non-main placement).
        if (intoSharedRoom)
            await _rooms.ShareIntoRoomAsync(rec.Id, roomId!.Value, UserId, sectionId: null);

        return CreatedAtAction(nameof(Get), new { id = rec.Id },
            new RecordingSummaryDto(rec.Id, rec.Title, rec.Name, rec.Source, rec.DurationMs, rec.Status, rec.CreatedAt,
                null, null, false, rec.HasAudio));
    }

    [HttpPost("{id:guid}/retranscribe")]
    [EndpointSummary("Re-transcribe a recording")]
    [EndpointDescription(
        "Queues a fresh transcription and returns 202 immediately. This creates a **new transcription version** " +
        "rather than replacing the old one; the recording then reports the new version as its current " +
        "transcript, and any manual segment edits on the previous version stay with that version. Speaker " +
        "display names you have set are preserved.\n\n" +
        "Send `speakers` to set the diarization hints (minimum and maximum speaker count; null means " +
        "automatic); omit it entirely to reuse whatever the recording already has.\n\n" +
        "The job reads the original audio, so a recording whose audio has been deleted is still accepted here " +
        "but the transcription itself then fails - check `hasAudio` before offering this.")]
    public async Task<IActionResult> Retranscribe(Guid id, RetranscribeRequest req)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        // Tri-state: a present Speakers object sets the diarization hints (null bounds = automatic);
        // an absent one leaves the recording's existing hints untouched (e.g. the list re-transcribe).
        if (req.Speakers is { } hints)
        {
            if (hints.Min is < 1 || hints.Max is < 1) return BadRequest("Speaker counts must be at least 1.");
            if (hints.Min is { } mn && hints.Max is { } mx && mn > mx)
                return BadRequest("Minimum speakers can't exceed the maximum.");
            rec.MinSpeakers = hints.Min;
            rec.MaxSpeakers = hints.Max;
        }

        await EnqueueTranscriptionAsync(rec, req.Model);
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpPut("{id:guid}/speakers")]
    [EndpointSummary("Rename a speaker")]
    [EndpointDescription(
        "Sets the display name shown against every segment carrying a diarization label (`SPEAKER_00`, ...) in " +
        "this recording. The label itself never changes, so the name survives a re-transcribe. Creates the " +
        "speaker record if the label has not been named yet.\n\n" +
        "A free-text rename is treated as a correction by hand: it detaches the speaker from any enrolled " +
        "voiceprint, clears the auto-identified flag, and takes it out of \"Multiple Speakers\" mode. To point a " +
        "speaker at an enrolled profile instead, use the assign endpoint.")]
    public async Task<IActionResult> RenameSpeaker(Guid id, RenameSpeakerRequest req)
    {
        var rec = await _db.Recordings.Include(r => r.Speakers)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var speaker = rec.Speakers.FirstOrDefault(s => s.Label == req.Label);
        if (speaker is null)
        {
            // Add to the DbSet (not the loaded nav collection) so EF tracks it as Added/INSERT.
            // Adding via rec.Speakers leaves the new row in the wrong state and SaveChanges
            // emits an UPDATE that affects 0 rows -> DbUpdateConcurrencyException.
            speaker = new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = req.Label };
            _db.Speakers.Add(speaker);
        }
        // A free-text rename detaches the speaker from any voiceprint (it's now hand-typed text)
        // and exits "Multiple Speakers" mode (the user has named a single person).
        speaker.DisplayName = req.DisplayName;
        speaker.ProfileId = null;
        speaker.IdentifiedAuto = false;
        speaker.IsMultiSpeaker = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Mark a recording's speaker as "Multiple Speakers" (overlapping/simultaneous speech). Such a
    /// speaker is detached from any voiceprint and excluded from auto-identification and enrolment, since
    /// its audio mixes people. Clearing happens implicitly when the user renames/assigns the speaker.</summary>
    [HttpPut("{id:guid}/speakers/{label}/multi")]
    [EndpointSummary("Mark a speaker as multiple speakers")]
    [EndpointDescription(
        "Flags a diarization label as overlapping or simultaneous speech rather than one person. Because its " +
        "audio mixes voices, the speaker is detached from any voiceprint and excluded from both automatic " +
        "identification and enrolment - so it can never pollute a stored voiceprint. There is no explicit " +
        "\"unmark\": renaming or assigning the speaker clears the flag.")]
    public async Task<IActionResult> MarkMultiSpeaker(Guid id, string label)
    {
        var rec = await _db.Recordings.Include(r => r.Speakers)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var speaker = rec.Speakers.FirstOrDefault(s => s.Label == label);
        if (speaker is null)
        {
            // Add to the DbSet (not the loaded nav collection) so EF tracks it as Added/INSERT.
            speaker = new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = label };
            _db.Speakers.Add(speaker);
        }
        speaker.IsMultiSpeaker = true;
        speaker.DisplayName = Speaker.MultiSpeakerName;
        speaker.ProfileId = null;
        speaker.IdentifiedAuto = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Reassign a recording's speaker to an enrolled voiceprint (or unassign with profileId=null).
    /// Assigning adds the speaker as a training contribution and recomputes the profile centroid.</summary>
    [HttpPut("{id:guid}/speakers/{label}/assign")]
    [EndpointSummary("Assign a speaker to an enrolled voiceprint")]
    [EndpointDescription(
        "Points a diarization label at one of your enrolled speaker profiles, taking its name from the profile. " +
        "Pass a null `profileId` to unassign.\n\n" +
        "Assigning also **teaches the profile**: this recording's speaker is added as a training contribution " +
        "and the profile's voiceprint centroid is recomputed, so later recordings identify that person more " +
        "reliably. Use the rename endpoint instead when you just want a label, with no effect on enrolment.")]
    public async Task<IActionResult> AssignSpeaker(Guid id, string label, AssignSpeakerRequest req)
    {
        var rec = await _db.Recordings.Include(r => r.Speakers)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var speaker = rec.Speakers.FirstOrDefault(s => s.Label == label);
        if (speaker is null) return NotFound();

        if (req.ProfileId is null)
        {
            // Unassign → revert to the anonymous label (and exit "Multiple Speakers" mode).
            speaker.ProfileId = null;
            speaker.DisplayName = speaker.Label;
            speaker.IdentifiedAuto = false;
            speaker.IsMultiSpeaker = false;
            await _db.SaveChangesAsync();
            return NoContent();
        }

        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(p => p.Id == req.ProfileId && p.UserId == UserId);
        if (profile is null) return NotFound();

        speaker.ProfileId = profile.Id;
        speaker.DisplayName = profile.Name;
        speaker.IdentifiedAuto = false; // an explicit manual assignment
        speaker.IsMultiSpeaker = false; // naming a single person exits "Multiple Speakers" mode

        // Train "by whole speakers": record this speaker as a contribution (once) and recompute the
        // centroid from all of the profile's contribution snapshots. Requires the speaker embedding,
        // which only exists once the worker has run — skip gracefully when it hasn't.
        if (speaker.Embedding is not null)
        {
            var already = await _db.ProfileContributions
                .AnyAsync(c => c.ProfileId == profile.Id && c.SpeakerId == speaker.Id);
            if (!already)
            {
                _db.ProfileContributions.Add(new ProfileContribution
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profile.Id,
                    SpeakerId = speaker.Id,
                    RecordingId = rec.Id,
                    Embedding = speaker.Embedding,
                });

                var snapshots = await _db.ProfileContributions
                    .Where(c => c.ProfileId == profile.Id)
                    .Select(c => c.Embedding)
                    .ToListAsync();
                snapshots.Add(speaker.Embedding); // the not-yet-saved contribution
                var centroid = Voiceprints.Centroid(snapshots.Select(v => v.ToArray()).ToList());
                if (centroid is not null) profile.Embedding = centroid;
                profile.SampleCount = snapshots.Count;
                profile.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Collapse consecutive same-speaker segments in the current transcription into single
    /// blocks (run after fixing speaker assignments). Permanent for this version; re-transcribe to
    /// regenerate granular segments.</summary>
    [HttpPost("{id:guid}/merge-segments")]
    [EndpointSummary("Merge consecutive same-speaker segments")]
    [EndpointDescription(
        "Collapses runs of consecutive segments that share a speaker into single blocks, which is what you want " +
        "after correcting speaker assignments has left the transcript fragmented. Text is joined and the block " +
        "spans from the first segment's start to the last one's end.\n\n" +
        "**Permanent for this transcription version** - there is no un-merge. Re-transcribe to get granular " +
        "segments back (that creates a new version and leaves this one intact).")]
    public async Task<IActionResult> MergeSegments(Guid id)
    {
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
        if (!owned) return NotFound();

        var current = await _db.Transcriptions.Where(t => t.RecordingId == id)
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync();
        if (current is null) return NotFound();

        var segments = await _db.Segments.Where(s => s.TranscriptionId == current.Id)
            .OrderBy(s => s.Ordinal).ToListAsync();
        if (segments.Count == 0) return NotFound();

        // Group by the speaker's effective identity (assigned profile, else display name), not the raw
        // diarization label — so two labels reassigned to the same person merge together.
        var speakers = await _db.Speakers.Where(s => s.RecordingId == id)
            .ToDictionaryAsync(s => s.Label, s => s);
        string KeyFor(string label)
        {
            if (!speakers.TryGetValue(label, out var sp)) return $"l:{label}";
            if (sp.ProfileId is Guid pid) return $"p:{pid}";
            return string.IsNullOrEmpty(sp.DisplayName) ? $"l:{label}" : $"n:{sp.DisplayName}";
        }

        // A note or a screenshot sits between two segments; don't let a same-speaker merge swallow that
        // boundary (the note or image would jump to after the whole merged block). Flag the segment after
        // each anchor. Both kinds of capture use the same rule, so they share one break set.
        var noteTimes = await _db.MeetingNotes
            .Where(n => n.RecordingId == id && n.CapturedAtMs != null)
            .Select(n => n.CapturedAtMs!.Value)
            .ToListAsync();
        var shotTimes = await _db.MeetingScreenshots
            .Where(s => s.RecordingId == id)
            .Select(s => s.CapturedAtMs)
            .ToListAsync();
        var breakBefore = TranscriptNoteAnchor.BreakBeforeIndices(
            segments.Select(s => s.StartMs).ToList(), noteTimes.Concat(shotTimes));

        var merged = SegmentMerger.Merge(segments
            .Select((s, i) => new SegmentMerger.Part(
                KeyFor(s.SpeakerLabel), s.SpeakerLabel, s.StartMs, s.EndMs, s.EffectiveText, breakBefore.Contains(i)))
            .ToList());
        if (merged.Count == segments.Count) return NoContent(); // nothing adjacent to merge

        _db.Segments.RemoveRange(segments);
        var ordinal = 0;
        foreach (var p in merged)
            _db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = current.Id,
                SpeakerLabel = p.SpeakerLabel,
                StartMs = p.StartMs,
                EndMs = p.EndMs,
                // Merge consolidates the displayed (effective) text; the per-segment original/revised split
                // is intentionally collapsed into a fresh Original on the merged row.
                Original = p.Text,
                Ordinal = ordinal++
            });
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Re-run speaker identification against the owner's current voiceprints, using the speakers'
    /// already-stored embeddings (no re-transcription). Anonymous/auto speakers are (re)labelled; manual
    /// names are left alone.</summary>
    [HttpPost("{id:guid}/reidentify")]
    [EndpointSummary("Re-run speaker identification")]
    [EndpointDescription(
        "Matches this recording's speakers against your current voiceprints again, using the embeddings already " +
        "stored on the recording - so it is quick and does **not** re-transcribe or need the audio. Use it after " +
        "enrolling a new profile or improving an existing one.\n\n" +
        "Only anonymous and previously auto-identified speakers are relabelled. Names you set by hand, and " +
        "speakers marked as multiple speakers, are left alone.")]
    public async Task<IActionResult> Reidentify(Guid id)
    {
        var rec = await _db.Recordings.Include(r => r.Speakers)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        await SpeakerLabeling.ApplyAsync(rec.Speakers, rec.UserId, _identifier);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Email the current transcript to the signed-in user's account address.</summary>
    [HttpPost("{id:guid}/email")]
    [EndpointSummary("Email the transcript to yourself")]
    [EndpointDescription(
        "Sends the current transcript - the recording name, its summary, and the speaker-labelled segments - to " +
        "**your own account address**. There is no recipient parameter, by design: this endpoint can only mail " +
        "you. Labels are rendered in your interface language. Requires an email sender to be configured on the " +
        "platform.")]
    public async Task<IActionResult> EmailTranscript(Guid id)
    {
        var rec = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null || current.Segments.Count == 0) return NotFound();

        var address = await _db.Users.Where(u => u.Id == UserId).Select(u => u.Email).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(address)) return BadRequest("Your account has no email address.");

        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        var segs = current.Segments
            .OrderBy(s => s.Ordinal)
            .Select(s => new SegmentDto(
                s.Id, s.SpeakerLabel,
                names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.Original, s.Revised))
            .ToList();
        var actions = rec.Actions
            .OrderBy(a => a.Ordinal)
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal))
            .ToList();

        var name = rec.Name ?? rec.Title;
        var labels = await ExportLabelsAsync();
        var minutesHtml = string.IsNullOrWhiteSpace(current.MeetingMinutes?.Text)
            ? null : MarkdownRenderer.ToHtml(current.MeetingMinutes!.Text);
        var html = TranscriptEmail.BuildHtml(name, current.Summary?.Text, segs, actions, labels, minutesHtml);
        var sent = await _email.SendAsync(address!, TranscriptEmail.Subject(name, labels), html);
        if (!sent) return BadRequest("Email isn't configured on the server. Contact an administrator.");
        return Ok();
    }

    /// <summary>Email just the meeting minutes (Markdown → HTML) to the signed-in user's account address,
    /// optionally attaching the recording's uploaded files.</summary>
    [HttpPost("{id:guid}/meeting-minutes/email")]
    [EndpointSummary("Email the meeting minutes to yourself")]
    [EndpointDescription(
        "Sends just the minutes (Markdown rendered to HTML), without the transcript, to **your own account " +
        "address** - like the transcript email, there is no recipient parameter. Optionally attaches the " +
        "recording's uploaded files. Returns 400 when the current transcription has no minutes yet, when your " +
        "account has no email address, or when no email sender is configured on the platform.")]
    public async Task<IActionResult> EmailMeetingMinutes(Guid id, EmailMeetingMinutesRequest req)
    {
        var rec = await _db.Recordings
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current?.MeetingMinutes is null || string.IsNullOrWhiteSpace(current.MeetingMinutes.Text))
            return BadRequest("There are no meeting minutes to email yet.");

        var address = await _db.Users.Where(u => u.Id == UserId).Select(u => u.Email).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(address)) return BadRequest("Your account has no email address.");

        var name = rec.Name ?? rec.Title;
        var labels = await ExportLabelsAsync();
        var html = MeetingMinutesEmail.BuildHtml(name, MarkdownRenderer.ToHtml(current.MeetingMinutes.Text), labels);

        List<EmailAttachment>? files = null;
        if (req.IncludeAttachments)
        {
            var attachments = await _db.Attachments
                .Where(a => a.RecordingId == id && a.Kind == AttachmentKind.File && a.BlobKey != null)
                .OrderBy(a => a.Ordinal)
                .ToListAsync();
            files = new List<EmailAttachment>();
            foreach (var a in attachments)
            {
                await using var stream = await _storage.OpenReadAsync(a.BlobKey!);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                files.Add(new EmailAttachment(a.Name, a.ContentType ?? "application/octet-stream", ms.ToArray()));
            }
        }

        var sent = await _email.SendAsync(address!, MeetingMinutesEmail.Subject(name, labels), html, files);
        if (!sent) return BadRequest("Email isn't configured on the server. Contact an administrator.");
        return Ok();
    }

    /// <summary>Find the Google Calendar meeting this recording was most likely captured during (by time
    /// overlap against the recording's wall-clock span). Requires the user to have granted Calendar access.
    /// Returns <c>{ match: null }</c> when nothing overlaps.</summary>
    [HttpGet("{id:guid}/calendar-match")]
    [EndpointSummary("Suggest the calendar event this recording belongs to")]
    [EndpointDescription(
        "Looks for the Google Calendar meeting the recording was most likely captured during, by overlapping " +
        "its wall-clock span against your events. This only suggests - nothing is stored until you confirm it " +
        "with the calendar-link endpoint. Returns `{ \"match\": null }` when nothing overlaps, and requires you " +
        "to have connected Google Calendar.")]
    public async Task<IActionResult> CalendarMatch(Guid id, CancellationToken ct)
    {
        var settings = await _db.UserSettings.FindAsync([UserId], ct);
        if (settings?.GoogleCalendarGranted != true)
            return BadRequest("Connect Google Calendar in Preferences to match meetings.");

        var rec = await _db.Recordings
            .Where(r => r.Id == id && r.UserId == UserId)
            .Select(r => new { r.CreatedAt, r.DurationMs })
            .FirstOrDefaultAsync(ct);
        if (rec is null) return NotFound();

        // The recording's wall-clock span, padded so a meeting that started a little before recording began
        // (or a recording started a touch late) still matches.
        var pad = TimeSpan.FromMinutes(30);
        var recStart = rec.CreatedAt;
        var recEnd = rec.CreatedAt.AddMilliseconds(rec.DurationMs);
        var events = await _calendar!.ListEventsAsync(UserId, recStart - pad, recEnd + pad, ct);
        if (events is null) return BadRequest("Your Google connection needs reauthorising — reconnect Calendar in Preferences.");

        var best = GoogleCalendarClient.PickBest(events, recStart, recEnd);
        return Ok(new
        {
            match = best is null
                ? null
                : new { best.Id, best.Summary, Start = best.Start, End = best.End, best.HtmlLink, best.CalendarId, best.Color },
        });
    }

    /// <summary>Persist a link from this recording to a Google Calendar event (used both to accept the
    /// auto-suggested match and to pick one by hand, even when the times don't line up). Stores a lightweight
    /// snapshot; the rich invite details are fetched live. <c>Manual</c> links are never overwritten by the
    /// auto-match.</summary>
    [HttpPut("{id:guid}/calendar-link")]
    [EndpointSummary("Link a recording to a calendar event")]
    [EndpointDescription(
        "Stores the link, whether you are accepting the suggested match or picking an event by hand (the times " +
        "need not line up). Only a lightweight snapshot is kept - title, times, colour - and the richer invite " +
        "details are fetched live when displayed.\n\n" +
        "A link made by hand is marked manual and is never overwritten by the automatic matcher, so a " +
        "deliberate choice survives later re-matching. Replaces any existing link.")]
    public async Task<IActionResult> LinkCalendar(Guid id, LinkCalendarRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.EventId)) return BadRequest("An event id is required.");

        var settings = await _db.UserSettings.FindAsync([UserId], ct);
        if (settings?.GoogleCalendarGranted != true)
            return BadRequest("Connect Google Calendar in Preferences to link meetings.");

        var rec = await _db.Recordings
            .Include(r => r.CalendarLink)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct);
        if (rec is null) return NotFound();

        var ev = await _calendar!.GetEventAsync(UserId, req.EventId, ct);
        if (ev is null)
            return BadRequest("That calendar event could not be found - it may have been deleted, or your Google connection needs reauthorising.");

        var link = rec.CalendarLink;
        if (link is null)
        {
            link = new RecordingCalendarLink { RecordingId = rec.Id };
            _db.RecordingCalendarLinks.Add(link);
        }
        link.EventId = ev.Id;
        // Which calendar the event is on - taken from the found event (authoritative), else the request's hint,
        // else primary. Lets the live re-fetch/preview target the right calendar.
        link.CalendarId = ev.CalendarId ?? req.CalendarId ?? "primary";
        link.Color = ev.Color;
        link.Summary = ev.Summary;
        // Google returns the event's local UTC offset (e.g. 09:00+01:00); Npgsql rejects a non-zero-offset
        // DateTimeOffset for a `timestamptz` column, so normalise to UTC before storing.
        link.StartsAt = ev.Start.ToUniversalTime();
        link.EndsAt = ev.End.ToUniversalTime();
        link.HtmlLink = ev.HtmlLink;
        link.LinkedManually = req.Manual;
        link.SyncedAt = DateTimeOffset.UtcNow;
        // Adopt any pre-meeting notes the user attached to this event (one-way, additive - see MeetingNoteAdoption).
        await MeetingNoteAdoption.AdoptAsync(_db, UserId, rec.Id, link.CalendarId, link.EventId, ct);
        await _db.SaveChangesAsync(ct);

        return Ok(ToLinkDto(link));
    }

    /// <summary>Remove this recording's calendar link (idempotent).</summary>
    [HttpDelete("{id:guid}/calendar-link")]
    [EndpointSummary("Remove a recording's calendar link")]
    [EndpointDescription(
        "Detaches the recording from its calendar event. Idempotent - unlinking a recording that has no link " +
        "succeeds. Only the link is removed; the calendar event itself is never touched.")]
    public async Task<IActionResult> UnlinkCalendar(Guid id, CancellationToken ct)
    {
        var rec = await _db.Recordings
            .Include(r => r.CalendarLink)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct);
        if (rec is null) return NotFound();

        if (rec.CalendarLink is not null)
        {
            _db.RecordingCalendarLinks.Remove(rec.CalendarLink);
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    [HttpPut("{id:guid}/segments/{segmentId:guid}")]
    [EndpointSummary("Edit a segment's text")]
    [EndpointDescription(
        "Corrects one line of the transcript. The edit is stored as a **revision** alongside the model's " +
        "original, which is never overwritten, so you can always see what was actually said. Send a null `text` " +
        "to clear the revision and fall back to the original; an empty string is a revision to empty text, not " +
        "a reset. Timings and the speaker label are not editable here.")]
    public async Task<IActionResult> UpdateSegment(Guid id, Guid segmentId, UpdateSegmentRequest req)
    {
        var seg = await _db.Segments.Include(s => s.Transcription)
            .FirstOrDefaultAsync(s => s.Id == segmentId);
        if (seg?.Transcription is null || seg.Transcription.RecordingId != id) return NotFound();

        // Ownership: the segment's recording must belong to the caller.
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
        if (!owned) return NotFound();

        // Edits land on Revised, preserving the model's Original. A null Text resets to the original
        // (clears the revision); a value (incl. "") sets the revision.
        seg.Revised = req.Text;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Delete a single segment from the current transcription (e.g. a meaningless filler row).
    /// Permanent for this version — re-transcribe to regenerate the full set. Remaining segments are
    /// renumbered so their ordinals stay contiguous.</summary>
    [HttpDelete("{id:guid}/segments/{segmentId:guid}")]
    [EndpointSummary("Delete a segment")]
    [EndpointDescription(
        "Removes one line from the current transcript - a filler row, a stray noise, and so on. Survivors are " +
        "renumbered so ordinals stay contiguous. If this was a speaker's last remaining segment, that speaker " +
        "drops off the recording and its stored voiceprint is cleared (any enrolled profile is untouched).\n\n" +
        "**Permanent for this transcription version.** Re-transcribe to regenerate the full set.")]
    public async Task<IActionResult> DeleteSegment(Guid id, Guid segmentId)
    {
        var seg = await _db.Segments.Include(s => s.Transcription)
            .FirstOrDefaultAsync(s => s.Id == segmentId);
        if (seg?.Transcription is null || seg.Transcription.RecordingId != id) return NotFound();

        // Ownership: the segment's recording must belong to the caller.
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
        if (!owned) return NotFound();

        var transcriptionId = seg.Transcription.Id;
        _db.Segments.Remove(seg);

        // Renumber the survivors contiguously from 0 (preserving order).
        var survivors = await _db.Segments
            .Where(s => s.TranscriptionId == transcriptionId && s.Id != segmentId)
            .OrderBy(s => s.Ordinal)
            .ToListAsync();
        var ordinal = 0;
        foreach (var s in survivors) s.Ordinal = ordinal++;

        await PruneOrphanSpeakersAsync(id, survivors);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Remove Speaker rows for this recording whose label no longer appears in any remaining segment
    /// (so deleting a speaker's last segment drops it from the Speakers list and clears its stored voiceprint).
    /// The enrolled Person profile is untouched — Segment↔Speaker is a label match, not an FK.</summary>
    private async Task PruneOrphanSpeakersAsync(Guid recordingId, IEnumerable<Segment> survivors)
    {
        var live = survivors.Select(s => s.SpeakerLabel).ToHashSet();
        var orphans = await _db.Speakers
            .Where(sp => sp.RecordingId == recordingId && !live.Contains(sp.Label))
            .ToListAsync();
        if (orphans.Count > 0) _db.Speakers.RemoveRange(orphans);
    }

    /// <summary>Delete several segments from the current transcription at once (the Select-mode bulk delete),
    /// renumbering the survivors once. Ids that aren't on this caller's recording are ignored.</summary>
    [HttpPost("{id:guid}/segments/delete")]
    [EndpointSummary("Delete several segments")]
    [EndpointDescription(
        "Bulk form of deleting a segment, renumbering the survivors once at the end rather than per row. Ids " +
        "that do not belong to this recording are skipped rather than failing the request, so a partly stale " +
        "selection still works. Same permanence: this affects the current transcription version only, and " +
        "re-transcribing regenerates the full set.")]
    public async Task<IActionResult> DeleteSegments(Guid id, DeleteSegmentsRequest req)
    {
        var current = await _db.Transcriptions.Where(t => t.RecordingId == id)
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync();
        if (current is null) return NotFound();
        if (!await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId)) return NotFound();

        var ids = (req.Ids ?? Array.Empty<Guid>()).ToHashSet();
        var segments = await _db.Segments
            .Where(s => s.TranscriptionId == current.Id)
            .OrderBy(s => s.Ordinal)
            .ToListAsync();

        var toRemove = segments.Where(s => ids.Contains(s.Id)).ToList();
        if (toRemove.Count > 0)
        {
            _db.Segments.RemoveRange(toRemove);
            // Renumber the survivors contiguously from 0, once.
            var survivors = segments.Where(s => !ids.Contains(s.Id)).ToList();
            var ordinal = 0;
            foreach (var s in survivors) s.Ordinal = ordinal++;
            await PruneOrphanSpeakersAsync(id, survivors);
            await _db.SaveChangesAsync();
        }
        return NoContent();
    }

    [HttpPost("{id:guid}/summarize")]
    [EndpointSummary("Generate a summary")]
    [EndpointDescription(
        "Queues an LLM summary of the current transcription and returns 202 immediately; poll the recording for " +
        "the result. While it runs the recording's status is `Summarizing`. Calling again during that window is " +
        "a no-op rather than queuing a second job.\n\n" +
        "Asking for this explicitly means \"overwrite\": a summary you had edited by hand loses its protection " +
        "and will be replaced. Returns 400 when no LLM endpoint is configured for you or the platform.")]
    public async Task<IActionResult> Summarize(Guid id)
    {
        var rec = await _db.Recordings
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null) return NotFound();

        var cfg = await _summarization.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Summarisation is not configured. Set an LLM endpoint in Settings.");

        // Idempotent: a summary is already in flight, don't enqueue a second job.
        if (rec.Status == RecordingStatus.Summarizing) return Accepted();

        // A user-initiated summarise is an explicit "overwrite" — clear the protected-edit flag so the
        // queued job replaces a hand-edited summary (the UI warns before getting here).
        if (current.Summary is { IsUserEdited: true } s) s.IsUserEdited = false;

        rec.Status = RecordingStatus.Summarizing;
        await _queue.EnqueueSummarizationAsync(new SummarizationJob(rec.Id, current.Id));
        await _hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());
        await _db.SaveChangesAsync();
        return Accepted();
    }

    /// <summary>Manually create or edit the current transcription's summary. Works even when no LLM is
    /// configured, and marks the summary as user-edited so the automatic summariser won't overwrite it.</summary>
    [HttpPut("{id:guid}/summary")]
    [EndpointSummary("Write the summary by hand")]
    [EndpointDescription(
        "Creates or replaces the current transcription's summary with your own text. Works with no LLM " +
        "configured at all, so it is the way to attach a summary on a platform with no model set up.\n\n" +
        "The summary is then marked user-edited, which **protects it**: the automatic summariser will not " +
        "overwrite it. Asking for a summary explicitly clears that protection.")]
    public async Task<IActionResult> UpdateSummary(Guid id, UpdateSummaryRequest req)
    {
        var rec = await _db.Recordings
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null) return NotFound();

        var summary = current.Summary;
        if (summary is null)
        {
            summary = new Summary { Id = Guid.NewGuid(), TranscriptionId = current.Id };
            _db.Summaries.Add(summary);
        }
        summary.Text = req.Text ?? string.Empty;
        summary.Model = Summary.UserEditedModel;
        summary.IsUserEdited = true;
        summary.UpdatedAt = DateTimeOffset.UtcNow;

        // The recording now has a summary — reflect that in its status (without clobbering an in-flight job).
        if (rec.Status is RecordingStatus.Transcribed) rec.Status = RecordingStatus.Summarized;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Re-create the current transcription's meeting minutes via the LLM. Clears the protected-edit
    /// flag first (the UI warns), so this explicit re-create overwrites hand-edited minutes.</summary>
    [HttpPost("{id:guid}/meeting-minutes/generate")]
    [EndpointSummary("Generate the meeting minutes")]
    [EndpointDescription(
        "Rebuilds the current transcription's minutes with the LLM, following the recording's meeting type " +
        "(the template naming the formula that produces them). Queued in the background: returns 202 " +
        "immediately, so poll the recording for the finished minutes. Returns 400 when no LLM endpoint is " +
        "configured for you or the platform.\n\n" +
        "This is an explicit re-create, so it **overwrites minutes you had edited by hand** - the protected-edit " +
        "flag is cleared first. Warn the user before calling it.")]
    public async Task<IActionResult> GenerateMeetingMinutes(Guid id)
    {
        var rec = await _db.Recordings
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null) return NotFound();

        var cfg = await _summarization.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Summarisation is not configured. Set an LLM endpoint in Settings.");

        if (current.MeetingMinutes is { IsUserEdited: true } m) m.IsUserEdited = false;

        await _queue.EnqueueMeetingMinutesAsync(new MeetingMinutesJob(rec.Id, current.Id));
        await _db.SaveChangesAsync();
        return Accepted();
    }

    /// <summary>Choose the meeting type driving this recording's minutes and re-generate. Null = the General
    /// default. The type must be usable by the owner (a Platform type or one they own). Clears the protected-edit
    /// flag so the run overwrites hand-edited minutes.</summary>
    [HttpPost("{id:guid}/meeting-type")]
    [EndpointSummary("Set the meeting type and regenerate the minutes")]
    [EndpointDescription(
        "Chooses the template driving this recording's minutes and regenerates them in one step. Pass a null id " +
        "for the General default. The type must be one you can use - a platform-wide type or one you own - " +
        "otherwise you get 404. Like the explicit regenerate it is queued (202) and **overwrites hand-edited " +
        "minutes**; 400 when no LLM endpoint is configured.")]
    public async Task<IActionResult> ApplyMeetingType(Guid id, ApplyMeetingTypeRequest req)
    {
        var rec = await _db.Recordings
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null) return NotFound();

        // A non-null type must exist and be usable by this user (Platform, or their own Personal type).
        if (req.MeetingTypeId is { } typeId &&
            !await _db.MeetingTypes.AnyAsync(t => t.Id == typeId && (t.UserId == null || t.UserId == UserId)))
            return NotFound();

        var cfg = await _summarization.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Summarisation is not configured. Set an LLM endpoint in Settings.");

        rec.MeetingTypeId = req.MeetingTypeId;
        if (current.MeetingMinutes is { IsUserEdited: true } m) m.IsUserEdited = false;

        await _queue.EnqueueMeetingMinutesAsync(new MeetingMinutesJob(rec.Id, current.Id));
        await _db.SaveChangesAsync();
        return Accepted();
    }

    /// <summary>Manually create or edit the current transcription's meeting minutes (Markdown). Works even
    /// with no LLM configured, and marks the minutes user-edited so the automatic generator won't overwrite
    /// them.</summary>
    [HttpPut("{id:guid}/meeting-minutes")]
    [EndpointSummary("Write the meeting minutes by hand")]
    [EndpointDescription(
        "Creates or replaces the current transcription's minutes with your own Markdown. Works with no LLM " +
        "configured. The minutes are then marked user-edited, which **protects them** from the automatic " +
        "generator - though an explicit regenerate, or applying a meeting type, still overwrites them.")]
    public async Task<IActionResult> UpdateMeetingMinutes(Guid id, UpdateMeetingMinutesRequest req)
    {
        var rec = await _db.Recordings
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null) return NotFound();

        var minutes = current.MeetingMinutes;
        if (minutes is null)
        {
            minutes = new MeetingMinutes { Id = Guid.NewGuid(), TranscriptionId = current.Id };
            _db.MeetingMinutes.Add(minutes);
        }
        minutes.Text = req.Text ?? string.Empty;
        minutes.Model = MeetingMinutes.UserEditedModel;
        minutes.IsUserEdited = true;
        minutes.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:guid}/name")]
    [EndpointSummary("Rename a recording")]
    [EndpointDescription(
        "Sets the display name. This is separate from the automatic title assigned at upload, which is kept: " +
        "send a blank name to clear yours and fall back to that title. The summariser also fills this in " +
        "automatically for a recording that has no name yet, so setting one stops that.")]
    public async Task<IActionResult> Rename(Guid id, RenameRecordingRequest req)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        rec.Name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim();
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:guid}/section")]
    [EndpointSummary("File a recording in a folder")]
    [EndpointDescription(
        "Moves the recording into a folder, or ungroups it with a null `sectionId`. The folder belongs to the " +
        "placement in one room, not to the recording itself, so this only affects the given room (your personal " +
        "room by default) - the same recording can sit in a different folder in each room it is shared into.\n\n" +
        "Requires `ManageContents` in that room; you always hold it in your own personal room. The recording " +
        "must already be placed in the room, and the target folder must belong to it.")]
    public async Task<IActionResult> MoveToSection(Guid id, MoveRecordingRequest req)
    {
        // The folder lives on the placement in the room being viewed (its personal room by default; null section
        // = ungroup). Filing needs ManageContents in that room; the personal-room owner holds it, so a plain
        // personal move is unchanged. A non-member 404s; a member without the permission gets 403.
        var roomId = req.RoomId ?? await _rooms.PersonalRoomIdAsync(UserId);
        if (!await _rooms.IsMemberAsync(UserId, roomId)) return NotFound();
        if (!(await _rooms.PermissionsAsync(UserId, roomId)).HasFlag(RoomPermission.ManageContents)) return Forbid();

        // The recording must actually be placed in this room, and the target section must live in it.
        if (!await _db.RoomRecordings.AnyAsync(p => p.RecordingId == id && p.RoomId == roomId)) return NotFound();
        if (req.SectionId is { } sectionId
            && !await _db.Sections.AnyAsync(s => s.Id == sectionId && s.RoomId == roomId))
            return NotFound();
        if (!await _rooms.SetSectionAsync(roomId, id, req.SectionId)) return NotFound();
        return NoContent();
    }

    /// <summary>Share a recording from one room into another (a non-main placement). Needs <c>ShareOut</c> in the
    /// source room and <c>CreateRecording</c> in the target; the link lands ungrouped for now.</summary>
    [HttpPost("{id:guid}/share")]
    [EndpointSummary("Share a recording into another room")]
    [EndpointDescription(
        "Adds the recording to a second room. This is a **link, not a copy**: one recording, visible in both " +
        "rooms, each with its own folder and position. The original placement is unaffected, and members of the " +
        "target room can read the transcript, notes, and screenshots but only the owner can change them.\n\n" +
        "Needs `ShareOut` in the source room and `CreateRecording` in the target. The share lands ungrouped. " +
        "Personal rooms cannot receive shares.")]
    public async Task<IActionResult> Share(Guid id, ShareRecordingRequest req)
    {
        // The recording must actually be placed in the source room (and the caller a member with ShareOut).
        if (!await _db.RoomRecordings.AnyAsync(p => p.RecordingId == id && p.RoomId == req.FromRoomId))
            return NotFound();
        if (!(await _rooms.PermissionsAsync(UserId, req.FromRoomId)).HasFlag(RoomPermission.ShareOut))
            return StatusCode(StatusCodes.Status403Forbidden, "You can't share recordings out of that room.");
        if (!(await _rooms.PermissionsAsync(UserId, req.ToRoomId)).HasFlag(RoomPermission.CreateRecording))
            return StatusCode(StatusCodes.Status403Forbidden, "You can't add recordings to that room.");

        var ok = await _rooms.ShareIntoRoomAsync(id, req.ToRoomId, UserId, sectionId: null);
        return ok ? NoContent() : BadRequest("That room can't take shared recordings."); // e.g. a personal target
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete a recording")]
    [EndpointDescription(
        "Permanently deletes the recording and everything belonging to it: every transcription version and its " +
        "segments, summaries and minutes, speakers, action items, notes, attachments, and screenshots, along " +
        "with the stored audio, attachment files, and screenshot images. It also disappears from every room it " +
        "was shared into.\n\n" +
        "**There is no undo and no recycle bin.** Only the owner can delete; sharing a recording into a room " +
        "does not let that room's members delete it. Enrolled speaker profiles survive - they are separate " +
        "records. To free the audio bytes while keeping the transcript, delete just the audio instead.")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        // Remove the blobs first: a dangling DB row is safer (and retriable) than an orphaned blob.
        // The DB cascade clears Transcriptions -> Segments + Summary, Speakers, Attachment and
        // MeetingScreenshot rows - but not their object-storage blobs, so the uploaded-attachment files
        // and the screenshot images must be deleted explicitly too.
        await _storage.DeleteAsync(rec.BlobKey);
        foreach (var key in await FileAttachmentKeysAsync(rec.Id))
            await _storage.DeleteAsync(key);
        foreach (var key in await ScreenshotKeysAsync(rec.Id))
            await _storage.DeleteAsync(key);
        _db.Recordings.Remove(rec);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Object-storage keys of a recording's uploaded-file attachments (URL attachments have none).</summary>
    private async Task<List<string>> FileAttachmentKeysAsync(Guid recordingId) =>
        await _db.Attachments
            .Where(a => a.RecordingId == recordingId && a.BlobKey != null)
            .Select(a => a.BlobKey!)
            .ToListAsync();

    /// <summary>Object-storage keys of a recording's screenshots - full image and thumbnail alike.</summary>
    private async Task<List<string>> ScreenshotKeysAsync(Guid recordingId)
    {
        var shots = await _db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .Select(s => new { s.BlobKey, s.ThumbBlobKey })
            .ToListAsync();
        return shots.SelectMany(s => new[] { s.BlobKey, s.ThumbBlobKey }).ToList();
    }

    /// <summary>Delete just the audio blob, keeping the transcript and metadata. Frees the recording's
    /// bytes against the owner's quota (SizeBytes -> 0) and flags <see cref="Recording.AudioDeletedAt"/>.
    /// Idempotent: deleting already-deleted audio is a no-op success.</summary>
    [HttpDelete("{id:guid}/audio")]
    [EndpointSummary("Delete a recording's audio")]
    [EndpointDescription(
        "Deletes the audio blob while keeping the transcript, summary, minutes, actions, and everything else. " +
        "The recording's bytes are released against your storage quota and it is flagged as audio-deleted.\n\n" +
        "You lose playback and the ability to re-transcribe, both of which need the original audio. Idempotent: " +
        "deleting already-deleted audio succeeds. Returns 409 while the recording is protected from audio " +
        "deletion - clear the protection first.")]
    public async Task<IActionResult> DeleteAudio(Guid id)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();
        if (rec.IsAudioProtected)
            return Conflict("This recording's audio is protected from deletion. Remove the protection first.");

        if (rec.HasAudio)
        {
            await _storage.DeleteAsync(rec.BlobKey);
            rec.AudioDeletedAt = DateTimeOffset.UtcNow;
            rec.SizeBytes = 0; // stop counting toward the quota (UsedBytes = SUM(SizeBytes))
            await _db.SaveChangesAsync();
        }
        return NoContent();
    }

    /// <summary>Bulk variant of <see cref="DeleteAudio"/> for the recordings-list "Delete audio" action.
    /// Skips ids that aren't the caller's or already have no audio.</summary>
    [HttpPost("audio/delete")]
    [EndpointSummary("Delete the audio of several recordings")]
    [EndpointDescription(
        "Bulk form of deleting a recording's audio. Transcripts are kept and the freed bytes are returned to " +
        "your quota. Ids that are not yours, already have no audio, or are protected from audio deletion are " +
        "skipped, so a mixed selection still succeeds.")]
    public async Task<IActionResult> DeleteAudioBulk(DeleteAudioRequest req)
    {
        var ids = (req.Ids ?? []).ToList();
        if (ids.Count == 0) return NoContent();

        var recs = await _db.Recordings
            .Where(r => ids.Contains(r.Id) && r.UserId == UserId
                && r.AudioDeletedAt == null && r.AudioProtectedAt == null)
            .ToListAsync();
        foreach (var rec in recs)
        {
            await _storage.DeleteAsync(rec.BlobKey);
            rec.AudioDeletedAt = DateTimeOffset.UtcNow;
            rec.SizeBytes = 0;
        }
        if (recs.Count > 0) await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Protect (or unprotect) a recording's audio from deletion. While protected, both the nightly
    /// auto-retention job and the manual <see cref="DeleteAudio"/> action skip/refuse it.</summary>
    [HttpPut("{id:guid}/audio-protection")]
    [EndpointSummary("Protect a recording's audio from deletion")]
    [EndpointDescription(
        "Marks the audio as protected, or clears that. While protected the recording is skipped by the nightly " +
        "auto-retention job and refused by both the single and bulk audio-delete endpoints. Use it to keep " +
        "important recordings playable and re-transcribable on a platform with a retention policy. It does not " +
        "protect against deleting the whole recording.")]
    public async Task<IActionResult> SetAudioProtection(Guid id, SetAudioProtectionRequest req)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        // Preserve the original protection date on a redundant re-protect; only stamp when toggling on.
        if (req.Protected)
            rec.AudioProtectedAt ??= DateTimeOffset.UtcNow;
        else
            rec.AudioProtectedAt = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Merge 2+ recordings into the earliest-created one: concatenate their transcripts (with
    /// timestamps laid end-to-end) into a new transcription version on the survivor and append the others'
    /// action items. Recordings may have had their audio deleted — those contribute only their transcript
    /// and actions. When at least one source still has audio, an audio-concatenation job stitches the
    /// available audio and the worker callback swaps in the combined blob and deletes the other recordings;
    /// when none has audio the merge finishes synchronously. The summary is not merged (regenerate it).</summary>
    [HttpPost("merge")]
    [EndpointSummary("Merge recordings into one")]
    [EndpointDescription(
        "Combines two or more recordings into the **earliest-created** one, which survives; the others are " +
        "deleted. Their transcripts are concatenated with timestamps laid end to end into a new transcription " +
        "version on the survivor, and their action items are appended. Use it for a meeting captured in several " +
        "parts.\n\n" +
        "Recordings whose audio has been deleted can still be merged - they contribute transcript and actions " +
        "only. If any source still has audio the response is 202 and an audio-concatenation job stitches the " +
        "available audio in the background, swapping in the combined blob and removing the other recordings " +
        "when it finishes; if none has audio the merge completes synchronously.\n\n" +
        "The summary and minutes are **not** merged - regenerate them afterwards.")]
    public async Task<IActionResult> Merge(MergeRecordingsRequest req)
    {
        var ids = (req.Ids ?? []).Distinct().ToList();
        if (ids.Count < 2) return BadRequest("Select at least two recordings to merge.");

        var recs = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions).ThenInclude(t => t.Segments)
            .Where(r => ids.Contains(r.Id) && r.UserId == UserId)
            .ToListAsync();
        if (recs.Count != ids.Count) return NotFound();                 // some aren't the caller's

        // Earliest-created recording survives; the rest are folded into it (chronological order).
        var ordered = recs.OrderBy(r => r.CreatedAt).ToList();
        var survivor = ordered[0];

        var sources = ordered.Select((rec, idx) =>
        {
            var current = rec.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault();
            var speakerByLabel = rec.Speakers.ToDictionary(s => s.Label);
            var segs = (current?.Segments ?? new List<Segment>())
                .OrderBy(s => s.Ordinal)
                .Select(s =>
                {
                    // Carry the source speaker's full identity (name + profile + auto/multi flags) so the
                    // merged speaker stays assigned - not just named - on the Speakers tab.
                    speakerByLabel.TryGetValue(s.SpeakerLabel, out var sp);
                    return new MergeSegmentInput(
                        s.SpeakerLabel,
                        sp?.DisplayName ?? s.SpeakerLabel,
                        s.StartMs, s.EndMs, s.EffectiveText,
                        sp?.ProfileId, sp?.IdentifiedAuto ?? false, sp?.IsMultiSpeaker ?? false);
                })
                .ToList();
            return new MergeSourceInput(idx, rec.DurationMs, segs);
        }).ToList();

        var merged = TranscriptMerger.Merge(sources);

        // A new (highest) transcription version on the survivor holds the concatenated transcript.
        var nextVersion = (survivor.Transcriptions.Max(t => (int?)t.Version) ?? 0) + 1;
        var tr = new Transcription
        {
            Id = Guid.NewGuid(),
            RecordingId = survivor.Id,
            Model = "merged",
            Version = nextVersion,
            Language = survivor.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault()?.Language,
        };
        _db.Transcriptions.Add(tr);
        foreach (var s in merged.Segments)
            _db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = tr.Id,
                SpeakerLabel = s.SpeakerLabel,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Original = s.Text,
                Ordinal = s.Ordinal,
            });

        // Seed Speaker rows on the survivor for the namespaced labels (so the detail view can name them).
        var existing = survivor.Speakers.Select(sp => sp.Label).ToHashSet();
        foreach (var sp in merged.Speakers.Where(sp => !existing.Contains(sp.Label)))
            _db.Speakers.Add(new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = survivor.Id, Label = sp.Label, DisplayName = sp.DisplayName,
                ProfileId = sp.ProfileId, IdentifiedAuto = sp.IdentifiedAuto, IsMultiSpeaker = sp.IsMultiSpeaker,
            });

        survivor.Error = null;

        // Append the other recordings' action items to the survivor (after its own). The summary is left
        // out deliberately — the merged transcript can be re-summarised.
        var nextActionOrdinal = survivor.Actions.Count == 0 ? 0 : survivor.Actions.Max(a => a.Ordinal) + 1;
        var mergedAnyAction = false;
        foreach (var a in ordered.Skip(1).SelectMany(r => r.Actions.OrderBy(x => x.Ordinal)))
        {
            _db.RecordingActions.Add(new RecordingAction
            {
                Id = Guid.NewGuid(), RecordingId = survivor.Id,
                Text = a.Text, Actor = a.Actor, Deadline = a.Deadline, Ordinal = nextActionOrdinal++,
            });
            mergedAnyAction = true;
        }
        if (mergedAnyAction) survivor.ActionsExtractedAt ??= DateTimeOffset.UtcNow;

        // Carry the merged-away recordings' attachments onto the survivor so the user keeps every attached
        // document — and so their blobs stay referenced rather than being orphaned when the source rows are
        // dropped (here in the sync path, or by the worker callback in the async one). Ordinals continue
        // after the survivor's existing attachments.
        var sourceIds = ordered.Skip(1).Select(r => r.Id).ToList();
        var movingAttachments = await _db.Attachments
            .Where(a => sourceIds.Contains(a.RecordingId))
            .ToListAsync();
        if (movingAttachments.Count > 0)
        {
            var nextAttachmentOrdinal = (await _db.Attachments
                .Where(a => a.RecordingId == survivor.Id)
                .Select(a => (int?)a.Ordinal).MaxAsync() ?? -1) + 1;
            // Keep a stable order: each source in merge (chronological) order, its attachments by ordinal.
            foreach (var a in movingAttachments
                .OrderBy(a => sourceIds.IndexOf(a.RecordingId)).ThenBy(a => a.Ordinal))
            {
                a.RecordingId = survivor.Id;
                a.Ordinal = nextAttachmentOrdinal++;
            }
        }

        var deleteIds = sourceIds;
        var audioSources = ordered.Where(r => r.HasAudio).ToList();

        if (audioSources.Count == 0)
        {
            // No audio to stitch — finish synchronously: the merged transcript + actions are already on the
            // survivor; just drop the source recordings (their audio is already gone). Attachments were
            // reassigned onto the survivor above, but screenshots are not - free their blobs first (the DB
            // cascade only clears the MeetingScreenshot rows), same as the async path's worker callback.
            survivor.Status = RecordingStatus.Transcribed;
            foreach (var sourceId in sourceIds)
                foreach (var key in await ScreenshotKeysAsync(sourceId))
                    await _storage.DeleteAsync(key);
            _db.Recordings.RemoveRange(ordered.Skip(1));
            await _db.SaveChangesAsync();
            await _hub.NotifyStatusAsync(UserId, survivor.Id, survivor.Status.ToString());
            return Accepted();
        }

        // At least one source still has audio — concatenate the available audio as before (audio-less
        // sources contribute only transcript/actions). The worker writes the combined audio to a fresh key
        // and the callback swaps it onto the survivor and deletes the other recordings.
        survivor.Status = RecordingStatus.Merging;
        var ext = Path.GetExtension(audioSources[0].BlobKey);
        var outputKey = $"{UserId}/{survivor.Id}-merged-{Guid.NewGuid():N}{(string.IsNullOrEmpty(ext) ? ".webm" : ext)}";
        var blobKeys = audioSources.Select(r => r.BlobKey).ToList();

        await _db.SaveChangesAsync();
        await _queue.EnqueueAudioMergeAsync(new AudioMergeJob(survivor.Id, blobKeys, outputKey, deleteIds));
        await _hub.NotifyStatusAsync(UserId, survivor.Id, survivor.Status.ToString());
        return Accepted();
    }

    /// <summary>Returns a same-origin URL the browser can stream/download the audio from. The audio is
    /// served by the API itself (see <see cref="GetAudio"/>), so MinIO never has to be reachable from the
    /// client. The &lt;audio&gt; element / download link can't send an Authorization header, so the caller's
    /// bearer is carried as <c>access_token</c> (the same approach SignalR uses for its WS handshake).</summary>
    [HttpGet("{id:guid}/audio-url")]
    [EndpointSummary("Get a playable URL for the audio")]
    [EndpointDescription(
        "Returns a same-origin URL an `<audio>` element or a download link can use directly. Those cannot send " +
        "an `Authorization` header, so the URL carries your bearer token as an `access_token` query parameter " +
        "instead - **treat it as a credential**: anyone with the URL can fetch the audio until the token " +
        "expires. The audio is served by the API itself, so the object store never has to be reachable from the " +
        "client. Pass `download=true` for a URL that saves rather than streams.")]
    public async Task<ActionResult<object>> AudioUrl(Guid id, [FromQuery] bool download = false)
    {
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId && r.AudioDeletedAt == null);
        if (!owned) return NotFound();

        var bearer = Request.Headers.Authorization.ToString();
        var token = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? bearer["Bearer ".Length..] : bearer;
        var url = $"/api/recordings/{id}/audio?access_token={Uri.EscapeDataString(token)}" + (download ? "&download=true" : "");
        return new { url };
    }

    /// <summary>Streams the original audio (with HTTP Range support for seeking). Authenticated like any
    /// other endpoint, but also accepts the bearer via <c>access_token</c> (see Program.cs) so the
    /// &lt;audio&gt; element can load it.</summary>
    [HttpGet("{id:guid}/audio")]
    [EndpointSummary("Stream or download the audio")]
    [EndpointDescription(
        "Serves the original audio bytes, honouring HTTP `Range` requests so a player can seek without pulling " +
        "the whole file (206 for a range, 200 for the lot). Pass `download=true` to get it as an attachment.\n\n" +
        "Authenticated like any other endpoint, but the bearer may also be supplied as an `access_token` query " +
        "parameter so an `<audio>` element can load it. Returns 404 once the audio has been deleted.")]
    public async Task<IActionResult> GetAudio(Guid id, [FromQuery] bool download = false, CancellationToken ct = default)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct);
        if (rec is null || !rec.HasAudio) return NotFound();

        var total = rec.SizeBytes > 0 ? rec.SizeBytes : (await _storage.GetSizeAsync(rec.BlobKey, ct) ?? 0);
        Response.Headers.AcceptRanges = "bytes";
        if (download)
            Response.Headers.ContentDisposition =
                $"attachment; filename=\"{Slug(rec.Name ?? rec.Title)}{Path.GetExtension(rec.BlobKey)}\"";

        long start = 0, end = 0;
        var hasRange = total > 0 && TryParseRange(Request.Headers.Range.ToString(), total, out start, out end);
        var blob = hasRange
            ? await _storage.OpenAsync(rec.BlobKey, start, end, ct)
            : await _storage.OpenAsync(rec.BlobKey, ct: ct);
        if (blob is null) return NotFound();

        await using var content = blob.Content;
        Response.ContentType = string.IsNullOrEmpty(rec.ContentType) ? blob.ContentType : rec.ContentType;
        if (hasRange)
        {
            Response.StatusCode = 206; // Partial Content
            Response.Headers.ContentRange = $"bytes {start}-{end}/{total}";
        }
        Response.ContentLength = blob.Length;
        await content.CopyToAsync(Response.Body, ct);
        return new EmptyResult();
    }

    /// <summary>Parse a single byte range ("bytes=start-end", end optional) against the known total.</summary>
    private static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;
        var spec = header["bytes=".Length..].Split(',')[0]; // honour only the first range
        var dash = spec.IndexOf('-');
        if (dash <= 0) return false; // require an explicit start (no suffix-range support)
        if (!long.TryParse(spec[..dash], out start)) return false;
        var endStr = spec[(dash + 1)..];
        if (endStr.Length > 0 && long.TryParse(endStr, out var e)) end = e;
        if (end > total - 1) end = total - 1;
        return start <= end;
    }

    [HttpGet("{id:guid}/transcript.txt")]
    [EndpointSummary("Download the transcript as plain text")]
    [EndpointDescription(
        "The current transcript as a `.txt` download: the recording name, its summary, then the " +
        "speaker-labelled segments. Speaker names and section labels follow your interface language. Reflects " +
        "your segment edits, using the revised text where you have corrected a line.")]
    public Task<IActionResult> TranscriptTxt(Guid id) => RenderTranscriptAsync(id, "txt");

    [HttpGet("{id:guid}/transcript.md")]
    [EndpointSummary("Download the transcript as Markdown")]
    [EndpointDescription(
        "The same content as the plain-text export, formatted as Markdown for pasting into a document or wiki.")]
    public Task<IActionResult> TranscriptMd(Guid id) => RenderTranscriptAsync(id, "md");

    [HttpGet("{id:guid}/transcript.rtf")]
    [EndpointSummary("Download the transcript as RTF")]
    [EndpointDescription(
        "The same content as the plain-text export, as Rich Text for opening in Word or a similar editor with " +
        "its formatting intact.")]
    public Task<IActionResult> TranscriptRtf(Guid id) => RenderTranscriptAsync(id, "rtf");

    [HttpGet("{id:guid}/transcript.srt")]
    [EndpointSummary("Download the transcript as subtitles")]
    [EndpointDescription(
        "The transcript as an SRT subtitle file: numbered cues with start and end timecodes, and **nothing " +
        "else** - no name or summary, unlike the other export formats. Suitable for a video editor or player.")]
    public Task<IActionResult> TranscriptSrt(Guid id) => RenderTranscriptAsync(id, "srt");

    /// <summary>Render the current transcript as a download. txt/md/rtf mirror the emailed layout
    /// (name + summary + transcript); srt is subtitle cues only.</summary>
    private async Task<IActionResult> RenderTranscriptAsync(Guid id, string format)
    {
        var rec = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null || current.Segments.Count == 0) return NotFound();

        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        var segs = current.Segments
            .OrderBy(s => s.Ordinal)
            .Select(s => new SegmentDto(
                s.Id, s.SpeakerLabel,
                names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.Original, s.Revised))
            .ToList();

        var name = rec.Name ?? rec.Title;
        var summary = current.Summary?.Text;
        var minutes = current.MeetingMinutes?.Text;
        var actions = rec.Actions
            .OrderBy(a => a.Ordinal)
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal))
            .ToList();

        var labels = await ExportLabelsAsync();
        var (body, mime, ext) = format switch
        {
            "md" => (TranscriptFormatter.ToMarkdown(name, summary, segs, actions, labels, minutes), "text/markdown", "md"),
            "rtf" => (TranscriptFormatter.ToRtf(name, summary, segs, actions, labels, minutes), "application/rtf", "rtf"),
            "srt" => (TranscriptFormatter.ToSrt(segs), "application/x-subrip", "srt"),
            _ => (TranscriptFormatter.ToText(name, summary, segs, actions, labels, minutes), "text/plain", "txt"),
        };
        return File(Encoding.UTF8.GetBytes(body), mime, $"{Slug(name)}.{ext}");
    }

    /// <summary>Filesystem-safe lowercase slug for download filenames.</summary>
    private static string Slug(string? s)
    {
        var slug = new string((s ?? "").Trim()
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "transcript" : slug;
    }

    private async Task EnqueueTranscriptionAsync(Recording rec, string? model = null)
    {
        var nextVersion = await _db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .Select(t => (int?)t.Version).MaxAsync() ?? 0;

        var transcription = new Transcription
        {
            Id = Guid.NewGuid(),
            RecordingId = rec.Id,
            Model = model ?? _defaultModel,
            Version = nextVersion + 1
        };
        _db.Transcriptions.Add(transcription);

        rec.Status = RecordingStatus.Queued;
        await _queue.EnqueueAsync(new TranscriptionJob(rec.Id, transcription.Id, rec.BlobKey, transcription.Model,
            rec.MinSpeakers, rec.MaxSpeakers));
        await _hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());
    }
}
