using Diariz.Api.Mcp;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>A resource the MCP client can list (id/name/description/mime, no body).</summary>
public sealed record McpResourceInfo(string Uri, string Name, string Description, string MimeType);

/// <summary>A resource's fetched body.</summary>
public sealed record McpResourceContent(string Uri, string MimeType, string Text);

public interface IMcpResourceService
{
    /// <summary>The user's recordings as MCP resources: a transcript resource per recording that has one, and a
    /// minutes resource where minutes exist. Newest first, capped.</summary>
    Task<IReadOnlyList<McpResourceInfo>> ListAsync(Guid userId, CancellationToken ct);

    /// <summary>Reads a <c>diariz://recording/{id}/{kind}</c> resource, or null when the URI is unknown or the
    /// recording isn't the user's / has no such content.</summary>
    Task<McpResourceContent?> ReadAsync(Guid userId, string uri, CancellationToken ct);
}

/// <summary>Backs the MCP resource handlers from the database, owner-scoped. Separate from the SDK handler glue
/// so the list/read logic is unit-testable with the in-memory provider.</summary>
public sealed class McpResourceService : IMcpResourceService
{
    /// <summary>Cap on listed recordings, so a large library doesn't produce an unbounded resource list.</summary>
    public const int MaxListed = 200;

    private readonly DiarizDbContext _db;

    public McpResourceService(DiarizDbContext db) => _db = db;

    public async Task<IReadOnlyList<McpResourceInfo>> ListAsync(Guid userId, CancellationToken ct)
    {
        var recs = await _db.Recordings
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(MaxListed)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Title,
                r.CreatedAt,
                Versions = r.Transcriptions.Select(t => new
                {
                    t.Version,
                    SegmentCount = t.Segments.Count,
                    HasMinutes = t.MeetingMinutes != null,
                }),
            })
            .ToListAsync(ct);

        var list = new List<McpResourceInfo>();
        foreach (var r in recs)
        {
            var current = r.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
            if (current is null) continue;
            var name = r.Name ?? r.Title;
            var date = r.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            if (current.SegmentCount > 0)
                list.Add(new McpResourceInfo(
                    McpResources.TranscriptUri(r.Id), $"{name} — transcript",
                    $"Transcript of \"{name}\" ({date})", McpResources.MarkdownMime));
            if (current.HasMinutes)
                list.Add(new McpResourceInfo(
                    McpResources.MinutesUri(r.Id), $"{name} — minutes",
                    $"Meeting minutes of \"{name}\" ({date})", McpResources.MarkdownMime));
        }
        return list;
    }

    public async Task<McpResourceContent?> ReadAsync(Guid userId, string uri, CancellationToken ct)
    {
        if (!McpResources.TryParse(uri, out var recordingId, out var kind)) return null;

        var rec = await _db.Recordings
            .Where(r => r.Id == recordingId && r.UserId == userId)
            .Include(r => r.Speakers)
            .Include(r => r.Transcriptions).ThenInclude(t => t.Segments)
            .Include(r => r.Transcriptions).ThenInclude(t => t.MeetingMinutes)
            .FirstOrDefaultAsync(ct);
        if (rec is null) return null;

        var current = rec.Transcriptions.OrderByDescending(t => t.Version).FirstOrDefault();
        if (current is null) return null;
        var name = rec.Name ?? rec.Title;

        if (kind == McpResources.MinutesKind)
        {
            var text = current.MeetingMinutes?.Text;
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new McpResourceContent(uri, McpResources.MarkdownMime, text.Trim());
        }

        // Transcript.
        var segs = current.Segments.ToList();
        if (segs.Count == 0) return null;
        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        return new McpResourceContent(
            uri, McpResources.MarkdownMime, McpResources.TranscriptText(name, rec.CreatedAt, segs, names));
    }
}
