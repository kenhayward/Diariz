using System.Security.Claims;
using System.Text;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>CRUD + email/download for the <see cref="FormulaResult"/> Markdown documents saved on a recording.
/// Access is split into two levels so Phase 2 rooms can extend them later without reshaping the endpoints:
/// <b>view</b> (list/get/email/download) needs the caller to be able to view the recording; <b>edit/delete</b>
/// additionally needs the result's creator OR the recording owner. Phase 1 "view" is just recording
/// ownership - see <see cref="CanViewRecordingAsync"/>.</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}/formula-results")]
public class FormulaResultsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IEmailSender _email;

    public FormulaResultsController(DiarizDbContext db, IEmailSender email)
    {
        _db = db;
        _email = email;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Phase 1 view gate: the caller must own the recording. Returns the recording (for the
    /// caller to use its Name/Title) or null when it isn't viewable/doesn't exist - callers 404 either
    /// way so a recording's existence isn't leaked to a non-viewer.</summary>
    private async Task<Recording?> CanViewRecordingAsync(Guid recordingId) =>
        await _db.Recordings.FirstOrDefaultAsync(r => r.Id == recordingId && r.UserId == UserId);

    /// <summary>Edit/delete gate: the result's creator OR the recording's owner. Only meaningful once the
    /// caller has already passed <see cref="CanViewRecordingAsync"/> for the same recording.</summary>
    private bool CanEdit(FormulaResult result, Recording recording) =>
        result.CreatedByUserId == UserId || recording.UserId == UserId;

    [HttpGet]
    [EndpointSummary("List a recording's formula documents")]
    [EndpointDescription(
        "The documents formulas have produced for this meeting, in display order, with the formula each came " +
        "from, its status, and whether it has been edited by hand. **Metadata only** - fetch one by id for its " +
        "text, so listing stays cheap however long the documents are.\n\n" +
        "**Owner only.** Unlike a folder's formula documents, which any room member can read, these follow the " +
        "recording's own visibility.")]
    public async Task<ActionResult<IReadOnlyList<FormulaResultDto>>> List(Guid recordingId)
    {
        var rec = await CanViewRecordingAsync(recordingId);
        if (rec is null) return NotFound();

        var results = await _db.FormulaResults
            .Where(r => r.RecordingId == recordingId)
            .OrderBy(r => r.Ordinal).ThenBy(r => r.CreatedAt)
            .ToListAsync();
        var origins = await FormulaResultOrigins.ResolveAsync(_db, results);
        return Ok(results.Select(r => ToDto(r, origins[r.Id])).ToList());
    }

    [HttpGet("{id:guid}")]
    [EndpointSummary("Read a formula document")]
    [EndpointDescription(
        "The document's Markdown text. One still generating comes back empty, so check its `status` in the " +
        "listing first. Ids are scoped to the recording in the path, so a document id from another meeting " +
        "404s.")]
    public async Task<ActionResult<FormulaResultTextDto>> Get(Guid recordingId, Guid id)
    {
        var rec = await CanViewRecordingAsync(recordingId);
        if (rec is null) return NotFound();

        var result = await _db.FormulaResults.FirstOrDefaultAsync(r => r.Id == id && r.RecordingId == recordingId);
        if (result is null) return NotFound();
        return Ok(new FormulaResultTextDto(result.Text));
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Edit a formula document")]
    [EndpointDescription(
        "Replaces the document's Markdown with your own. It is then marked hand-edited, which protects it " +
        "from **automatic** re-runs - the additional formulas a meeting type fires whenever the minutes " +
        "regenerate - but an explicit run of the same formula still overwrites it.\n\n" +
        "Editable by whoever ran it or by the recording's owner; anyone else gets 403.")]
    public async Task<ActionResult<FormulaResultDto>> Update(Guid recordingId, Guid id, UpdateFormulaResultRequest req)
    {
        var rec = await CanViewRecordingAsync(recordingId);
        if (rec is null) return NotFound();

        var result = await _db.FormulaResults.FirstOrDefaultAsync(r => r.Id == id && r.RecordingId == recordingId);
        if (result is null) return NotFound();
        if (!CanEdit(result, rec)) return Forbidden("Only the creator or the recording owner can edit this result.");

        result.Text = req.Text;
        // Mark it hand-edited: an AUTOMATIC re-run (a meeting type's additional formulas, re-firing when the
        // minutes regenerate) must never overwrite the user's own words. An explicit run still will.
        result.IsUserEdited = true;
        result.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        var origins = await FormulaResultOrigins.ResolveAsync(_db, new[] { result });
        return Ok(ToDto(result, origins[result.Id]));
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete a formula document")]
    [EndpointDescription(
        "Removes the document permanently. The formula itself is untouched, so you can run it over this " +
        "recording again. Same gate as editing: whoever ran it, or the recording's owner.")]
    public async Task<IActionResult> Delete(Guid recordingId, Guid id)
    {
        var rec = await CanViewRecordingAsync(recordingId);
        if (rec is null) return NotFound();

        var result = await _db.FormulaResults.FirstOrDefaultAsync(r => r.Id == id && r.RecordingId == recordingId);
        if (result is null) return NotFound();
        if (!CanEdit(result, rec)) return Forbidden("Only the creator or the recording owner can delete this result.");

        _db.FormulaResults.Remove(result);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Emails the result's Markdown to the signed-in user's OWN registered address - never a
    /// client-supplied address (mirrors <c>RecordingsController.EmailMeetingMinutes</c>).</summary>
    [HttpPost("{id:guid}/email")]
    [EndpointSummary("Email a formula document to yourself")]
    [EndpointDescription(
        "Sends the document, rendered from Markdown to HTML, to **your own account address**. There is no " +
        "recipient parameter, by design. Returns 400 when your account has no email address or the platform " +
        "has no email sender configured.")]
    public async Task<IActionResult> Email(Guid recordingId, Guid id)
    {
        var rec = await CanViewRecordingAsync(recordingId);
        if (rec is null) return NotFound();

        var result = await _db.FormulaResults.FirstOrDefaultAsync(r => r.Id == id && r.RecordingId == recordingId);
        if (result is null) return NotFound();

        var address = await _db.Users.Where(u => u.Id == UserId).Select(u => u.Email).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(address)) return BadRequest("Your account has no email address.");

        var html = MeetingMinutesEmail.BuildHtml(result.Name, MarkdownRenderer.ToHtml(result.Text));
        var subject = $"Formula result: {result.Name}";
        var sent = await _email.SendAsync(address!, subject, html);
        if (!sent) return BadRequest("Email isn't configured on the server. Contact an administrator.");
        return Ok();
    }

    /// <summary>Downloads the result's Markdown as a <c>.md</c> file (mirrors the transcript download's
    /// content-disposition handling in <c>RecordingsController</c>).</summary>
    [HttpGet("{id:guid}/download")]
    [EndpointSummary("Download a formula document")]
    [EndpointDescription(
        "Returns the document as a `.md` file download, named after the formula. The bytes are the same " +
        "Markdown the read endpoint returns - use this when you want a file rather than a JSON string.")]
    public async Task<IActionResult> Download(Guid recordingId, Guid id)
    {
        var rec = await CanViewRecordingAsync(recordingId);
        if (rec is null) return NotFound();

        var result = await _db.FormulaResults.FirstOrDefaultAsync(r => r.Id == id && r.RecordingId == recordingId);
        if (result is null) return NotFound();

        return File(Encoding.UTF8.GetBytes(result.Text), "text/markdown", $"{Slug(result.Name)}.md");
    }

    private ObjectResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);

    private static FormulaResultDto ToDto(FormulaResult r, FormulaResultOriginDto origin) => new(
        r.Id, r.RecordingId, r.Name, r.CreatedByUserId, r.CreatedAt, r.UpdatedAt, origin,
        r.Status.ToString(), r.Error);

    /// <summary>Filesystem-safe lowercase slug for download filenames (mirrors
    /// <c>RecordingsController.Slug</c>).</summary>
    private static string Slug(string? s)
    {
        var slug = new string((s ?? "").Trim()
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "formula-result" : slug;
    }
}
