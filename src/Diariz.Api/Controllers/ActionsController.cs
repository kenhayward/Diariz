using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Actions across the whole library: a flat list of every action on the caller's recordings (the
/// "Actions" tab) plus a bulk complete/un-complete. Per-recording extract/add/edit/remove live in
/// <see cref="RecordingActionsController"/>. Ownership is transitive via <c>Recording.UserId</c> — expressed
/// as an explicit join so it works on both Npgsql and the in-memory test provider.</summary>
[ApiController]
[Authorize]
[Route("api/actions")]
public class ActionsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IRoomScope _rooms;
    public ActionsController(DiarizDbContext db, IRoomScope rooms)
    {
        _db = db;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Every action on the recordings the caller can see — newest recording first, then by ordinal —
    /// each tagged with its source recording (id + display name) so the UI can link back to the transcript.
    /// With no <paramref name="roomId"/> this is the caller's own library; with one it is the recordings placed
    /// in that room (membership is the read gate - a non-member 404s).</summary>
    [HttpGet]
    [EndpointSummary("List action items across meetings")]
    [EndpointDescription(
        "Every action item in one flat list rather than per recording - the view behind the Actions tab. " +
        "Newest meeting first, each item tagged with the recording it came from so you can link back to the " +
        "moment it was agreed, and carrying its owner, deadline, and completion state for filtering.\n\n" +
        "With no `roomId` this covers your own library; with one it covers the recordings placed in that room " +
        "(404 if you are not a member). For the actions on a single recording, use its own endpoint.")]
    public async Task<ActionResult<IReadOnlyList<ActionListItemDto>>> List([FromQuery] Guid? roomId = null)
    {
        IQueryable<Recording> recs;
        if (roomId is { } rid)
        {
            if (!await _rooms.IsMemberAsync(UserId, rid)) return NotFound();
            recs = _rooms.RecordingsIn(rid);
        }
        else
        {
            recs = _db.Recordings.Where(r => r.UserId == UserId);
        }

        var actions = await (
            from a in _db.RecordingActions
            join r in recs on a.RecordingId equals r.Id
            orderby r.CreatedAt descending, a.Ordinal
            select new ActionListItemDto(
                a.Id, a.RecordingId, r.Name ?? r.Title, a.Text, a.Actor, a.Deadline,
                a.Ordinal, a.Completed, a.CompletedAt, a.CreatedAt, r.UserId)).ToListAsync();
        return actions;
    }

    /// <summary>Mark the given actions complete (or not). Sets/clears <c>CompletedAt</c> accordingly. Ids the
    /// caller doesn't own are silently ignored.</summary>
    [HttpPost("complete")]
    [EndpointSummary("Complete or reopen action items")]
    [EndpointDescription(
        "Ticks off several actions at once, or reopens them with `completed: false` - which also clears the " +
        "completion timestamp. **This is the only place completion is set**: the per-recording edit endpoint " +
        "changes text, owner and deadline but not this.\n\n" +
        "Ids that are not yours are silently skipped rather than failing the call, so a mixed selection still " +
        "works; an empty list succeeds without doing anything.")]
    public async Task<IActionResult> Complete(CompleteActionsRequest req)
    {
        var ids = req.Ids?.ToHashSet() ?? new HashSet<Guid>();
        if (ids.Count == 0) return NoContent();

        var owned = await (
            from a in _db.RecordingActions
            join r in _db.Recordings on a.RecordingId equals r.Id
            where ids.Contains(a.Id) && r.UserId == UserId
            select a).ToListAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var a in owned)
        {
            a.Completed = req.Completed;
            a.CompletedAt = req.Completed ? now : null;
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
