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
    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9_-]{1,62}$", RegexOptions.Compiled);
    private readonly DiarizDbContext _db;
    public WorkflowSignalsController(DiarizDbContext db) => _db = db;

    /// <summary>Active signals, for the formula-author picker. Any authenticated user.</summary>
    [HttpGet]
    [EndpointSummary("List active workflow signals")]
    [EndpointDescription(
        "The signals a formula author can attach to a formula - the picker behind \"When this finishes, " +
        "trigger: ...\". A signal is a **named intent**, like \"send to Slack\", that an administrator has " +
        "already wired to a destination; the author picks one without knowing the URL or that a webhook is " +
        "involved.\n\n" +
        "Active signals only, alphabetically. Open to any signed-in user - naming what can be triggered is " +
        "not privileged.")]
    public async Task<ActionResult<IReadOnlyList<WorkflowSignalDto>>> ListActive() =>
        await _db.WorkflowSignals.Where(s => s.IsActive).OrderBy(s => s.Label)
            .Select(s => new WorkflowSignalDto(s.Id, s.Key, s.Label, s.Description, s.IsActive)).ToListAsync();

    [HttpGet("manage")]
    [EndpointSummary("List all workflow signals for administration")]
    [EndpointDescription(
        "Every signal, **including inactive ones** - the admin view, as opposed to the picker, which shows " +
        "only what can be chosen. Requires a Platform Administrator, as does creating, editing and deleting.")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<ActionResult<IReadOnlyList<WorkflowSignalDto>>> ListAll() =>
        await _db.WorkflowSignals.OrderBy(s => s.Label)
            .Select(s => new WorkflowSignalDto(s.Id, s.Key, s.Label, s.Description, s.IsActive)).ToListAsync();

    [HttpPost]
    [EndpointSummary("Create a workflow signal")]
    [EndpointDescription(
        "Defines a new named intent for formula authors to choose. The `label` is what they see; the `key` is " +
        "the **routing value** a platform automation subscribes to, and is **immutable once created** - it is " +
        "lower-cased for you, must be 2 to 63 characters of lowercase letters, digits, hyphens or " +
        "underscores, and must be unique (400 on any of those).\n\n" +
        "Creating a signal on its own does nothing: pair it with a platform automation that selects it, or " +
        "formulas tagged with it will fire into nowhere.")]
    [Authorize(Policy = "ManagePlatform")]
    public async Task<ActionResult<WorkflowSignalDto>> Create(CreateWorkflowSignalRequest req)
    {
        var key = (req.Key ?? "").Trim().ToLowerInvariant();
        if (!KeyPattern.IsMatch(key)) return BadRequest("Key must be 2-63 chars of lowercase letters, digits, hyphens, or underscores.");
        if (string.IsNullOrWhiteSpace(req.Label)) return BadRequest("A label is required.");
        if (await _db.WorkflowSignals.AnyAsync(s => s.Key == key)) return BadRequest("That key is already in use.");

        var row = new WorkflowSignal { Id = Guid.NewGuid(), Key = key, Label = req.Label.Trim(), Description = req.Description };
        _db.WorkflowSignals.Add(row);
        await _db.SaveChangesAsync();
        return new WorkflowSignalDto(row.Id, row.Key, row.Label, row.Description, row.IsActive);
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Edit a workflow signal")]
    [EndpointDescription(
        "Changes the label, description, or whether the signal is active. **The key cannot be changed** - " +
        "automations route on it, so renaming would silently break every wiring; create a new signal instead.\n\n" +
        "Deactivating both hides it from the formula picker **and stops it firing**, while leaving existing " +
        "formulas tagged with it - so reactivating resumes delivery with the wiring intact. That makes it the " +
        "reversible way to switch a signal off, and the one to prefer over deleting.")]
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
    [EndpointSummary("Delete a workflow signal")]
    [EndpointDescription(
        "Removes the signal and **detaches it from every formula** that used it - those formulas keep working " +
        "but stop triggering anything, and automations that selected it stop matching. There is no undo, and " +
        "re-creating the same key does **not** restore the formula links: every author would have to re-tag " +
        "their formula. Deactivate instead when you only want to switch it off.")]
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
