using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>The fields of an attachment this resolver needs, independent of which table it came from.
/// <see cref="Attachment"/> (recording-owned) and <see cref="SectionAttachment"/> (folder-owned) are
/// shape-identical here, and a struct keeps the two callers from having to share a base type they otherwise
/// do not need.</summary>
public readonly record struct AttachmentRef(
    AttachmentKind Kind, string Name, string? BlobKey, string? ContentType, string? Url);

/// <summary>Turns one attachment into text for chat context: an uploaded file is streamed from object storage
/// and extracted; a URL is fetched behind the existing SSRF guards in <see cref="IUrlFetcher"/>.</summary>
public interface IAttachmentTextResolver
{
    /// <summary>The attachment's text, or null when there is none to be had - an unsupported file type, an
    /// empty extraction, an unreachable URL, or a read that failed. <b>Never throws</b> (except on
    /// cancellation): the bulk chat-context path must not fail a whole turn over one bad attachment, and the
    /// single-drop endpoint turns null into a 400.</summary>
    Task<AttachmentText?> ResolveAsync(AttachmentRef attachment, CancellationToken ct = default);
}

/// <summary>Extracted from <c>ChatController.LoadAttachmentDocumentsAsync</c> so that dropping one attachment
/// onto the chat composer and ticking "include attachments" read the same bytes the same way.</summary>
public sealed class AttachmentTextResolver(
    IAttachmentExtractor extractor, IAudioStorage storage, IUrlFetcher urls) : IAttachmentTextResolver
{
    public async Task<AttachmentText?> ResolveAsync(AttachmentRef a, CancellationToken ct = default)
    {
        try
        {
            if (a.Kind == AttachmentKind.Url)
            {
                if (string.IsNullOrWhiteSpace(a.Url)) return null;
                var text = await urls.FetchTextAsync(a.Url, ct);
                return string.IsNullOrWhiteSpace(text) ? null : new AttachmentText(a.Name, text!, text!.Length);
            }

            if (string.IsNullOrWhiteSpace(a.BlobKey)) return null;
            // Checked before the read, so an unsupported type costs no storage round-trip.
            if (!extractor.IsSupported(a.Name, a.ContentType)) return null;

            await using var stream = await storage.OpenReadAsync(a.BlobKey!, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var extracted = extractor.Extract(a.Name, a.ContentType, buffer.ToArray());
            return string.IsNullOrWhiteSpace(extracted.Text) ? null : extracted;
        }
        catch (OperationCanceledException)
        {
            throw; // a cancelled request is not a failed attachment
        }
        catch
        {
            return null;
        }
    }
}
