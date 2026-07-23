using System.Text.RegularExpressions;
using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The admin-defined vocabulary a formula author picks from when attaching a Workflow Signal to a
/// formula. Any authenticated user can list the active signals (the picker); only a Platform Administrator
/// (<c>ManagePlatform</c>) can manage the vocabulary itself.</summary>
[ApiController]
[Authorize]
[Route("api/workflow-signals")]
public class WorkflowSignalsController : ControllerBase
{
    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9-]{1,62}$", RegexOptions.Compiled);
    private readonly DiarizDbContext _db;
    public WorkflowSignalsController(DiarizDbContext db) => _db = db;

    /// <summary>Active signals, for the formula-author picker. Any authenticated user.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkflowSignalDto>>> ListActive() =>
        await _db.WorkflowSignals.Where(s => s.IsActive).OrderBy(s => s.Label)
            .Select(s => new WorkflowSignalDto(s.Id, s.Key, s.Label, s.Description, s.IsActive)).ToListAsync();

    [HttpGet("manage")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<ActionResult<IReadOnlyList<WorkflowSignalDto>>> ListAll() =>
        await _db.WorkflowSignals.OrderBy(s => s.Label)
            .Select(s => new WorkflowSignalDto(s.Id, s.Key, s.Label, s.Description, s.IsActive)).ToListAsync();

    [HttpPost]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<ActionResult<WorkflowSignalDto>> Create(CreateWorkflowSignalRequest req)
    {
        var key = (req.Key ?? "").Trim().ToLowerInvariant();
        if (!KeyPattern.IsMatch(key)) return BadRequest("Key must be 2-63 chars of lowercase letters, digits, or hyphens.");
        if (string.IsNullOrWhiteSpace(req.Label)) return BadRequest("A label is required.");
        if (await _db.WorkflowSignals.AnyAsync(s => s.Key == key)) return BadRequest("That key is already in use.");

        var row = new WorkflowSignal { Id = Guid.NewGuid(), Key = key, Label = req.Label.Trim(), Description = req.Description };
        _db.WorkflowSignals.Add(row);
        await _db.SaveChangesAsync();
        return new WorkflowSignalDto(row.Id, row.Key, row.Label, row.Description, row.IsActive);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<ActionResult<WorkflowSignalDto>> Update(Guid id, UpdateWorkflowSignalRequest req)
    {
        var row = await _db.WorkflowSignals.FirstOrDefaultAsync(s => s.Id == id);
        if (row is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Label)) return BadRequest("A label is required.");
        row.Label = req.Label.Trim();
        row.Description = req.Description;
        row.IsActive = req.IsActive;   // the Key is immutable once created (it's the routing slug)
        await _db.SaveChangesAsync();
        return new WorkflowSignalDto(row.Id, row.Key, row.Label, row.Description, row.IsActive);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var row = await _db.WorkflowSignals.FirstOrDefaultAsync(s => s.Id == id);
        if (row is null) return NotFound();
        _db.WorkflowSignals.Remove(row); // cascade removes FormulaWorkflowSignal links
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
