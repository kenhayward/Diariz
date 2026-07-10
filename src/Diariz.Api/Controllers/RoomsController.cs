using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The rooms the signed-in user belongs to, and (for <c>ManageRooms</c> holders) creating, editing and
/// deleting shared rooms + their membership. The Personal room is immutable and private - it cannot be created,
/// renamed, deleted, or given members here.</summary>
[ApiController]
[Authorize]
[Route("api/rooms")]
public class RoomsController(IRoomScope rooms, DiarizDbContext db) : ControllerBase
{
    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var list = await rooms.RoomsForUserAsync(UserId, ct);
        return Ok(list.Select(r => new RoomListItemDto(
            r.Id, r.Name, r.Kind, r.Icon, r.Color, r.IsPersonal, (int)r.Permissions)));
    }

    /// <summary>A shared room with its membership, for the Manage Rooms editor. Members only (a stranger gets a
    /// 404, so room existence stays private).</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        if (!await rooms.IsMemberAsync(UserId, id, ct)) return NotFound();

        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (room is null) return NotFound();

        var members = await db.RoomMembers
            .Where(m => m.RoomId == id)
            .Select(m => new RoomMemberDto(m.PrincipalType, m.PrincipalId, (int)m.Permissions))
            .ToListAsync(ct);

        return Ok(new RoomDetailDto(room.Id, room.Name, room.Description, room.Icon, room.Color, members));
    }

    [HttpPost]
    [Authorize(Policy = "ManageRooms")]
    public async Task<IActionResult> Create(RoomInput input, CancellationToken ct = default)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("A room name is required.");
        if (await SharedNameTaken(name, null, ct)) return Conflict("A room with that name already exists.");

        var id = await rooms.CreateSharedRoomAsync(name, Trim(input.Description), Trim(input.Icon), Trim(input.Color), ct);
        return CreatedAtAction(nameof(Get), new { id }, new { id });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "ManageRooms")]
    public async Task<IActionResult> Update(Guid id, RoomInput input, CancellationToken ct = default)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("A room name is required.");

        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (room is null) return NotFound();
        if (room.Kind == RoomKind.Personal) return BadRequest("The Personal room cannot be edited.");
        if (await SharedNameTaken(name, id, ct)) return Conflict("A room with that name already exists.");

        await rooms.UpdateRoomAsync(id, name, Trim(input.Description), Trim(input.Icon), Trim(input.Color), ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "ManageRooms")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (room is null) return NotFound();
        if (room.Kind == RoomKind.Personal) return BadRequest("The Personal room cannot be deleted.");

        await rooms.DeleteRoomAsync(id, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/members")]
    [Authorize(Policy = "ManageRooms")]
    public async Task<IActionResult> SetMember(Guid id, RoomMemberInput input, CancellationToken ct = default)
    {
        var ok = await rooms.SetMemberAsync(id, input.PrincipalType, input.PrincipalId, (RoomPermission)input.Permissions, ct);
        return ok ? NoContent() : BadRequest("That room cannot take members.");
    }

    [HttpDelete("{id:guid}/members/{type}/{principalId:guid}")]
    [Authorize(Policy = "ManageRooms")]
    public async Task<IActionResult> RemoveMember(Guid id, RoomPrincipalType type, Guid principalId, CancellationToken ct = default)
    {
        var ok = await rooms.RemoveMemberAsync(id, type, principalId, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Unshare a recording from this room (delete its non-main placement). The recorder, or a member
    /// holding <c>RemoveOthersRecordings</c>, may do it. Never the main room - that is a delete, from its home.</summary>
    [HttpDelete("{roomId:guid}/recordings/{recordingId:guid}")]
    public async Task<IActionResult> RemoveRecording(Guid roomId, Guid recordingId, CancellationToken ct = default)
    {
        if (!await rooms.IsMemberAsync(UserId, roomId, ct)) return NotFound(); // don't reveal the room exists

        var isRecorder = await db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId, ct);
        if (!isRecorder &&
            !(await rooms.PermissionsAsync(UserId, roomId, ct)).HasFlag(RoomPermission.RemoveOthersRecordings))
            return StatusCode(StatusCodes.Status403Forbidden, "You can't remove others' recordings from this room.");

        var ok = await rooms.RemoveFromRoomAsync(recordingId, roomId, ct);
        return ok ? NoContent() : BadRequest("That recording isn't shared here (delete it from its home room).");
    }

    private Task<bool> SharedNameTaken(string name, Guid? excludeId, CancellationToken ct) =>
        db.Rooms.AnyAsync(r => r.Kind == RoomKind.Shared && r.Name == name && r.Id != excludeId, ct);

    private static string? Trim(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
