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
    [EndpointSummary("List your rooms")]
    [EndpointDescription(
        "Every room you belong to, starting with your private Personal room, each with its folder and " +
        "recording counts and **your** permissions in it as a bitmask: `ManageRoom` 1, `CreateRecording` 2, " +
        "`RemoveOthersRecordings` 4, `ShareOut` 8, `ManageContents` 16, `EditOthersRecordings` 32.\n\n" +
        "Test those bits to know what you may do before calling anything else - you always hold all of them " +
        "in your own Personal room. Rooms you are not a member of never appear.")]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var list = await rooms.RoomsForUserAsync(UserId, ct);

        // The switcher shows "N sections . M recordings" for every room, which is cross-room and so cannot be
        // derived on the client without one fetch per room. Two grouped counts over the caller's rooms, not a
        // query each: plain EF, so this stays provider-agnostic.
        var ids = list.Select(r => r.Id).ToList();
        var sectionCounts = await db.Sections.Where(s => ids.Contains(s.RoomId))
            .GroupBy(s => s.RoomId)
            .Select(g => new { RoomId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoomId, x => x.Count, ct);
        var recordingCounts = await db.RoomRecordings.Where(p => ids.Contains(p.RoomId))
            .GroupBy(p => p.RoomId)
            .Select(g => new { RoomId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoomId, x => x.Count, ct);

        return Ok(list.Select(r => new RoomListItemDto(
            r.Id, r.Name, r.Kind, r.Icon, r.Color, r.IsPersonal, (int)r.Permissions,
            sectionCounts.TryGetValue(r.Id, out var sc) ? sc : 0,
            recordingCounts.TryGetValue(r.Id, out var rc) ? rc : 0)));
    }

    /// <summary>A shared room with its membership, for the Manage Rooms editor. Members only (a stranger gets a
    /// 404, so room existence stays private).</summary>
    [HttpGet("{id:guid}")]
    [EndpointSummary("Get a room and its members")]
    [EndpointDescription(
        "A room with its full membership list, for the management editor. Each member is a **user or a " +
        "group** (`principalType` says which) with its own permission bitmask, and display names are resolved " +
        "for you - so you do not need permission to list platform users just to show who is in a room.\n\n" +
        "Members only: a non-member gets 404 rather than 403, so a room's existence stays private.")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct = default)
    {
        if (!await rooms.IsMemberAsync(UserId, id, ct)) return NotFound();

        var room = await db.Rooms.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (room is null) return NotFound();

        var memberRows = await db.RoomMembers.Where(m => m.RoomId == id).ToListAsync(ct);

        // Resolve display names server-side: a member's PrincipalId is a user or a group id (no FK, so we
        // look it up by type). Doing it here means a ManageRooms holder who can't list all platform users
        // still sees names rather than raw guids.
        var userIds = memberRows.Where(m => m.PrincipalType == RoomPrincipalType.User).Select(m => m.PrincipalId).ToList();
        var groupIds = memberRows.Where(m => m.PrincipalType == RoomPrincipalType.Group).Select(m => m.PrincipalId).ToList();
        var userNames = await db.Users.Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName ?? u.Email ?? u.UserName, ct);
        var groupNames = await db.UserGroups.Where(g => groupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => (string?)g.Name, ct);

        var members = memberRows
            .Select(m => new RoomMemberDto(m.PrincipalType, m.PrincipalId, (int)m.Permissions,
                m.PrincipalType == RoomPrincipalType.User
                    ? userNames.GetValueOrDefault(m.PrincipalId)
                    : groupNames.GetValueOrDefault(m.PrincipalId)))
            .ToList();

        return Ok(new RoomDetailDto(room.Id, room.Name, room.Description, room.Icon, room.Color, members));
    }

    [HttpPost]
    [EndpointSummary("Create a shared room")]
    [EndpointDescription(
        "Creates a shared workspace with its own folder tree, recordings, and membership. **You are added as " +
        "a full member automatically**, holding every room permission - a shared room has no owner, so " +
        "without that its creator could not manage it.\n\n" +
        "Shared room names must be unique across the platform (409 on a clash); an empty name is 400. " +
        "Requires the Manage Rooms platform permission. Personal rooms cannot be created - everyone gets " +
        "exactly one automatically.")]
    [Authorize(Policy = "ManageRooms")]
    public async Task<IActionResult> Create(RoomInput input, CancellationToken ct = default)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("A room name is required.");
        if (await SharedNameTaken(name, null, ct)) return Conflict("A room with that name already exists.");

        var id = await rooms.CreateSharedRoomAsync(name, Trim(input.Description), Trim(input.Icon), Trim(input.Color), ct);

        // The creator joins as a full member, so the room appears in their switcher/list and they can manage it
        // (a shared room resolves permissions from member rows, not an owner - unlike a personal room).
        await rooms.SetMemberAsync(id, RoomPrincipalType.User, UserId,
            RoomPermission.ManageRoom | RoomPermission.CreateRecording | RoomPermission.RemoveOthersRecordings |
            RoomPermission.ShareOut | RoomPermission.ManageContents | RoomPermission.EditOthersRecordings, ct);

        return CreatedAtAction(nameof(Get), new { id }, new { id });
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Edit a shared room")]
    [EndpointDescription(
        "Changes a room's name, description, icon and colour. Membership is managed separately. Names stay " +
        "unique across shared rooms (409 on a clash) and an empty name is 400.\n\n" +
        "The **Personal room cannot be edited** (400) - it is nobody's to rename. Requires Manage Rooms.")]
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
    [EndpointSummary("Delete a shared room")]
    [EndpointDescription(
        "Removes the room, its folders, and its membership. **Recordings are not deleted** - a room holds " +
        "placements, and every recording still lives in its owner's Personal room; only the sharing goes.\n\n" +
        "The **Personal room cannot be deleted** (400). Requires Manage Rooms.")]
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
    [EndpointSummary("Add or update a room member")]
    [EndpointDescription(
        "Adds a member or replaces their permissions - an **upsert**, so the same call invites someone and " +
        "later changes what they may do. The principal is a **user or a group**; adding a group grants its " +
        "permissions to everyone in it, now and as it changes, which is the tidier way to run a team.\n\n" +
        "`permissions` is the bitmask from the room list, and is replaced wholesale rather than merged. " +
        "Personal rooms cannot take members (400). Requires Manage Rooms.")]
    [Authorize(Policy = "ManageRooms")]
    public async Task<IActionResult> SetMember(Guid id, RoomMemberInput input, CancellationToken ct = default)
    {
        var ok = await rooms.SetMemberAsync(id, input.PrincipalType, input.PrincipalId, (RoomPermission)input.Permissions, ct);
        return ok ? NoContent() : BadRequest("That room cannot take members.");
    }

    [HttpDelete("{id:guid}/members/{type}/{principalId:guid}")]
    [EndpointSummary("Remove a room member")]
    [EndpointDescription(
        "Revokes a user's or group's access. They immediately stop seeing the room and everything shared into " +
        "it - but **recordings they contributed stay**, still owned by them in their own Personal room.\n\n" +
        "Give the same `principalType` used when adding them: removing a user does not remove access granted " +
        "through a group they are in, and vice versa. Requires Manage Rooms.")]
    [Authorize(Policy = "ManageRooms")]
    public async Task<IActionResult> RemoveMember(Guid id, RoomPrincipalType type, Guid principalId, CancellationToken ct = default)
    {
        var ok = await rooms.RemoveMemberAsync(id, type, principalId, ct);
        return ok ? NoContent() : NotFound();
    }

    /// <summary>Unshare a recording from this room (delete its non-main placement). The recorder, or a member
    /// holding <c>RemoveOthersRecordings</c>, may do it. Never the main room - that is a delete, from its home.</summary>
    [HttpDelete("{roomId:guid}/recordings/{recordingId:guid}")]
    [EndpointSummary("Unshare a recording from a room")]
    [EndpointDescription(
        "Removes a recording from this room without deleting it - the reverse of sharing one in. It stays in " +
        "its owner's Personal room and in any other room it was shared into.\n\n" +
        "You can unshare your own recording from any room you belong to; removing **someone else's** needs " +
        "the `RemoveOthersRecordings` permission (403 otherwise). A recording's home room cannot be unshared " +
        "from (400) - deleting it there means deleting the recording itself.")]
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
