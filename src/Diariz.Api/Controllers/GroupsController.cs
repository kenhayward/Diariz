using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

public record GroupInput(string Name, string? Description, string? Icon, string? Color, PlatformPermission Permissions);

/// <summary>A group as the web sees it. <paramref name="Permissions"/> is an int, not the enum: the API
/// serializes enums by name (JsonConfig), and the client does bit arithmetic on this field.</summary>
public record GroupDto(Guid Id, string Name, string? Description, string? Icon, string? Color,
    int Permissions, bool IsSystem, Guid[] MemberIds);

/// <summary>Group administration. The system group (Platform Administrators) is protected three ways: it cannot
/// be deleted, its name and permissions cannot be edited, and its last member cannot be removed. Any one of
/// those would let an administrator lock the whole deployment out of maintenance.</summary>
[ApiController]
[Route("api/groups")]
[Authorize(Policy = "ManageUsers")]
public class GroupsController(DiarizDbContext db) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("List user groups")]
    [EndpointDescription(
        "Every group, with its members and its platform permissions as a bitmask: `ManageRooms` 1, " +
        "`ManageUsers` 2, `ManagePlatform` 4, `ManageFormulas` 8. **Platform authority comes from group " +
        "membership** - there are no per-user permission flags - so this is where administrative power is " +
        "granted.\n\n" +
        "The built-in Platform Administrators group is flagged `isSystem` and comes first; it is protected " +
        "from deletion, renaming, and losing its last member. The whole section requires the Manage Users " +
        "permission.")]
    public async Task<List<GroupDto>> List() =>
        await db.UserGroups
            .OrderByDescending(g => g.IsSystem).ThenBy(g => g.Name)
            .Select(g => new GroupDto(g.Id, g.Name, g.Description, g.Icon, g.Color, (int)g.Permissions, g.IsSystem,
                g.Members.Select(m => m.UserId).ToArray()))
            .ToListAsync();

    [HttpPost]
    [EndpointSummary("Create a user group")]
    [EndpointDescription(
        "Creates a named permission holder. It starts empty - add members separately. Whatever permission " +
        "bits you set are granted to every member, so a group is the unit of administrative authority.\n\n" +
        "Group names are unique (409 on a clash) and an empty name is 400. A new group is never a system " +
        "group, whatever you send.")]
    public async Task<IActionResult> Create(GroupInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return BadRequest("A group needs a name.");

        var name = input.Name.Trim();
        if (await db.UserGroups.AnyAsync(g => g.Name == name))
            return Conflict($"A group named '{name}' already exists.");

        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = input.Description,
            Icon = input.Icon,
            Color = input.Color,
            Permissions = input.Permissions,
        };
        db.UserGroups.Add(group);
        await db.SaveChangesAsync();
        return Ok(new GroupDto(group.Id, group.Name, group.Description, group.Icon, group.Color,
            (int)group.Permissions, group.IsSystem, []));
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Edit a user group")]
    [EndpointDescription(
        "Changes a group's name, permissions, and appearance. Permission changes take effect immediately for " +
        "every member. Names stay unique (409 on a clash).\n\n" +
        "For the **system group** the name and permissions are **silently ignored** - only the description, " +
        "icon and colour change. Clearing `ManagePlatform` from Platform Administrators would lock the " +
        "deployment out of maintenance, so it is refused rather than obeyed. Check `isSystem` before offering " +
        "those fields for editing.")]
    public async Task<IActionResult> Update(Guid id, GroupInput input)
    {
        var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group is null) return NotFound();

        var name = input.Name.Trim();
        if (await db.UserGroups.AnyAsync(g => g.Name == name && g.Id != id))
            return Conflict($"A group named '{name}' already exists.");

        // A system group's name and flags belong to the seeder. Clearing ManagePlatform from Platform
        // Administrators would lock the deployment out of maintenance, so it is refused rather than obeyed.
        // Cosmetics (description, icon, colour) stay editable.
        if (!group.IsSystem)
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return BadRequest("A group needs a name.");
            group.Name = name;
            group.Permissions = input.Permissions;
        }
        group.Description = input.Description;
        group.Icon = input.Icon;
        group.Color = input.Color;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete a user group")]
    [EndpointDescription(
        "Removes the group. **The users themselves are untouched** - they simply lose whatever the group " +
        "granted them, both platform permissions and access to any shared room the group was a member of. " +
        "Those room memberships are cleaned up here, so no stale grant survives.\n\n" +
        "The Platform Administrators group cannot be deleted (403).")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group is null) return NotFound();
        if (group.IsSystem) return Forbidden("The Platform Administrators group can't be deleted.");

        // RoomMember.PrincipalId has no FK (it points at either AspNetUsers or UserGroups), so the database
        // cannot cascade. Sweep this group's room-membership rows here, or a stale row survives as a live grant
        // in every shared room the group belonged to once those have members (Phase 4).
        var memberships = await db.RoomMembers
            .Where(m => m.PrincipalType == RoomPrincipalType.Group && m.PrincipalId == id)
            .ToListAsync();
        db.RoomMembers.RemoveRange(memberships);

        db.UserGroups.Remove(group); // UserGroupMembers cascade; the users themselves are untouched
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:guid}/members/{userId:guid}")]
    [EndpointSummary("Add a user to a group")]
    [EndpointDescription(
        "Grants the user everything the group holds - platform permissions and membership of any shared room " +
        "the group belongs to - taking effect on their next request, with no sign-out needed. Idempotent. " +
        "404 for an unknown group or user.")]
    public async Task<IActionResult> AddMember(Guid id, Guid userId)
    {
        if (!await db.UserGroups.AnyAsync(g => g.Id == id)) return NotFound();
        // Checked explicitly: an unknown id would otherwise violate the FK and surface as a 500.
        if (!await db.Users.AnyAsync(u => u.Id == userId)) return NotFound();
        if (await db.UserGroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == userId)) return NoContent();

        db.UserGroupMembers.Add(new UserGroupMember { GroupId = id, UserId = userId });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}/members/{userId:guid}")]
    [EndpointSummary("Remove a user from a group")]
    [EndpointDescription(
        "Revokes what the group granted them; the account itself is untouched and they keep anything granted " +
        "another way. Idempotent - removing a non-member succeeds.\n\n" +
        "**The Platform Administrators group must keep at least one member** (403 on the last one), so a " +
        "deployment can never be left with nobody able to administer it.")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid userId)
    {
        var group = await db.UserGroups.FirstOrDefaultAsync(g => g.Id == id);
        if (group is null) return NotFound();

        var member = await db.UserGroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == userId);
        if (member is null) return NoContent();

        if (group.IsSystem && await db.UserGroupMembers.CountAsync(m => m.GroupId == id) <= 1)
            return Forbidden("The Platform Administrators group must always have at least one member.");

        db.UserGroupMembers.Remove(member);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private IActionResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);
}
