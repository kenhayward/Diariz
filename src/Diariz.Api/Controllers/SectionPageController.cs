using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The folder (section) detail page: aggregated stats, an LLM folder summary + folder minutes (both
/// generated async), and read aggregations of the actions/notes/attachments across the section and its child
/// sections. CRUD/reorder for sections stays in <see cref="SectionsController"/>; the per-item action/note/
/// attachment edit+delete reuse their existing controllers. Ownership is transitive via <c>Recording.UserId</c>,
/// expressed as explicit joins so queries work on both Npgsql and the in-memory test provider.</summary>
[ApiController]
[Authorize]
[Route("api/sections")]
public class SectionPageController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IJobQueue _queue;
    private readonly ISummarizationSettingsResolver _summarization;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly IRoomScope _rooms;

    public SectionPageController(
        DiarizDbContext db, IJobQueue queue, ISummarizationSettingsResolver summarization,
        IHubContext<TranscriptionHub> hub, IRoomScope rooms)
    {
        _db = db;
        _queue = queue;
        _summarization = summarization;
        _hub = hub;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>The section id plus its child section ids (within the same room) - the set a recording's
    /// placement <c>SectionId</c> must be in to count as "included" in this folder.</summary>
    private async Task<List<Guid>> IncludedSectionIdsAsync(Guid sectionId, Guid roomId)
    {
        var ids = await _db.Sections
            .Where(s => s.RoomId == roomId && s.ParentId == sectionId).Select(s => s.Id).ToListAsync();
        ids.Add(sectionId);
        return ids;
    }

    /// <summary>Maps the shared <see cref="RoomAccessError"/> from <see cref="IRoomScope.ManageableSectionAsync"/>
    /// to this controller's status codes: 404 for a non-member/missing section (room existence stays private),
    /// 403 for a member lacking the permission. Fixes issue #289: folders in a shared room used to be hardcoded
    /// to the caller's personal room and so 404'd, leaving the page stuck on "Loading ...".</summary>
    private ActionResult? ToActionResult(RoomAccessError? error) => error switch
    {
        RoomAccessError.NotFound => NotFound(),
        RoomAccessError.Forbidden => Forbid(),
        _ => null,
    };

    // ---- Detail (stats + folder summary + folder minutes) ----

    [HttpGet("{id:guid}")]
    [EndpointSummary("Get a folder page")]
    [EndpointDescription(
        "A folder's roll-up: how many recordings it holds, their total duration and date range, plus its LLM " +
        "folder summary and folder minutes if they exist. Everything counts the folder **and its " +
        "sub-folders**, so a top-level folder reports the whole branch.\n\n" +
        "The summary and minutes each carry a `status` (`Generating`, `Ready` or `Failed`) and an `error`, " +
        "because they are produced in the background - poll here after asking for one. Readable by any member " +
        "of the folder's room.")]
    public async Task<ActionResult<SectionDetailDto>> Get(Guid id)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, id, withArtifacts: true);
        if (section is null) return NotFound();
        var roomId = section.RoomId;

        var allIds = await IncludedSectionIdsAsync(id, roomId);
        // Recordings filed under this folder (or its children) now come from the placement in the caller's
        // personal room, not from Recording.SectionId.
        var recs = from p in _db.RoomRecordings
                   where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
                   join r in _db.Recordings on p.RecordingId equals r.Id
                   select r;

        var stats = new SectionStatsDto(
            await recs.CountAsync(),
            await recs.SumAsync(r => r.DurationMs),
            await recs.MinAsync(r => (DateTimeOffset?)r.CreatedAt),
            await recs.MaxAsync(r => (DateTimeOffset?)r.CreatedAt));

        var summary = section.Summary is { } s
            ? new FolderSummaryDto(s.Model, s.Text, s.CreatedAt, s.IsUserEdited, s.Status, s.Error)
            : null;
        var minutes = section.Minutes is { } m
            ? new FolderMinutesDto(m.Model, m.Text, m.CreatedAt, m.IsUserEdited, m.MeetingTypeId, m.Status, m.Error)
            : null;

        return new SectionDetailDto(section.Id, section.Name, section.ParentId, section.RoomId, stats, summary,
            minutes, section.Minutes?.MeetingTypeId);
    }

    // ---- Aggregations (read-only; edit/delete reuse the per-item controllers) ----

    [HttpGet("{id:guid}/actions")]
    [EndpointSummary("List the action items across a folder")]
    [EndpointDescription(
        "Every action item from every recording in the folder **and its sub-folders**, newest meeting first, " +
        "each carrying the recording it came from so you can link back. Read-only aggregation - create, edit, " +
        "and complete them through the per-recording and Actions endpoints.")]
    public async Task<ActionResult<IReadOnlyList<ActionListItemDto>>> Actions(Guid id)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, id);
        if (section is null) return NotFound();
        var roomId = section.RoomId;
        var allIds = await IncludedSectionIdsAsync(id, roomId);
        return await (
            from a in _db.RecordingActions
            join r in _db.Recordings on a.RecordingId equals r.Id
            join p in _db.RoomRecordings on r.Id equals p.RecordingId
            where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
            orderby r.CreatedAt descending, a.Ordinal
            select new ActionListItemDto(
                a.Id, a.RecordingId, r.Name ?? r.Title, a.Text, a.Actor, a.Deadline,
                a.Ordinal, a.Completed, a.CompletedAt, a.CreatedAt, r.UserId)).ToListAsync();
    }

    [HttpGet("{id:guid}/notes")]
    [EndpointSummary("List the notes across a folder")]
    [EndpointDescription(
        "Every note line from every recording in the folder and its sub-folders, newest meeting first, each " +
        "carrying its recording and the moment it was captured. Read-only aggregation - write through the " +
        "per-recording notes endpoints.")]
    public async Task<ActionResult<IReadOnlyList<SectionNoteListItemDto>>> Notes(Guid id)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, id);
        if (section is null) return NotFound();
        var roomId = section.RoomId;
        var allIds = await IncludedSectionIdsAsync(id, roomId);
        return await (
            from n in _db.MeetingNotes
            join r in _db.Recordings on n.RecordingId equals r.Id
            join p in _db.RoomRecordings on r.Id equals p.RecordingId
            where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
            orderby r.CreatedAt descending, n.Ordinal
            select new SectionNoteListItemDto(
                n.Id, r.Id, r.Name ?? r.Title, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt, r.UserId))
            .ToListAsync();
    }

    [HttpGet("{id:guid}/attachments")]
    [EndpointSummary("List the recording attachments across a folder")]
    [EndpointDescription(
        "Every attachment belonging to a **recording** in the folder and its sub-folders, each carrying the " +
        "recording it hangs off. This is not the same as the folder's own attachments, which are filed " +
        "directly against the folder and live under the separate folder-attachments endpoints. Read-only " +
        "aggregation.")]
    public async Task<ActionResult<IReadOnlyList<SectionAttachmentListItemDto>>> Attachments(Guid id)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, id);
        if (section is null) return NotFound();
        var roomId = section.RoomId;
        var allIds = await IncludedSectionIdsAsync(id, roomId);
        return await (
            from a in _db.Attachments
            join r in _db.Recordings on a.RecordingId equals r.Id
            join p in _db.RoomRecordings on r.Id equals p.RecordingId
            where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
            orderby r.CreatedAt descending, a.Ordinal
            select new SectionAttachmentListItemDto(
                a.Id, r.Id, r.Name ?? r.Title, a.Kind, a.Name, a.ContentType, a.SizeBytes, a.Url, a.Ordinal,
                r.UserId))
            .ToListAsync();
    }

    // ---- Folder summary: generate (async) + edit ----

    [HttpPost("{id:guid}/summary/generate")]
    [EndpointSummary("Generate a folder summary")]
    [EndpointDescription(
        "Queues an LLM summary across every recording in the folder and its sub-folders, returning 202 " +
        "immediately. Watch the folder page's summary `status` for `Ready` or `Failed`; asking again while one " +
        "is already generating is a no-op rather than a second job.\n\n" +
        "An explicit generate **overwrites a summary you had written by hand**. Needs `ManageContents` in the " +
        "folder's room (403 for a member without it), and 400 when no LLM endpoint is configured.")]
    public async Task<IActionResult> GenerateSummary(Guid id)
    {
        var (section, error) = await _rooms.ManageableSectionAsync(UserId, id, withArtifacts: true);
        if (ToActionResult(error) is { } errorResult) return errorResult;
        var sec = section!;

        var cfg = await _summarization.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Summarisation is not configured. Set an LLM endpoint in Settings.");

        var summary = await UpsertSummaryAsync(sec);
        if (summary.Status == SectionGenerationStatus.Generating) return Accepted(); // idempotent

        summary.IsUserEdited = false; // an explicit regenerate overwrites a hand-edited summary
        summary.Status = SectionGenerationStatus.Generating;
        summary.Error = null;
        await _queue.EnqueueSectionSummaryAsync(new SectionSummaryJob(sec.Id));
        await _hub.NotifySectionStatusAsync(UserId, sec.Id, "summary", "Generating");
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpPut("{id:guid}/summary")]
    [EndpointSummary("Write a folder summary by hand")]
    [EndpointDescription(
        "Sets the folder's summary to your own text, creating it if the folder has none. Works with no LLM " +
        "configured, and marks the summary user-edited so it survives - though an explicit generate still " +
        "replaces it. The status becomes `Ready` and any previous generation error is cleared, so this is also " +
        "the way to get a folder out of a `Failed` state. Needs `ManageContents`.")]
    public async Task<IActionResult> UpdateSummary(Guid id, UpdateSummaryRequest req)
    {
        var (section, error) = await _rooms.ManageableSectionAsync(UserId, id, withArtifacts: true);
        if (ToActionResult(error) is { } errorResult) return errorResult;

        var summary = await UpsertSummaryAsync(section!);
        summary.Text = req.Text ?? string.Empty;
        summary.Model = SectionSummary.UserEditedModel;
        summary.IsUserEdited = true;
        summary.UpdatedAt = DateTimeOffset.UtcNow;
        summary.Status = SectionGenerationStatus.Ready;
        summary.Error = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---- Folder minutes: generate (async, with template) + edit ----

    [HttpPost("{id:guid}/minutes/generate")]
    [EndpointSummary("Generate folder minutes")]
    [EndpointDescription(
        "Queues consolidated minutes across the folder and its sub-folders, returning 202. Pass a " +
        "`meetingTypeId` to choose the template, or null for the General default; the type must be a General " +
        "one or belong to this folder's room, otherwise 404. The chosen type is remembered on the folder even " +
        "when a generation is already running.\n\n" +
        "As with the summary, an explicit generate **overwrites hand-edited minutes**, a second call while one " +
        "is generating is a no-op, and 400 comes back when no LLM endpoint is configured. Needs " +
        "`ManageContents`.")]
    public async Task<IActionResult> GenerateMinutes(Guid id, ApplyMeetingTypeRequest req)
    {
        var (section, error) = await _rooms.ManageableSectionAsync(UserId, id, withArtifacts: true);
        if (ToActionResult(error) is { } errorResult) return errorResult;
        var sec = section!;

        // A chosen type must be a General type (null room) or a type in this section's room.
        if (req.MeetingTypeId is { } typeId &&
            !await _db.MeetingTypes.AnyAsync(t => t.Id == typeId && (t.RoomId == null || t.RoomId == sec.RoomId)))
            return NotFound();

        var cfg = await _summarization.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Summarisation is not configured. Set an LLM endpoint in Settings.");

        var minutes = await UpsertMinutesAsync(sec);
        minutes.MeetingTypeId = req.MeetingTypeId;
        if (minutes.Status == SectionGenerationStatus.Generating) return Accepted();

        minutes.IsUserEdited = false;
        minutes.Status = SectionGenerationStatus.Generating;
        minutes.Error = null;
        await _queue.EnqueueSectionMinutesAsync(new SectionMinutesJob(sec.Id));
        await _hub.NotifySectionStatusAsync(UserId, sec.Id, "minutes", "Generating");
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpPut("{id:guid}/minutes")]
    [EndpointSummary("Write folder minutes by hand")]
    [EndpointDescription(
        "Sets the folder's minutes to your own Markdown, creating them if there are none. Works with no LLM " +
        "configured, marks them user-edited, and clears any generation error - so it also rescues a folder " +
        "stuck in `Failed`. The remembered meeting type is left as it is. Needs `ManageContents`.")]
    public async Task<IActionResult> UpdateMinutes(Guid id, UpdateMeetingMinutesRequest req)
    {
        var (section, error) = await _rooms.ManageableSectionAsync(UserId, id, withArtifacts: true);
        if (ToActionResult(error) is { } errorResult) return errorResult;

        var minutes = await UpsertMinutesAsync(section!);
        minutes.Text = req.Text ?? string.Empty;
        minutes.Model = SectionMinutes.UserEditedModel;
        minutes.IsUserEdited = true;
        minutes.UpdatedAt = DateTimeOffset.UtcNow;
        minutes.Status = SectionGenerationStatus.Ready;
        minutes.Error = null;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ---- helpers ----

    private async Task<SectionSummary> UpsertSummaryAsync(Section section)
    {
        var summary = section.Summary;
        if (summary is null)
        {
            summary = new SectionSummary { Id = Guid.NewGuid(), SectionId = section.Id };
            _db.SectionSummaries.Add(summary);
            section.Summary = summary;
        }
        return await Task.FromResult(summary);
    }

    private async Task<SectionMinutes> UpsertMinutesAsync(Section section)
    {
        var minutes = section.Minutes;
        if (minutes is null)
        {
            minutes = new SectionMinutes { Id = Guid.NewGuid(), SectionId = section.Id };
            _db.SectionMinutes.Add(minutes);
            section.Minutes = minutes;
        }
        return await Task.FromResult(minutes);
    }
}
