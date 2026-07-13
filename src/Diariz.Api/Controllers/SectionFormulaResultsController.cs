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

/// <summary>Folder (section) formula results: the action that runs a Formula over a whole folder (async,
/// map-reduce over its meetings) plus - in a later unit - listing/reading/editing/deleting the results. The
/// section-scoped twin of the recording formula surface in <see cref="FormulasController"/>. Every action is
/// gated by folder membership (a caller who is not a member of the section's room 404s, so a room's contents
/// stay private) - mirroring <c>SectionPageController.ViewableSectionAsync</c>.</summary>
[ApiController]
[Authorize]
[Route("api/sections")]
public class SectionFormulaResultsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IFormulaRunner _runner;
    private readonly IJobQueue _queue;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly IRoomScope _rooms;

    public SectionFormulaResultsController(DiarizDbContext db, IFormulaRunner runner, IJobQueue queue,
        IHubContext<TranscriptionHub> hub, IRoomScope rooms)
    {
        _db = db;
        _runner = runner;
        _queue = queue;
        _hub = hub;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Load a section and check the caller may view it - i.e. is a member of the section's room. Returns
    /// null when the section is missing OR the caller isn't a member, so callers 404 either way and a room's
    /// contents stay private (same gate as <c>SectionPageController.ViewableSectionAsync</c>).</summary>
    private async Task<Section?> ViewableSectionAsync(Guid id)
    {
        var section = await _db.Sections.FirstOrDefaultAsync(s => s.Id == id);
        if (section is null || !await _rooms.IsMemberAsync(UserId, section.RoomId)) return null;
        return section;
    }

    /// <summary>The section id plus its child section ids (within the same room) - the placements that count as
    /// "included" in this folder (mirrors <c>SectionPageController.IncludedSectionIdsAsync</c>).</summary>
    private async Task<List<Guid>> IncludedSectionIdsAsync(Guid sectionId, Guid roomId, CancellationToken ct)
    {
        var ids = await _db.Sections
            .Where(s => s.RoomId == roomId && s.ParentId == sectionId).Select(s => s.Id).ToListAsync(ct);
        ids.Add(sectionId);
        return ids;
    }

    /// <summary>Kicks off an async formula run over a FOLDER (map-reduce across its included meetings): gate
    /// folder membership (404), validate formula run-access + LLM config (via
    /// <see cref="IFormulaRunner.ValidateFormulaRunAccessAsync"/>, 404/403/400), require at least one included
    /// meeting (400), create a pending <c>Generating</c> <see cref="SectionFormulaResult"/>, enqueue a
    /// section-scoped <see cref="FormulaRunJob"/>, and return <c>202 Accepted</c>. The <c>FormulaRunProcessor</c>
    /// flips the row to Ready/Failed and notifies the browser over SignalR.</summary>
    [HttpPost("~/api/sections/{sectionId:guid}/formulas/{formulaId:guid}/run")]
    public async Task<ActionResult<SectionFormulaResultDto>> Run(Guid sectionId, Guid formulaId, CancellationToken ct)
    {
        var section = await ViewableSectionAsync(sectionId);
        if (section is null) return NotFound();

        Formula formula;
        try
        {
            formula = await _runner.ValidateFormulaRunAccessAsync(UserId, formulaId, ct);
        }
        catch (FormulaNotFoundException)
        {
            return NotFound();
        }
        catch (FormulaAccessException ex)
        {
            return Forbidden(ex.Message);
        }
        catch (FormulaNotConfiguredException)
        {
            return BadRequest("Formulas need an AI endpoint. Set one in Settings.");
        }

        var includedSectionIds = await IncludedSectionIdsAsync(sectionId, section.RoomId, ct);
        var included = await _db.RoomRecordings
            .Where(p => p.RoomId == section.RoomId && p.SectionId.HasValue && includedSectionIds.Contains(p.SectionId.Value))
            .CountAsync(ct);
        if (included == 0)
            return BadRequest("This folder has no meetings to run a formula over.");

        var ordinal = (await _db.SectionFormulaResults.Where(r => r.SectionId == sectionId)
            .Select(r => (int?)r.Ordinal).MaxAsync(ct) ?? -1) + 1;
        var result = new SectionFormulaResult
        {
            Id = Guid.NewGuid(),
            SectionId = sectionId,
            CreatedByUserId = UserId,
            FormulaId = formula.Id,
            Name = formula.Name,
            Ordinal = ordinal,
            Status = FormulaRunStatus.Generating,
        };
        _db.SectionFormulaResults.Add(result);
        await _db.SaveChangesAsync(ct);

        await _queue.EnqueueFormulaRunAsync(new FormulaRunJob(null, sectionId, result.Id, formula.Id, UserId), ct);
        await _hub.NotifyFormulaStatusAsync(UserId, null, sectionId, result.Id, FormulaRunStatus.Generating.ToString());

        var origins = await FormulaResultOrigins.ResolveAsync(
            _db, new[] { (result.Id, result.FormulaId, result.CreatedByUserId) }, ct);
        return Accepted(ToDto(result, origins[result.Id]));
    }

    private ObjectResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);

    private static SectionFormulaResultDto ToDto(SectionFormulaResult r, FormulaResultOriginDto origin) => new(
        r.Id, r.SectionId, r.Name, r.CreatedByUserId, r.CreatedAt, r.UpdatedAt,
        r.Status.ToString(), r.Error, origin);
}
