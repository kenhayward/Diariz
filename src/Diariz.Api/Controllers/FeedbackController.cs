using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
        "Reading and deleting submitted feedback is a Platform Administrator surface, not exposed here.")]
    public async Task<IActionResult> Create(CreateFeedbackRequest req, CancellationToken ct = default)
    {
        var description = (req.Description ?? "").Trim();
        if (description.Length == 0) return BadRequest("Please describe the problem.");
        if (description.Length > MaxDescription) description = description[..MaxDescription];

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

        return Ok(new { id = row.Id });
    }
}
