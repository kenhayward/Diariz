using System.Security.Claims;
using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
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
    private readonly string _defaultModel;

    public RecordingsController(
        DiarizDbContext db, IAudioStorage storage, IJobQueue queue,
        IHubContext<TranscriptionHub> hub, IConfiguration config,
        ISummarizationSettingsResolver summarization, IEmailSender email, ISpeakerIdentifier identifier,
        IOptions<UploadOptions> uploads)
    {
        _db = db;
        _storage = storage;
        _queue = queue;
        _hub = hub;
        _summarization = summarization;
        _email = email;
        _identifier = identifier;
        _uploads = uploads.Value;
        _defaultModel = config["Transcription:DefaultModel"] ?? "whisperx-large-v3";
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IReadOnlyList<RecordingSummaryDto>> List() =>
        await _db.Recordings
            .Where(r => r.UserId == UserId)
            .OrderBy(r => r.Position)
            .ThenByDescending(r => r.CreatedAt)
            .Select(r => new RecordingSummaryDto(r.Id, r.Title, r.Name, r.Source, r.DurationMs, r.Status, r.CreatedAt,
                r.SectionId, r.Section != null ? r.Section.Name : null, r.Actions.Any()))
            .ToListAsync();

    /// <summary>Drag-and-drop: set the section and 0-based position of each listed recording in one
    /// call (used for both within-group sequencing and cross-group moves).</summary>
    [HttpPut("reorder")]
    public async Task<IActionResult> Reorder(ReorderRecordingsRequest req)
    {
        var ids = (req.OrderedIds ?? []).ToList();
        if (ids.Count == 0) return NoContent();

        if (req.SectionId is { } sectionId &&
            !await _db.Sections.AnyAsync(s => s.Id == sectionId && s.UserId == UserId))
            return NotFound(); // can't move into a section the caller doesn't own

        var recs = await _db.Recordings
            .Where(r => ids.Contains(r.Id) && r.UserId == UserId)
            .ToListAsync();
        if (recs.Count != ids.Count) return NotFound(); // a listed recording isn't the caller's

        var byId = recs.ToDictionary(r => r.Id);
        for (var i = 0; i < ids.Count; i++)
        {
            var rec = byId[ids[i]];
            rec.SectionId = req.SectionId;
            rec.Position = i;
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RecordingDetailDto>> Get(Guid id)
    {
        var rec = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);

        if (rec is null) return NotFound();

        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        var actions = rec.Actions
            .OrderBy(a => a.Ordinal)
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal))
            .ToList();
        var speakers = rec.Speakers
            .OrderBy(s => s.Label)
            .Select(s => new SpeakerInfoDto(s.Label, s.DisplayName, s.ProfileId, s.IdentifiedAuto))
            .ToList();
        var current = rec.Transcriptions.FirstOrDefault();
        TranscriptionDto? tDto = current is null ? null : new(
            current.Id, current.Model, current.Version, current.Language, current.CreatedAt,
            current.Segments.Select(s => new SegmentDto(
                s.Id,
                s.SpeakerLabel,
                names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.Text)).ToList());
        SummaryDto? sDto = current?.Summary is null ? null
            : new(current.Summary.Model, current.Summary.Text, current.Summary.CreatedAt);

        return new RecordingDetailDto(rec.Id, rec.Title, rec.Name, rec.Source, rec.DurationMs, rec.SizeBytes,
            rec.Status, rec.Error, rec.CreatedAt, rec.MinSpeakers, rec.MaxSpeakers, names, speakers, tDto, sDto,
            actions, rec.ActionsExtractedAt != null);
    }

    /// <summary>Upload an audio file and kick off transcription.</summary>
    [HttpPost]
    [RequestSizeLimit(1024L * 1024 * 1024)] // 1 GiB
    public async Task<ActionResult<RecordingSummaryDto>> Upload(
        [FromForm] IFormFile audio, [FromForm] string? title, [FromForm] long durationMs,
        [FromForm] RecordingSource source = RecordingSource.Microphone)
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

        return CreatedAtAction(nameof(Get), new { id = rec.Id },
            new RecordingSummaryDto(rec.Id, rec.Title, rec.Name, rec.Source, rec.DurationMs, rec.Status, rec.CreatedAt,
                rec.SectionId, null, false));
    }

    [HttpPost("{id:guid}/retranscribe")]
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
        // A free-text rename detaches the speaker from any voiceprint (it's now hand-typed text).
        speaker.DisplayName = req.DisplayName;
        speaker.ProfileId = null;
        speaker.IdentifiedAuto = false;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Reassign a recording's speaker to an enrolled voiceprint (or unassign with profileId=null).
    /// Assigning adds the speaker as a training contribution and recomputes the profile centroid.</summary>
    [HttpPut("{id:guid}/speakers/{label}/assign")]
    public async Task<IActionResult> AssignSpeaker(Guid id, string label, AssignSpeakerRequest req)
    {
        var rec = await _db.Recordings.Include(r => r.Speakers)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var speaker = rec.Speakers.FirstOrDefault(s => s.Label == label);
        if (speaker is null) return NotFound();

        if (req.ProfileId is null)
        {
            // Unassign → revert to the anonymous label.
            speaker.ProfileId = null;
            speaker.DisplayName = speaker.Label;
            speaker.IdentifiedAuto = false;
            await _db.SaveChangesAsync();
            return NoContent();
        }

        var profile = await _db.SpeakerProfiles
            .FirstOrDefaultAsync(p => p.Id == req.ProfileId && p.UserId == UserId);
        if (profile is null) return NotFound();

        speaker.ProfileId = profile.Id;
        speaker.DisplayName = profile.Name;
        speaker.IdentifiedAuto = false; // an explicit manual assignment

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

        var merged = SegmentMerger.Merge(segments
            .Select(s => new SegmentMerger.Part(KeyFor(s.SpeakerLabel), s.SpeakerLabel, s.StartMs, s.EndMs, s.Text)).ToList());
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
                Text = p.Text,
                Ordinal = ordinal++
            });
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Re-run speaker identification against the owner's current voiceprints, using the speakers'
    /// already-stored embeddings (no re-transcription). Anonymous/auto speakers are (re)labelled; manual
    /// names are left alone.</summary>
    [HttpPost("{id:guid}/reidentify")]
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
    public async Task<IActionResult> EmailTranscript(Guid id)
    {
        var rec = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary)
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
                s.StartMs, s.EndMs, s.Text))
            .ToList();
        var actions = rec.Actions
            .OrderBy(a => a.Ordinal)
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal))
            .ToList();

        var name = rec.Name ?? rec.Title;
        var html = TranscriptEmail.BuildHtml(name, current.Summary?.Text, segs, actions);
        var sent = await _email.SendAsync(address!, TranscriptEmail.Subject(name), html);
        if (!sent) return BadRequest("Email isn't configured on the server. Contact an administrator.");
        return Ok();
    }

    [HttpPut("{id:guid}/segments/{segmentId:guid}")]
    public async Task<IActionResult> UpdateSegment(Guid id, Guid segmentId, UpdateSegmentRequest req)
    {
        var seg = await _db.Segments.Include(s => s.Transcription)
            .FirstOrDefaultAsync(s => s.Id == segmentId);
        if (seg?.Transcription is null || seg.Transcription.RecordingId != id) return NotFound();

        // Ownership: the segment's recording must belong to the caller.
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
        if (!owned) return NotFound();

        seg.Text = req.Text ?? "";
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/summarize")]
    public async Task<IActionResult> Summarize(Guid id)
    {
        var rec = await _db.Recordings
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null) return NotFound();

        var cfg = await _summarization.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Summarisation is not configured. Set an LLM endpoint in Settings.");

        // Idempotent: a summary is already in flight, don't enqueue a second job.
        if (rec.Status == RecordingStatus.Summarizing) return Accepted();

        rec.Status = RecordingStatus.Summarizing;
        await _queue.EnqueueSummarizationAsync(new SummarizationJob(rec.Id, current.Id));
        await _hub.NotifyStatusAsync(rec.UserId, rec.Id, rec.Status.ToString());
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpPut("{id:guid}/name")]
    public async Task<IActionResult> Rename(Guid id, RenameRecordingRequest req)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        rec.Name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim();
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:guid}/section")]
    public async Task<IActionResult> MoveToSection(Guid id, MoveRecordingRequest req)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        if (req.SectionId is { } sectionId)
        {
            // Only allow moving into a section the caller owns.
            var owned = await _db.Sections.AnyAsync(s => s.Id == sectionId && s.UserId == UserId);
            if (!owned) return NotFound();
            rec.SectionId = sectionId;
        }
        else
        {
            rec.SectionId = null; // ungroup
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (rec is null) return NotFound();

        // Remove the blob first: a dangling DB row is safer (and retriable) than an orphaned blob.
        // The DB cascade clears Transcriptions -> Segments + Summary, and Speakers.
        await _storage.DeleteAsync(rec.BlobKey);
        _db.Recordings.Remove(rec);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Returns a same-origin URL the browser can stream/download the audio from. The audio is
    /// served by the API itself (see <see cref="GetAudio"/>), so MinIO never has to be reachable from the
    /// client. The &lt;audio&gt; element / download link can't send an Authorization header, so the caller's
    /// bearer is carried as <c>access_token</c> (the same approach SignalR uses for its WS handshake).</summary>
    [HttpGet("{id:guid}/audio-url")]
    public async Task<ActionResult<object>> AudioUrl(Guid id, [FromQuery] bool download = false)
    {
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
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
    public async Task<IActionResult> GetAudio(Guid id, [FromQuery] bool download = false, CancellationToken ct = default)
    {
        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct);
        if (rec is null) return NotFound();

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
    public Task<IActionResult> TranscriptTxt(Guid id) => RenderTranscriptAsync(id, "txt");

    [HttpGet("{id:guid}/transcript.md")]
    public Task<IActionResult> TranscriptMd(Guid id) => RenderTranscriptAsync(id, "md");

    [HttpGet("{id:guid}/transcript.rtf")]
    public Task<IActionResult> TranscriptRtf(Guid id) => RenderTranscriptAsync(id, "rtf");

    [HttpGet("{id:guid}/transcript.srt")]
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
                s.StartMs, s.EndMs, s.Text))
            .ToList();

        var name = rec.Name ?? rec.Title;
        var summary = current.Summary?.Text;
        var actions = rec.Actions
            .OrderBy(a => a.Ordinal)
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal))
            .ToList();

        var (body, mime, ext) = format switch
        {
            "md" => (TranscriptFormatter.ToMarkdown(name, summary, segs, actions), "text/markdown", "md"),
            "rtf" => (TranscriptFormatter.ToRtf(name, summary, segs, actions), "application/rtf", "rtf"),
            "srt" => (TranscriptFormatter.ToSrt(segs), "application/x-subrip", "srt"),
            _ => (TranscriptFormatter.ToText(name, summary, segs, actions), "text/plain", "txt"),
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
