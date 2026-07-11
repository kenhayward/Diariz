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
/// Ownership is a direct <c>Section.UserId</c> check; files count toward the owner's quota.</summary>
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

    private async Task<bool> OwnsAsync(Guid sectionId)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        return await _db.Sections.AnyAsync(s => s.Id == sectionId && s.RoomId == roomId);
    }

    private static AttachmentDto ToDto(SectionAttachment a) =>
        new(a.Id, a.Kind, a.Name, a.ContentType, a.SizeBytes, a.Url, a.Ordinal);

    private async Task<int> NextOrdinalAsync(Guid sectionId) =>
        (await _db.SectionAttachments.Where(a => a.SectionId == sectionId)
            .Select(a => (int?)a.Ordinal).MaxAsync() ?? -1) + 1;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> List(Guid sectionId)
    {
        if (!await OwnsAsync(sectionId)) return NotFound();
        var items = await _db.SectionAttachments
            .Where(a => a.SectionId == sectionId)
            .OrderBy(a => a.Ordinal)
            .Select(a => new AttachmentDto(a.Id, a.Kind, a.Name, a.ContentType, a.SizeBytes, a.Url, a.Ordinal))
            .ToListAsync();
        return items;
    }

    /// <summary>Upload a file attachment (any supporting document). Size-capped and quota-enforced.</summary>
    [HttpPost("file")]
    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<ActionResult<AttachmentDto>> AddFile(Guid sectionId, [FromForm] IFormFile? file)
    {
        if (!await OwnsAsync(sectionId)) return NotFound();
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
    public async Task<ActionResult<AttachmentDto>> AddMarkdown(Guid sectionId, AddMarkdownAttachmentRequest req)
    {
        if (!await OwnsAsync(sectionId)) return NotFound();
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
    public async Task<ActionResult<AttachmentDto>> AddUrl(Guid sectionId, AddUrlAttachmentRequest req)
    {
        if (!await OwnsAsync(sectionId)) return NotFound();
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return BadRequest("A valid http(s) URL is required.");

        var name = string.IsNullOrWhiteSpace(req.Name) ? uri.Host : req.Name!.Trim();
        var attachment = new SectionAttachment
        {
            Id = Guid.NewGuid(),
            SectionId = sectionId,
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
    public async Task<IActionResult> Rename(Guid sectionId, Guid attachmentId, RenameAttachmentRequest req)
    {
        if (!await OwnsAsync(sectionId)) return NotFound();
        var a = await _db.SectionAttachments.FirstOrDefaultAsync(x => x.Id == attachmentId && x.SectionId == sectionId);
        if (a is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("A name is required.");
        a.Name = SafeName(req.Name);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Overwrite a Markdown attachment's content in place (the in-app TipTap editor's Save).</summary>
    [HttpPut("{attachmentId:guid}/content")]
    public async Task<IActionResult> UpdateContent(
        Guid sectionId, Guid attachmentId, UpdateAttachmentContentRequest req)
    {
        if (!await OwnsAsync(sectionId)) return NotFound();
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
        if (used - a.SizeBytes + bytes.Length > quota)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                "Storage quota exceeded. Delete some recordings/attachments or ask an administrator to raise your quota.");

        await using (var ms = new MemoryStream(bytes))
            await _storage.UploadAsync(a.BlobKey, ms, "text/markdown");
        a.SizeBytes = bytes.Length;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{attachmentId:guid}")]
    public async Task<IActionResult> Delete(Guid sectionId, Guid attachmentId)
    {
        if (!await OwnsAsync(sectionId)) return NotFound();
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
    public async Task<IActionResult> Content(Guid sectionId, Guid attachmentId, CancellationToken ct = default)
    {
        if (!await OwnsAsync(sectionId)) return NotFound();
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
