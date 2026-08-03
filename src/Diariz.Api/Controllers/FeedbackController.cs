using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Accepts a user's "something looks or behaves wrong" report, captured with the client-side trail
/// leading up to it. Any signed-in user may submit one; reading and deleting them is a Platform Administrator
/// surface (see <see cref="Diariz.Domain.Entities.Feedback"/>).</summary>
[ApiController]
[Route("api/feedback")]
[Authorize]
public class FeedbackController : ControllerBase
{
    /// <summary>Cap on stored description length. Generous for a real report, bounded so a paste of an
    /// entire transcript does not become a row nobody can read or delete comfortably.</summary>
    public const int MaxDescription = 4000;

    /// <summary>Cap on the stored trail. The client trail is a 30-entry ring buffer, so a real one is a few
    /// kilobytes - 64 KiB is roughly an order of magnitude of headroom and will never reject a genuine
    /// submission. It exists because the trail is stored verbatim in an unbounded <c>text</c> column and the
    /// submitter cannot delete their own rows (only a Platform Administrator can): without a cap, a signed-in
    /// user could park Kestrel's whole default body (~30 MB) per submission, repeatedly.</summary>
    public const int MaxTrailJson = 64 * 1024;

    private readonly DiarizDbContext _db;
    private readonly IWebhookPublisher _webhooks;

    public FeedbackController(DiarizDbContext db, IWebhookPublisher webhooks)
    {
        _db = db;
        _webhooks = webhooks;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    [EndpointSummary("Submit feedback about the app")]
    [EndpointDescription(
        "Stores a bug or UX report against the calling user, together with the SPA route and app release it " +
        "was filed from and a client-captured trail (already scrubbed browser-side) for reproduction context.\n\n" +
        "The description is trimmed and rejected if empty, and truncated to a bounded length if very long. " +
        "Reading and deleting submitted feedback is a Platform Administrator surface, not exposed here.\n\n" +
        "The trail is capped too, and an oversized one is rejected rather than trimmed - a truncated trail " +
        "is not valid JSON.")]
    [RequestSizeLimit(1024 * 1024)] // 1 MiB - a feedback body is kilobytes; this bounds it well under Kestrel's ~30 MB default.
    public async Task<IActionResult> Create(CreateFeedbackRequest req, CancellationToken ct = default)
    {
        var description = (req.Description ?? "").Trim();
        if (description.Length == 0) return BadRequest("Please describe the problem.");
        if (description.Length > MaxDescription) description = description[..MaxDescription];

        // Rejected, not truncated like the description: half a JSON array is not JSON, so trimming would
        // store a trail no reader could parse.
        if ((req.TrailJson?.Length ?? 0) > MaxTrailJson)
            return BadRequest("That report carries too much diagnostic detail. Please try again.");

        var row = new Feedback
        {
            UserId = UserId,
            // Npgsql rejects a non-zero offset on a timestamptz column.
            CreatedAt = DateTimeOffset.UtcNow.ToUniversalTime(),
            Description = description,
            Route = (req.Route ?? "").Trim(),
            Release = (req.Release ?? "").Trim(),
            TrailJson = string.IsNullOrWhiteSpace(req.TrailJson) ? "[]" : req.TrailJson,
        };
        _db.Feedback.Add(row);
        await _db.SaveChangesAsync(ct);

        // Best-effort, and after the save: the submission is stored whether or not any automation is
        // listening. The thin body carries no words; only a subscription that has opted in gets those.
        await _webhooks.PublishAsync(
            WebhookEventTypes.FeedbackSubmitted, row.UserId,
            data: new { id = row.Id, route = row.Route, release = row.Release, hasScreenshot = false },
            dataWithFeedbackText: new
            {
                id = row.Id, route = row.Route, release = row.Release, hasScreenshot = false,
                description = row.Description,
            },
            ct: ct);

        return Ok(new { id = row.Id });
    }

    /// <summary>Platform Administrator only - deliberately, including a user's own submissions. A
    /// per-user view would imply a support conversation this feature does not have, and would need its
    /// own ownership rules for no benefit.</summary>
    [HttpGet]
    [Authorize(Policy = "ManagePlatform")]
    [EndpointSummary("List all submitted feedback")]
    [EndpointDescription(
        "Platform Administrator only, deliberately including a user's own submissions - there is no per-user " +
        "feedback surface. Returns newest first.")]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var items = await _db.Feedback
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FeedbackDto(f.Id, f.UserId, f.User!.Email, f.CreatedAt,
                f.Description, f.Route, f.Release, f.TrailJson))
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "ManagePlatform")]
    [EndpointSummary("Delete a submitted feedback report")]
    [EndpointDescription("Platform Administrator only.")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var row = await _db.Feedback.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row is null) return NotFound();
        _db.Feedback.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
