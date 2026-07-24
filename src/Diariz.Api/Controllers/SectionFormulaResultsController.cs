using System.Security.Claims;
using System.Text;
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
/// stay private) - <see cref="IRoomScope.ViewableSectionAsync"/>, the shared gate also used by
/// <c>SectionPageController</c> and <c>SectionAttachmentsController</c>.</summary>
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
    private readonly IEmailSender _email;

    public SectionFormulaResultsController(DiarizDbContext db, IFormulaRunner runner, IJobQueue queue,
        IHubContext<TranscriptionHub> hub, IRoomScope rooms, IEmailSender email)
    {
        _db = db;
        _runner = runner;
        _queue = queue;
        _hub = hub;
        _rooms = rooms;
        _email = email;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

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
    [EndpointSummary("Run a formula over a folder")]
    [EndpointDescription(
        "Runs a formula across **every meeting in the folder and its sub-folders** and produces one " +
        "consolidated document. Returns 202 with the result in `Generating`; watch its `status` (or the " +
        "SignalR notification) for `Ready` or `Failed`, then fetch the text.\n\n" +
        "An explicit run **replaces this folder's existing document for that formula** rather than adding a " +
        "second one, and overwrites it even if it had been edited by hand.\n\n" +
        "You must be a member of the folder's room (404 otherwise), the formula must be one you can run " +
        "(404 unknown, 403 not yours or shared with you), an AI endpoint must be configured (400), and the " +
        "folder must contain at least one meeting (400).")]
    public async Task<ActionResult<SectionFormulaResultDto>> Run(Guid sectionId, Guid formulaId, CancellationToken ct)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, sectionId);
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

        // An explicit run replaces this folder's existing result for the formula rather than appending a duplicate.
        var result = (await FormulaResultUpsert.ForSectionAsync(_db, sectionId, formula, UserId, automatic: false, ct))!;
        await _db.SaveChangesAsync(ct);

        await _queue.EnqueueFormulaRunAsync(new FormulaRunJob(null, sectionId, result.Id, formula.Id, UserId), ct);
        await _hub.NotifyFormulaStatusAsync(UserId, null, sectionId, result.Id, FormulaRunStatus.Generating.ToString());

        var origins = await FormulaResultOrigins.ResolveAsync(
            _db, new[] { (result.Id, result.FormulaId, result.CreatedByUserId) }, ct);
        return Accepted(ToDto(result, origins[result.Id]));
    }

    // ---- Results CRUD (list/get/update/delete/email/download) ----
    // The section analog of the recording FormulaResultsController: view = section member; edit/delete = the
    // result's creator OR a member with room ManageContents (mirrors SectionPageController's manage gate). All
    // lookups are scoped to {sectionId} so a result id from another folder 404s.

    [HttpGet("{sectionId:guid}/formula-results")]
    [EndpointSummary("List a folder's formula documents")]
    [EndpointDescription(
        "The documents formulas have produced for this folder, in display order, with the formula each came " +
        "from, who ran it, its status, and whether it has been edited by hand. **Metadata only** - the " +
        "document text comes from fetching one by id, so a listing stays cheap however long the documents " +
        "are. Readable by any member of the folder's room.")]
    public async Task<ActionResult<IReadOnlyList<SectionFormulaResultDto>>> List(Guid sectionId)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, sectionId);
        if (section is null) return NotFound();

        var results = await _db.SectionFormulaResults
            .Where(r => r.SectionId == sectionId)
            .OrderBy(r => r.Ordinal).ThenBy(r => r.CreatedAt)
            .ToListAsync();
        var origins = await FormulaResultOrigins.ResolveAsync(
            _db, results.Select(r => (r.Id, r.FormulaId, r.CreatedByUserId)));
        return Ok(results.Select(r => ToDto(r, origins[r.Id])).ToList());
    }

    [HttpGet("{sectionId:guid}/formula-results/{id:guid}")]
    [EndpointSummary("Read a folder formula document")]
    [EndpointDescription(
        "The document's Markdown text. A document still generating comes back empty, so check its `status` in " +
        "the listing first. Ids are scoped to the folder in the path, so a document id from another folder " +
        "404s even when you can see that folder. Readable by any member of the room.")]
    public async Task<ActionResult<FormulaResultTextDto>> Get(Guid sectionId, Guid id)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, sectionId);
        if (section is null) return NotFound();

        var result = await ResultAsync(sectionId, id);
        if (result is null) return NotFound();
        return Ok(new FormulaResultTextDto(result.Text));
    }

    [HttpPut("{sectionId:guid}/formula-results/{id:guid}")]
    [EndpointSummary("Edit a folder formula document")]
    [EndpointDescription(
        "Replaces the document's Markdown with your own. It is then marked hand-edited, which protects it from " +
        "**automatic** re-runs (the ones a meeting type fires when minutes regenerate) - but an explicit run of " +
        "the same formula over this folder still overwrites it.\n\n" +
        "Editable by the person who ran it, or by a member with `ManageContents` in the room; anyone else who " +
        "can see it gets 403.")]
    public async Task<ActionResult<SectionFormulaResultDto>> Update(Guid sectionId, Guid id, UpdateFormulaResultRequest req)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, sectionId);
        if (section is null) return NotFound();

        var result = await ResultAsync(sectionId, id);
        if (result is null) return NotFound();
        if (!await CanEditAsync(result, section))
            return Forbidden("Only the creator or a member who can manage contents can edit this result.");

        result.Text = req.Text;
        // Mark it hand-edited: an AUTOMATIC re-run (a meeting type's additional formulas, re-firing when the
        // minutes regenerate) must never overwrite the user's own words. An explicit run still will.
        result.IsUserEdited = true;
        result.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        var origins = await FormulaResultOrigins.ResolveAsync(
            _db, new[] { (result.Id, result.FormulaId, result.CreatedByUserId) });
        return Ok(ToDto(result, origins[result.Id]));
    }

    [HttpDelete("{sectionId:guid}/formula-results/{id:guid}")]
    [EndpointSummary("Delete a folder formula document")]
    [EndpointDescription(
        "Removes the document permanently. The formula itself is untouched, so you can run it over the folder " +
        "again. Same gate as editing: the person who ran it, or a member with `ManageContents`.")]
    public async Task<IActionResult> Delete(Guid sectionId, Guid id)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, sectionId);
        if (section is null) return NotFound();

        var result = await ResultAsync(sectionId, id);
        if (result is null) return NotFound();
        if (!await CanEditAsync(result, section))
            return Forbidden("Only the creator or a member who can manage contents can delete this result.");

        _db.SectionFormulaResults.Remove(result);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Emails the result's Markdown to the signed-in user's OWN registered address - never a
    /// client-supplied address (mirrors <c>FormulaResultsController.Email</c>).</summary>
    [HttpPost("{sectionId:guid}/formula-results/{id:guid}/email")]
    [EndpointSummary("Email a folder formula document to yourself")]
    [EndpointDescription(
        "Sends the document, rendered from Markdown to HTML, to **your own account address**. There is no " +
        "recipient parameter, by design. Any member of the room who can read the document may mail it to " +
        "themselves. Returns 400 when your account has no email address or the platform has no email sender " +
        "configured.")]
    public async Task<IActionResult> Email(Guid sectionId, Guid id)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, sectionId);
        if (section is null) return NotFound();

        var result = await ResultAsync(sectionId, id);
        if (result is null) return NotFound();

        var address = await _db.Users.Where(u => u.Id == UserId).Select(u => u.Email).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(address)) return BadRequest("Your account has no email address.");

        var html = MeetingMinutesEmail.BuildHtml(result.Name, MarkdownRenderer.ToHtml(result.Text));
        var subject = $"Formula result: {result.Name}";
        var sent = await _email.SendAsync(address!, subject, html);
        if (!sent) return BadRequest("Email isn't configured on the server. Contact an administrator.");
        return Ok();
    }

    /// <summary>Downloads the result's Markdown as a <c>.md</c> file (mirrors
    /// <c>FormulaResultsController.Download</c>).</summary>
    [HttpGet("{sectionId:guid}/formula-results/{id:guid}/download")]
    [EndpointSummary("Download a folder formula document")]
    [EndpointDescription(
        "Returns the document as a `.md` file download, named after the formula. The bytes are the same " +
        "Markdown the read endpoint returns - use this when you want a file rather than a JSON string. " +
        "Readable by any member of the folder's room.")]
    public async Task<IActionResult> Download(Guid sectionId, Guid id)
    {
        var section = await _rooms.ViewableSectionAsync(UserId, sectionId);
        if (section is null) return NotFound();

        var result = await ResultAsync(sectionId, id);
        if (result is null) return NotFound();

        return File(Encoding.UTF8.GetBytes(result.Text), "text/markdown", $"{Slug(result.Name)}.md");
    }

    /// <summary>Load a result scoped to its folder so a result id from another section 404s.</summary>
    private Task<SectionFormulaResult?> ResultAsync(Guid sectionId, Guid id) =>
        _db.SectionFormulaResults.FirstOrDefaultAsync(r => r.SectionId == sectionId && r.Id == id);

    /// <summary>Edit/delete gate: the result's creator OR a member of the section's room with ManageContents
    /// (the personal-room owner holds every permission). Only meaningful once the caller has passed
    /// <see cref="IRoomScope.ViewableSectionAsync"/> for the same section.</summary>
    private async Task<bool> CanEditAsync(SectionFormulaResult result, Section section) =>
        result.CreatedByUserId == UserId ||
        (await _rooms.PermissionsAsync(UserId, section.RoomId)).HasFlag(RoomPermission.ManageContents);

    private ObjectResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);

    /// <summary>Filesystem-safe lowercase slug for download filenames (mirrors
    /// <c>FormulaResultsController.Slug</c>).</summary>
    private static string Slug(string? s)
    {
        var slug = new string((s ?? "").Trim()
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "formula-result" : slug;
    }

    private static SectionFormulaResultDto ToDto(SectionFormulaResult r, FormulaResultOriginDto origin) => new(
        r.Id, r.SectionId, r.Name, r.CreatedByUserId, r.CreatedAt, r.UpdatedAt,
        r.Status.ToString(), r.Error, origin);
}
