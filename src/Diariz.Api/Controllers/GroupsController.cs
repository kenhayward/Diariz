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
    public async Task<List<GroupDto>> List() =>
        await db.UserGroups
            .OrderByDescending(g => g.IsSystem).ThenBy(g => g.Name)
            .Select(g => new GroupDto(g.Id, g.Name, g.Description, g.Icon, g.Color, (int)g.Permissions, g.IsSystem,
                g.Members.Select(m => m.UserId).ToArray()))
            .ToListAsync();

    [HttpPost]
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
