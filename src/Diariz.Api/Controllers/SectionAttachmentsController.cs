using System.Security.Claims;
using System.Text;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Diariz.Api.Controllers;

/// <summary>Supporting-document attachments filed directly against a folder (section): list/add (file, URL or
/// Markdown)/rename/edit/remove, plus a content endpoint the browser opens directly. Mirrors
/// <see cref="AttachmentsController"/> (recording attachments) but is owned by the folder itself, so these
/// survive independently of any one transcript. The route is <c>folder-attachments</c> to avoid colliding with
/// <see cref="SectionPageController"/>'s aggregated <c>/attachments</c> read of the folder's transcripts.
///
/// Access is gated by room membership/permission, not by a direct <c>Section.UserId</c> check (that check used
/// to hardcode the caller's OWN personal room, so every route 404'd for a folder in a shared room - including
/// for whoever created it). Read (<see cref="List"/>, <see cref="Content"/>) only requires the caller to be a
/// member of the section's room - <see cref="IRoomScope.ViewableSectionAsync"/>, shared with
/// <c>SectionPageController</c> and <c>SectionFormulaResultsController</c>. Write (add/rename/edit-content/delete)
/// additionally requires <see cref="RoomPermission.ManageContents"/> - <see cref="IRoomScope.ManageableSectionAsync"/>,
/// shared with <c>SectionPageController</c> and the room-id-based gate <see cref="SectionsController"/> uses for
/// folder create/rename/delete. The personal room's owner holds every permission (see
/// <see cref="IRoomScope.PermissionsAsync"/>), so personal-room behaviour is unchanged. Files count toward the
/// uploader's own quota, not the folder creator's (<see cref="SectionAttachment.UploadedByUserId"/>, checked
/// against the caller's own <c>QuotaBytes</c> here and summed by <c>StorageUsage</c>) - in a shared room, whoever
/// holds <see cref="RoomPermission.ManageContents"/> can add to a folder someone else created.</summary>
[ApiController]
[Authorize]
[Route("api/sections/{sectionId:guid}/folder-attachments")]
public class SectionAttachmentsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IAudioStorage _storage;
    private readonly IStorageUsage _usage;
    private readonly AttachmentOptions _options;
    private readonly IRoomScope _rooms;

    public SectionAttachmentsController(
        DiarizDbContext db, IAudioStorage storage, IStorageUsage usage, IOptions<AttachmentOptions> options,
        IRoomScope rooms)
    {
        _db = db;
        _storage = storage;
        _usage = usage;
        _options = options.Value;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Maps the shared <see cref="RoomAccessError"/> from <see cref="IRoomScope.ManageableSectionAsync"/>
    /// to this controller's status codes: 404 for a non-member/missing section, 403 for a member lacking the
    /// permission.</summary>
    private ActionResult? ToActionResult(RoomAccessError? error) => error switch
    {
        RoomAccessError.NotFound => NotFound(),
        RoomAccessError.Forbidden => Forbid(),
        _ => null,
    };

    private static AttachmentDto ToDto(SectionAttachment a) =>
        new(a.Id, a.Kind, a.Name, a.ContentType, a.SizeBytes, a.Url, a.Ordinal);

    private async Task<int> NextOrdinalAsync(Guid sectionId) =>
        (await _db.SectionAttachments.Where(a => a.SectionId == sectionId)
            .Select(a => (int?)a.Ordinal).MaxAsync() ?? -1) + 1;

    [HttpGet]
    [EndpointSummary("List a folder's own attachments")]
    [EndpointDescription(
        "The supporting documents filed against the **folder itself** - a terms of reference, a standing " +
        "agenda - rather than against any one meeting. These outlive the recordings in the folder, and are " +
        "distinct from the folder page's `/attachments` roll-up, which gathers the attachments of the " +
        "recordings inside it. Both `File` and `Url` kinds come back in one list; check `kind` before " +
        "assuming there is content to fetch.\n\n" +
        "Readable by **any member of the folder's room**, not just whoever created the folder.")]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> List(Guid sectionId)
    {
        if (await _rooms.ViewableSectionAsync(UserId, sectionId) is null) return NotFound();
        var items = await _db.SectionAttachments
            .Where(a => a.SectionId == sectionId)
            .OrderBy(a => a.Ordinal)
            .Select(a => new AttachmentDto(a.Id, a.Kind, a.Name, a.ContentType, a.SizeBytes, a.Url, a.Ordinal))
            .ToListAsync();
        return items;
    }

    /// <summary>Upload a file attachment (any supporting document). Size-capped and quota-enforced.</summary>
    [HttpPost("file")]
    [EndpointSummary("Attach a file to a folder")]
    [EndpointDescription(
        "Uploads a document against the folder as multipart form data. Any type is accepted - it is stored and " +
        "served, never transcribed.\n\n" +
        "The bytes count against **your own** storage quota, not the folder creator's, so in a shared room " +
        "each contributor pays for what they add. 413 when your quota or the platform's attachment size limit " +
        "would be exceeded. Needs `ManageContents` in the folder's room (403 for a member without it).")]
    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<ActionResult<AttachmentDto>> AddFile(Guid sectionId, [FromForm] IFormFile? file)
    {
        var (_, error) = await _rooms.ManageableSectionAsync(UserId, sectionId);
        if (ToActionResult(error) is { } errorResult) return errorResult;
        if (file is null || file.Length == 0) return BadRequest("A file is required.");
        if (file.Length > _options.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Attachment too large. The maximum is {_options.MaxBytes / (1024 * 1024)} MB.");

        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        if (used + file.Length > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                "Storage quota exceeded. Delete some recordings/attachments or ask an administrator to raise your quota.");

        var id = Guid.NewGuid();
        var ext = Path.GetExtension(file.FileName);
        var blobKey = $"{UserId}/section-attachments/{id}{ext}";

        await using (var stream = file.OpenReadStream())
            await _storage.UploadAsync(blobKey, stream, file.ContentType ?? "application/octet-stream");

        var attachment = new SectionAttachment
        {
            Id = id,
            SectionId = sectionId,
            UploadedByUserId = UserId,
            Kind = AttachmentKind.File,
            Name = SafeName(StripPath(file.FileName)),
            BlobKey = blobKey,
            ContentType = file.ContentType ?? "application/octet-stream",
            SizeBytes = file.Length,
            Ordinal = await NextOrdinalAsync(sectionId),
        };
        _db.SectionAttachments.Add(attachment);
        await _db.SaveChangesAsync();
        return ToDto(attachment);
    }

    /// <summary>Create a Markdown attachment from text content (the chat <c>/attach</c> command posts here for a
    /// folder). Stored as a <c>.md</c> file blob with <c>text/markdown</c> content type; quota-enforced.</summary>
    [HttpPost("markdown")]
    [EndpointSummary("Attach a Markdown document to a folder")]
    [EndpointDescription(
        "Creates a folder attachment from text sent as JSON rather than an uploaded file - this is what saving " +
        "a chat conversation to a folder does. Stored as a real `.md` blob, so it counts against your quota " +
        "like any file and lists as a `File`. The name defaults to \"note\" and always gets a `.md` extension. " +
        "Markdown attachments are the only ones editable afterwards. Needs `ManageContents`.")]
    public async Task<ActionResult<AttachmentDto>> AddMarkdown(Guid sectionId, AddMarkdownAttachmentRequest req)
    {
        var (_, error) = await _rooms.ManageableSectionAsync(UserId, sectionId);
        if (ToActionResult(error) is { } errorResult) return errorResult;
        if (string.IsNullOrWhiteSpace(req.Content)) return BadRequest("Content is required.");

        var bytes = Encoding.UTF8.GetBytes(req.Content);
        if (bytes.Length > _options.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Attachment too large. The maximum is {_options.MaxBytes / (1024 * 1024)} MB.");

        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        if (used + bytes.Length > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                "Storage quota exceeded. Delete some recordings/attachments or ask an administrator to raise your quota.");

        var id = Guid.NewGuid();
        var blobKey = $"{UserId}/section-attachments/{id}.md";
        await using (var ms = new MemoryStream(bytes))
            await _storage.UploadAsync(blobKey, ms, "text/markdown");

        var attachment = new SectionAttachment
        {
            Id = id,
            SectionId = sectionId,
            UploadedByUserId = UserId,
            Kind = AttachmentKind.File,
            Name = MarkdownName(req.Name),
            BlobKey = blobKey,
            ContentType = "text/markdown",
            SizeBytes = bytes.Length,
            Ordinal = await NextOrdinalAsync(sectionId),
        };
        _db.SectionAttachments.Add(attachment);
        await _db.SaveChangesAsync();
        return ToDto(attachment);
    }

    /// <summary>Attach a URL (address + optional display name).</summary>
    [HttpPost("url")]
    [EndpointSummary("Attach a URL to a folder")]
    [EndpointDescription(
        "Records a link against the folder. Nothing is fetched or stored, so it uses no quota and has no " +
        "content to download. Only `http` and `https` addresses are accepted (400 otherwise); omit the name " +
        "and the host is used as the label. Needs `ManageContents`.")]
    public async Task<ActionResult<AttachmentDto>> AddUrl(Guid sectionId, AddUrlAttachmentRequest req)
    {
        var (_, error) = await _rooms.ManageableSectionAsync(UserId, sectionId);
        if (ToActionResult(error) is { } errorResult) return errorResult;
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return BadRequest("A valid http(s) URL is required.");

        var name = string.IsNullOrWhiteSpace(req.Name) ? uri.Host : req.Name!.Trim();
        var attachment = new SectionAttachment
        {
            Id = Guid.NewGuid(),
            SectionId = sectionId,
            UploadedByUserId = UserId,
            Kind = AttachmentKind.Url,
            Name = SafeName(name),
            Url = uri.ToString(),
            Ordinal = await NextOrdinalAsync(sectionId),
        };
        _db.SectionAttachments.Add(attachment);
        await _db.SaveChangesAsync();
        return ToDto(attachment);
    }

    [HttpPut("{attachmentId:guid}")]
    [EndpointSummary("Rename a folder attachment")]
    [EndpointDescription(
        "Changes the display name only - the stored file, its content type, and a URL attachment's address are " +
        "untouched. Names are trimmed and capped at 256 characters; an empty name is rejected with 400. Needs " +
        "`ManageContents`.")]
    public async Task<IActionResult> Rename(Guid sectionId, Guid attachmentId, RenameAttachmentRequest req)
    {
        var (_, error) = await _rooms.ManageableSectionAsync(UserId, sectionId);
        if (ToActionResult(error) is { } errorResult) return errorResult;
        var a = await _db.SectionAttachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.SectionId == sectionId);
        if (a is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("A name is required.");
        a.Name = SafeName(req.Name);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Overwrite a Markdown attachment's content in place (the in-app TipTap editor's Save).</summary>
    [HttpPut("{attachmentId:guid}/content")]
    [EndpointSummary("Replace a folder Markdown attachment's content")]
    [EndpointDescription(
        "Overwrites the document in place, keeping its id and name. **Only Markdown attachments can be " +
        "edited** (400 for any other file, 404 for a URL attachment), and there is no version history - the " +
        "previous content is gone.\n\n" +
        "Editing also **transfers ownership of the stored bytes to you**, following the \"uploader pays\" rule: " +
        "in a shared room, editing someone else's folder document moves its size onto your quota. Your quota " +
        "is checked against the resulting change, so this can return 413 even when the document barely grew.")]
    public async Task<IActionResult> UpdateContent(
        Guid sectionId, Guid attachmentId, UpdateAttachmentContentRequest req)
    {
        var (_, error) = await _rooms.ManageableSectionAsync(UserId, sectionId);
        if (ToActionResult(error) is { } errorResult) return errorResult;
        var a = await _db.SectionAttachments.FirstOrDefaultAsync(
            x => x.Id == attachmentId && x.SectionId == sectionId && x.Kind == AttachmentKind.File);
        if (a?.BlobKey is null) return NotFound();
        if (!MarkdownAttachments.IsMarkdown(a.Name, a.ContentType))
            return BadRequest("Only Markdown attachments can be edited.");

        var bytes = Encoding.UTF8.GetBytes(req.Content ?? string.Empty);
        if (bytes.Length > _options.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Attachment too large. The maximum is {_options.MaxBytes / (1024 * 1024)} MB.");

        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        // `used` only counts rows already attributed to the caller, so only subtract the row's current size when
        // the caller already owns it - otherwise this edit is about to re-attribute a stranger's bytes onto the
        // caller's ledger for the first time, and none of the old size was ever part of `used` to subtract.
        var currentContribution = a.UploadedByUserId == UserId ? a.SizeBytes : 0;
        if (used - currentContribution + bytes.Length > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                "Storage quota exceeded. Delete some recordings/attachments or ask an administrator to raise your quota.");

        await using (var ms = new MemoryStream(bytes))
            await _storage.UploadAsync(a.BlobKey, ms, "text/markdown");
        a.SizeBytes = bytes.Length;
        // Re-attribute: whoever edits the content now owns the stored bytes, matching the "uploader pays" rule
        // the other write paths already enforce (a shared-room member with ManageContents need not be the
        // original uploader). The blob key keeps the ORIGINAL uploader's prefix (`{uploader}/section-attachments/
        // {id}.md`) - that's harmless, keys are opaque identifiers, not an access-control or billing signal.
        a.UploadedByUserId = UserId;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{attachmentId:guid}")]
    [EndpointSummary("Delete a folder attachment")]
    [EndpointDescription(
        "Removes the attachment permanently. For a file the stored bytes go too, released against **whoever " +
        "currently owns them** rather than the caller. For a URL only the row goes. Needs `ManageContents` in " +
        "the folder's room, so in a shared room you can delete a document another member added.")]
    public async Task<IActionResult> Delete(Guid sectionId, Guid attachmentId)
    {
        var (_, error) = await _rooms.ManageableSectionAsync(UserId, sectionId);
        if (ToActionResult(error) is { } errorResult) return errorResult;
        var a = await _db.SectionAttachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.SectionId == sectionId);
        if (a is null) return NotFound();

        if (a.Kind == AttachmentKind.File && a.BlobKey is not null)
            await _storage.DeleteAsync(a.BlobKey);
        _db.SectionAttachments.Remove(a);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Streams a file attachment's bytes for the browser to open (inline) or download. Accepts the
    /// bearer via <c>access_token</c> (see Program.cs) so it can be opened in a new tab.</summary>
    [HttpGet("{attachmentId:guid}/content")]
    [EndpointSummary("Download a folder attachment")]
    [EndpointDescription(
        "Streams a file attachment's bytes with its stored content type, marked `inline` so a browser opens " +
        "viewable types such as PDFs and images in the tab and downloads the rest. The bearer may be supplied " +
        "as an `access_token` query parameter so a new tab can open it; **treat such a URL as a credential**.\n\n" +
        "Readable by any member of the folder's room. URL attachments have no content and return 404.")]
    public async Task<IActionResult> Content(Guid sectionId, Guid attachmentId, CancellationToken ct = default)
    {
        if (await _rooms.ViewableSectionAsync(UserId, sectionId) is null) return NotFound();
        var a = await _db.SectionAttachments.FirstOrDefaultAsync(
            x => x.Id == attachmentId && x.SectionId == sectionId && x.Kind == AttachmentKind.File, ct);
        if (a?.BlobKey is null) return NotFound();

        var stream = await _storage.OpenReadAsync(a.BlobKey, ct);
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(a.Name);
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        return File(stream, a.ContentType ?? "application/octet-stream");
    }

    // ---- helpers (mirrors AttachmentsController) ----

    private static string MarkdownName(string? name)
    {
        var trimmed = SafeName(StripPath(name ?? ""));
        if (trimmed.Length == 0) trimmed = "note";
        return trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".md";
    }

    private static string SafeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 256 ? trimmed[..256] : trimmed;
    }

    private static string StripPath(string name)
    {
        var cut = name.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 ? name[(cut + 1)..] : name;
    }
}
