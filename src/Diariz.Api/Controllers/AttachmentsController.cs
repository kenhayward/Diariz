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

/// <summary>Supporting-document attachments for a recording: list/add (file or URL)/rename/remove, plus a
/// content endpoint the browser opens directly. Files are stored in object storage and count toward the
/// owner's quota; URLs are just a row (address + display name).</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}/attachments")]
public class AttachmentsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IAudioStorage _storage;
    private readonly IStorageUsage _usage;
    private readonly AttachmentOptions _options;

    public AttachmentsController(
        DiarizDbContext db, IAudioStorage storage, IStorageUsage usage, IOptions<AttachmentOptions> options)
    {
        _db = db;
        _storage = storage;
        _usage = usage;
        _options = options.Value;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> OwnsAsync(Guid recordingId) =>
        _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);

    private static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, a.Kind, a.Name, a.ContentType, a.SizeBytes, a.Url, a.Ordinal);

    private async Task<int> NextOrdinalAsync(Guid recordingId) =>
        (await _db.Attachments.Where(a => a.RecordingId == recordingId)
            .Select(a => (int?)a.Ordinal).MaxAsync() ?? -1) + 1;

    [HttpGet]
    [EndpointSummary("List a recording's attachments")]
    [EndpointDescription(
        "The supporting documents attached to a recording, in display order. Two kinds come back in one list: " +
        "`File` (stored bytes, with a content type and size) and `Url` (just an address and a label, taking no " +
        "storage). Check `kind` before assuming an attachment has content to fetch.\n\n" +
        "**Owner only** - unlike notes and screenshots, a room member who can read the recording does not see " +
        "its attachments.")]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> List(Guid recordingId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var items = await _db.Attachments
            .Where(a => a.RecordingId == recordingId)
            .OrderBy(a => a.Ordinal)
            .Select(a => new AttachmentDto(a.Id, a.Kind, a.Name, a.ContentType, a.SizeBytes, a.Url, a.Ordinal))
            .ToListAsync();
        return items;
    }

    /// <summary>Upload a file attachment (any supporting document). Size-capped and quota-enforced.</summary>
    [HttpPost("file")]
    [EndpointSummary("Attach a file")]
    [EndpointDescription(
        "Uploads a supporting document as multipart form data - an agenda, a deck, a PDF, whatever is relevant " +
        "to the meeting. Any type is accepted; unlike a recording upload there is no format gate, because the " +
        "file is stored and served rather than transcribed.\n\n" +
        "The bytes count against your storage quota, and the file is capped by the platform's attachment " +
        "limit; either being exceeded returns 413. The display name is taken from the upload with any " +
        "directory path stripped and long names truncated, so read it back from the response.")]
    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<ActionResult<AttachmentDto>> AddFile(Guid recordingId, [FromForm] IFormFile? file)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        if (file is null || file.Length == 0) return BadRequest("A file is required.");
        if (file.Length > _options.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Attachment too large. The maximum is {_options.MaxBytes / (1024 * 1024)} MB.");

        // Enforce the owner's storage quota (audio + attachment bytes).
        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        if (used + file.Length > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                "Storage quota exceeded. Delete some recordings/attachments or ask an administrator to raise your quota.");

        var id = Guid.NewGuid();
        var ext = Path.GetExtension(file.FileName);
        var blobKey = $"{UserId}/attachments/{id}{ext}";

        await using (var stream = file.OpenReadStream())
            await _storage.UploadAsync(blobKey, stream, file.ContentType ?? "application/octet-stream");

        var attachment = new Attachment
        {
            Id = id,
            RecordingId = recordingId,
            Kind = AttachmentKind.File,
            Name = SafeName(StripPath(file.FileName)), // strip any smuggled path (cross-platform)
            BlobKey = blobKey,
            ContentType = file.ContentType ?? "application/octet-stream",
            SizeBytes = file.Length,
            Ordinal = await NextOrdinalAsync(recordingId),
        };
        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync();
        return ToDto(attachment);
    }

    /// <summary>Create a Markdown attachment from text content (the chat "add as attachment" tool posts here).
    /// Stored as a <c>.md</c> file blob with <c>text/markdown</c> content type; quota-enforced like any file.</summary>
    [HttpPost("markdown")]
    [EndpointSummary("Attach a Markdown document")]
    [EndpointDescription(
        "Creates an attachment from text you send as JSON, rather than uploading a file - this is how a chat " +
        "conversation gets saved onto a recording. It is stored as a real `.md` file blob, so it counts " +
        "against your quota exactly like an uploaded file and appears in the list as a `File`.\n\n" +
        "The name defaults to \"note\" and always gets a `.md` extension. Markdown attachments are the only " +
        "ones that can be edited afterwards, through the content endpoint.")]
    public async Task<ActionResult<AttachmentDto>> AddMarkdown(Guid recordingId, AddMarkdownAttachmentRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
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
        var blobKey = $"{UserId}/attachments/{id}.md";
        await using (var ms = new MemoryStream(bytes))
            await _storage.UploadAsync(blobKey, ms, "text/markdown");

        var attachment = new Attachment
        {
            Id = id,
            RecordingId = recordingId,
            Kind = AttachmentKind.File,
            Name = MarkdownName(req.Name),
            BlobKey = blobKey,
            ContentType = "text/markdown",
            SizeBytes = bytes.Length,
            Ordinal = await NextOrdinalAsync(recordingId),
        };
        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync();
        return ToDto(attachment);
    }

    /// <summary>A safe display name for a Markdown attachment: strip any path, default to "note", ensure it
    /// ends in <c>.md</c>.</summary>
    private static string MarkdownName(string? name)
    {
        var trimmed = SafeName(StripPath(name ?? ""));
        if (trimmed.Length == 0) trimmed = "note";
        return trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + ".md";
    }

    /// <summary>Attach a URL (address + optional display name).</summary>
    [HttpPost("url")]
    [EndpointSummary("Attach a URL")]
    [EndpointDescription(
        "Records a link alongside the recording - a ticket, a document, a wiki page. Nothing is fetched or " +
        "stored, so it uses no quota and there is no content to download; the page is only retrieved later if " +
        "the attachment is fed to chat.\n\n" +
        "Only `http` and `https` addresses are accepted (400 otherwise). Omit the name and the host is used " +
        "as the label.")]
    public async Task<ActionResult<AttachmentDto>> AddUrl(Guid recordingId, AddUrlAttachmentRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return BadRequest("A valid http(s) URL is required.");

        var name = string.IsNullOrWhiteSpace(req.Name) ? uri.Host : req.Name!.Trim();
        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            RecordingId = recordingId,
            Kind = AttachmentKind.Url,
            Name = SafeName(name),
            Url = uri.ToString(),
            Ordinal = await NextOrdinalAsync(recordingId),
        };
        _db.Attachments.Add(attachment);
        await _db.SaveChangesAsync();
        return ToDto(attachment);
    }

    [HttpPut("{attachmentId:guid}")]
    [EndpointSummary("Rename an attachment")]
    [EndpointDescription(
        "Changes the display name only - the stored file, its content type, and a URL attachment's address are " +
        "untouched, so renaming cannot change what an attachment points at. Names are trimmed and capped at " +
        "256 characters; an empty name is rejected with 400.")]
    public async Task<IActionResult> Rename(Guid recordingId, Guid attachmentId, RenameAttachmentRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var a = await _db.Attachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.RecordingId == recordingId);
        if (a is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("A name is required.");
        a.Name = SafeName(req.Name);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{attachmentId:guid}")]
    [EndpointSummary("Delete an attachment")]
    [EndpointDescription(
        "Removes the attachment permanently. For a file this also deletes the stored bytes and releases them " +
        "against your quota; for a URL only the row goes, and the linked page is of course unaffected.")]
    public async Task<IActionResult> Delete(Guid recordingId, Guid attachmentId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var a = await _db.Attachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.RecordingId == recordingId);
        if (a is null) return NotFound();

        if (a.Kind == AttachmentKind.File && a.BlobKey is not null)
            await _storage.DeleteAsync(a.BlobKey);
        _db.Attachments.Remove(a);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Streams a file attachment's bytes for the browser to open (inline) or download. Accepts the
    /// bearer via <c>access_token</c> (see Program.cs) so it can be opened in a new tab.</summary>
    [HttpGet("{attachmentId:guid}/content")]
    [EndpointSummary("Download an attachment")]
    [EndpointDescription(
        "Streams a file attachment's bytes with its stored content type, marked `inline` so a browser opens " +
        "viewable types such as PDFs and images in the tab and downloads the rest. Like the audio and " +
        "screenshot endpoints, the bearer may be supplied as an `access_token` query parameter so a new tab " +
        "can open it; **treat such a URL as a credential**.\n\n" +
        "URL attachments have no content and return 404 - follow their `url` instead.")]
    public async Task<IActionResult> Content(Guid recordingId, Guid attachmentId, CancellationToken ct = default)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var a = await _db.Attachments.FirstOrDefaultAsync(
            x => x.Id == attachmentId && x.RecordingId == recordingId && x.Kind == AttachmentKind.File, ct);
        if (a?.BlobKey is null) return NotFound();

        var stream = await _storage.OpenReadAsync(a.BlobKey, ct);
        // Inline so viewable types (PDF/images) open in the tab; the browser downloads the rest.
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(a.Name);
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        return File(stream, a.ContentType ?? "application/octet-stream");
    }

    /// <summary>Overwrite a Markdown attachment's content in place (the in-app TipTap editor's Save). The blob
    /// key is reused so MinIO replaces the object wholesale; the size is recomputed and quota re-checked on the
    /// delta. Only Markdown file attachments are editable this way.</summary>
    [HttpPut("{attachmentId:guid}/content")]
    [EndpointSummary("Replace a Markdown attachment's content")]
    [EndpointDescription(
        "Overwrites the document in place, keeping its id and name - this is what the in-app editor saves. " +
        "**Only Markdown attachments can be edited** (400 for any other file, and 404 for a URL attachment); " +
        "everything else is immutable once uploaded, so replace it by deleting and re-adding.\n\n" +
        "There is no version history: the previous content is gone. The stored size is recalculated and your " +
        "quota is re-checked against the difference, so growing a document can return 413 while shrinking one " +
        "gives bytes back.")]
    public async Task<IActionResult> UpdateContent(
        Guid recordingId, Guid attachmentId, UpdateAttachmentContentRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var a = await _db.Attachments.FirstOrDefaultAsync(
            x => x.Id == attachmentId && x.RecordingId == recordingId && x.Kind == AttachmentKind.File);
        if (a?.BlobKey is null) return NotFound();
        if (!MarkdownAttachments.IsMarkdown(a.Name, a.ContentType))
            return BadRequest("Only Markdown attachments can be edited.");

        var bytes = Encoding.UTF8.GetBytes(req.Content ?? string.Empty);
        if (bytes.Length > _options.MaxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                $"Attachment too large. The maximum is {_options.MaxBytes / (1024 * 1024)} MB.");

        var quota = await _db.Users.Where(u => u.Id == UserId).Select(u => u.QuotaBytes).FirstOrDefaultAsync();
        var used = await _usage.UsedBytesAsync(UserId);
        if (used - a.SizeBytes + bytes.Length > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                "Storage quota exceeded. Delete some recordings/attachments or ask an administrator to raise your quota.");

        await using (var ms = new MemoryStream(bytes))
            await _storage.UploadAsync(a.BlobKey, ms, "text/markdown");
        a.SizeBytes = bytes.Length;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Trim a display name and cap its length (the file branch path-strips before calling this).</summary>
    private static string SafeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 256 ? trimmed[..256] : trimmed;
    }

    /// <summary>Strip any smuggled directory path from a user-supplied name, taking everything after the
    /// last <c>/</c> or <c>\</c>. Unlike <see cref="Path.GetFileName(string?)"/> this is OS-independent:
    /// on Linux GetFileName ignores <c>\</c>, so <c>..\..\secret</c> would pass through unstripped (the
    /// API runs in Linux containers). It also never treats <c>:</c> as a volume separator, so names like
    /// <c>"Email: subject"</c> survive intact.</summary>
    private static string StripPath(string name)
    {
        var cut = name.LastIndexOfAny(new[] { '/', '\\' });
        return cut >= 0 ? name[(cut + 1)..] : name;
    }
}
