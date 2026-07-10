using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>The rooms the signed-in user belongs to. Phase 3: read-only, and the only room is each user's
/// Personal room. Creation, editing and membership arrive with Manage Rooms in Phase 4.</summary>
[ApiController]
[Authorize]
[Route("api/rooms")]
public class RoomsController(IRoomScope rooms) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var list = await rooms.RoomsForUserAsync(UserId, ct);
        return Ok(list.Select(r => new RoomListItemDto(
            r.Id, r.Name, r.Kind, r.Icon, r.Color, r.IsPersonal, (int)r.Permissions)));
    }
}
