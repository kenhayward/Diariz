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

    /// <summary>The section id plus its child section ids - the set a recording's <c>SectionId</c> must be in
    /// to count as "included" in this folder.</summary>
    private async Task<List<Guid>> IncludedSectionIdsAsync(Guid sectionId)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var ids = await _db.Sections
            .Where(s => s.RoomId == roomId && s.ParentId == sectionId).Select(s => s.Id).ToListAsync();
        ids.Add(sectionId);
        return ids;
    }

    // ---- Detail (stats + folder summary + folder minutes) ----

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SectionDetailDto>> Get(Guid id)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var section = await _db.Sections
            .Include(s => s.Summary)
            .Include(s => s.Minutes)
            .FirstOrDefaultAsync(s => s.Id == id && s.RoomId == roomId);
        if (section is null) return NotFound();

        var allIds = await IncludedSectionIdsAsync(id);
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

        return new SectionDetailDto(section.Id, section.Name, section.ParentId, stats, summary, minutes,
            section.Minutes?.MeetingTypeId);
    }

    // ---- Aggregations (read-only; edit/delete reuse the per-item controllers) ----

    [HttpGet("{id:guid}/actions")]
    public async Task<ActionResult<IReadOnlyList<ActionListItemDto>>> Actions(Guid id)
    {
        if (!await OwnsSectionAsync(id)) return NotFound();
        var allIds = await IncludedSectionIdsAsync(id);
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        return await (
            from a in _db.RecordingActions
            join r in _db.Recordings on a.RecordingId equals r.Id
            join p in _db.RoomRecordings on r.Id equals p.RecordingId
            where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
            orderby r.CreatedAt descending, a.Ordinal
            select new ActionListItemDto(
                a.Id, a.RecordingId, r.Name ?? r.Title, a.Text, a.Actor, a.Deadline,
                a.Ordinal, a.Completed, a.CompletedAt, a.CreatedAt)).ToListAsync();
    }

    [HttpGet("{id:guid}/notes")]
    public async Task<ActionResult<IReadOnlyList<SectionNoteListItemDto>>> Notes(Guid id)
    {
        if (!await OwnsSectionAsync(id)) return NotFound();
        var allIds = await IncludedSectionIdsAsync(id);
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        return await (
            from n in _db.MeetingNotes
            join r in _db.Recordings on n.RecordingId equals r.Id
            join p in _db.RoomRecordings on r.Id equals p.RecordingId
            where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
            orderby r.CreatedAt descending, n.Ordinal
            select new SectionNoteListItemDto(
                n.Id, r.Id, r.Name ?? r.Title, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt)).ToListAsync();
    }

    [HttpGet("{id:guid}/attachments")]
    public async Task<ActionResult<IReadOnlyList<SectionAttachmentListItemDto>>> Attachments(Guid id)
    {
        if (!await OwnsSectionAsync(id)) return NotFound();
        var allIds = await IncludedSectionIdsAsync(id);
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        return await (
            from a in _db.Attachments
            join r in _db.Recordings on a.RecordingId equals r.Id
            join p in _db.RoomRecordings on r.Id equals p.RecordingId
            where p.RoomId == roomId && p.SectionId.HasValue && allIds.Contains(p.SectionId.Value)
            orderby r.CreatedAt descending, a.Ordinal
            select new SectionAttachmentListItemDto(
                a.Id, r.Id, r.Name ?? r.Title, a.Kind, a.Name, a.ContentType, a.SizeBytes, a.Url, a.Ordinal))
            .ToListAsync();
    }

    // ---- Folder summary: generate (async) + edit ----

    [HttpPost("{id:guid}/summary/generate")]
    public async Task<IActionResult> GenerateSummary(Guid id)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var section = await _db.Sections.Include(s => s.Summary)
            .FirstOrDefaultAsync(s => s.Id == id && s.RoomId == roomId);
        if (section is null) return NotFound();

        var cfg = await _summarization.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Summarisation is not configured. Set an LLM endpoint in Settings.");

        var summary = await UpsertSummaryAsync(section);
        if (summary.Status == SectionGenerationStatus.Generating) return Accepted(); // idempotent

        summary.IsUserEdited = false; // an explicit regenerate overwrites a hand-edited summary
        summary.Status = SectionGenerationStatus.Generating;
        summary.Error = null;
        await _queue.EnqueueSectionSummaryAsync(new SectionSummaryJob(section.Id));
        await _hub.NotifySectionStatusAsync(UserId, section.Id, "summary", "Generating");
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpPut("{id:guid}/summary")]
    public async Task<IActionResult> UpdateSummary(Guid id, UpdateSummaryRequest req)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var section = await _db.Sections.Include(s => s.Summary)
            .FirstOrDefaultAsync(s => s.Id == id && s.RoomId == roomId);
        if (section is null) return NotFound();

        var summary = await UpsertSummaryAsync(section);
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
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var section = await _db.Sections.Include(s => s.Minutes)
            .FirstOrDefaultAsync(s => s.Id == id && s.RoomId == roomId);
        if (section is null) return NotFound();

        // A chosen type must be the caller's own Personal type or a shared Platform type.
        if (req.MeetingTypeId is { } typeId &&
            !await _db.MeetingTypes.AnyAsync(t => t.Id == typeId && (t.RoomId == null || t.RoomId == roomId)))
            return NotFound();

        var cfg = await _summarization.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Summarisation is not configured. Set an LLM endpoint in Settings.");

        var minutes = await UpsertMinutesAsync(section);
        minutes.MeetingTypeId = req.MeetingTypeId;
        if (minutes.Status == SectionGenerationStatus.Generating) return Accepted();

        minutes.IsUserEdited = false;
        minutes.Status = SectionGenerationStatus.Generating;
        minutes.Error = null;
        await _queue.EnqueueSectionMinutesAsync(new SectionMinutesJob(section.Id));
        await _hub.NotifySectionStatusAsync(UserId, section.Id, "minutes", "Generating");
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpPut("{id:guid}/minutes")]
    public async Task<IActionResult> UpdateMinutes(Guid id, UpdateMeetingMinutesRequest req)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var section = await _db.Sections.Include(s => s.Minutes)
            .FirstOrDefaultAsync(s => s.Id == id && s.RoomId == roomId);
        if (section is null) return NotFound();

        var minutes = await UpsertMinutesAsync(section);
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

    private async Task<bool> OwnsSectionAsync(Guid id)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        return await _db.Sections.AnyAsync(s => s.Id == id && s.RoomId == roomId);
    }

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
