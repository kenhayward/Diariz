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
