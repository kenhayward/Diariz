using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tools;

/// <summary>Shared helper for the tools that save a Markdown attachment (add_as_attachment, send_email): the
/// destination candidates are the in-context recordings the user actually owns. The client then resolves which
/// one — a single candidate is used directly; several prompt the user to pick.</summary>
internal static class AttachmentTargets
{
    public static async Task<List<DraftRecording>> ForContextAsync(
        DiarizDbContext db, ChatToolContext ctx, CancellationToken ct) =>
        await db.Recordings
            .Where(r => r.UserId == ctx.UserId && ctx.SelectedRecordingIds.Contains(r.Id))
            .Select(r => new DraftRecording(r.Id, r.Name ?? r.Title))
            .ToListAsync(ct);
}
