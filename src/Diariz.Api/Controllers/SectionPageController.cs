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

    /// <summary>Load a section by id and check the caller may VIEW it - i.e. is a member of the section's own
    /// room (their personal room for a personal folder, or a shared room they belong to). Returns null when the
    /// section doesn't exist OR the caller isn't a member, so callers 404 either way and a room's contents stay
    /// private. Fixes issue #289: folders in a shared room were hardcoded to the caller's personal room and so
    /// 404'd, leaving the page stuck on "Loading ...".</summary>
    private async Task<Section?> ViewableSectionAsync(Guid id, bool withArtifacts = false)
    {
        IQueryable<Section> q = _db.Sections;
        if (withArtifacts) q = q.Include(s => s.Summary).Include(s => s.Minutes);
        var section = await q.FirstOrDefaultAsync(s => s.Id == id);
        if (section is null || !await _rooms.IsMemberAsync(UserId, section.RoomId)) return null;
        return section;
    }

    /// <summary>As <see cref="ViewableSectionAsync"/> but additionally requires ManageContents in the section's
    /// room (the personal-room owner holds every permission). Returns the section (with artifacts included), or
    /// an error result: 404 for a non-member/missing section, 403 for a member lacking the permission.</summary>
    private async Task<(Section? Section, ActionResult? Error)> ManageableSectionAsync(Guid id)
    {
        var section = await ViewableSectionAsync(id, withArtifacts: true);
        if (section is null) return (null, NotFound());
        if (!(await _rooms.PermissionsAsync(UserId, section.RoomId)).HasFlag(RoomPermission.ManageContents))
            return (null, Forbid());
        return (section, null);
    }

    // ---- Detail (stats + folder summary + folder minutes) ----

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SectionDetailDto>> Get(Guid id)
    {
        var section = await ViewableSectionAsync(id, withArtifacts: true);
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
    public async Task<ActionResult<IReadOnlyList<ActionListItemDto>>> Actions(Guid id)
    {
        var section = await ViewableSectionAsync(id);
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
    public async Task<ActionResult<IReadOnlyList<SectionNoteListItemDto>>> Notes(Guid id)
    {
        var section = await ViewableSectionAsync(id);
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
    public async Task<ActionResult<IReadOnlyList<SectionAttachmentListItemDto>>> Attachments(Guid id)
    {
        var section = await ViewableSectionAsync(id);
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
    public async Task<IActionResult> GenerateSummary(Guid id)
    {
        var (section, error) = await ManageableSectionAsync(id);
        if (error is not null) return error;
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
    public async Task<IActionResult> UpdateSummary(Guid id, UpdateSummaryRequest req)
    {
        var (section, error) = await ManageableSectionAsync(id);
        if (error is not null) return error;

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
    public async Task<IActionResult> GenerateMinutes(Guid id, ApplyMeetingTypeRequest req)
    {
        var (section, error) = await ManageableSectionAsync(id);
        if (error is not null) return error;
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
    public async Task<IActionResult> UpdateMinutes(Guid id, UpdateMeetingMinutesRequest req)
    {
        var (section, error) = await ManageableSectionAsync(id);
        if (error is not null) return error;

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
