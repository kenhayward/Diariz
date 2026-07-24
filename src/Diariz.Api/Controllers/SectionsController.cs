using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/sections")]
public class SectionsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IRoomScope _rooms;

    public SectionsController(DiarizDbContext db, IRoomScope rooms)
    {
        _db = db;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Resolve the room a section op targets (its own room by default) and check the caller may manage
    /// its contents - the room-id-based twin of <see cref="IRoomScope.ManageableSectionAsync"/>
    /// (<see cref="IRoomScope.AuthorizeManageContentsAsync"/>). Returns the room id, or an error result: 404 for
    /// a non-member (room existence stays private), 403 for a member lacking
    /// <see cref="RoomPermission.ManageContents"/>. The personal room's owner holds every permission, so the
    /// personal-room behaviour is unchanged.</summary>
    private async Task<(Guid RoomId, ActionResult? Error)> AuthorizeManage(Guid? roomIdArg, CancellationToken ct)
    {
        var roomId = roomIdArg ?? await _rooms.PersonalRoomIdAsync(UserId, ct);
        var error = await _rooms.AuthorizeManageContentsAsync(UserId, roomId, ct);
        return (roomId, error switch
        {
            RoomAccessError.NotFound => NotFound(),
            RoomAccessError.Forbidden => Forbid(),
            _ => null,
        });
    }

    [HttpGet]
    [EndpointSummary("List folders")]
    [EndpointDescription(
        "The folder tree of one room, flat and in display order. Defaults to your personal room; pass `roomId` " +
        "for a shared room you belong to. Build the tree from `parentId` - null means top level, and nesting " +
        "only ever goes **one level deep**, so a folder with a parent can never have children of its own.\n\n" +
        "Each room has its own independent folders. A non-member gets 404 rather than learning the room exists.")]
    public async Task<ActionResult<IReadOnlyList<SectionDto>>> List([FromQuery] Guid? roomId = null)
    {
        // The folders of the room being viewed (its personal room by default). Members only.
        var room = roomId ?? await _rooms.PersonalRoomIdAsync(UserId);
        if (!await _rooms.IsMemberAsync(UserId, room)) return NotFound();

        return await _db.Sections
            .Where(s => s.RoomId == room)
            .OrderBy(s => s.Position)
            .ThenBy(s => s.Name) // stable tiebreak (and preserves the old alphabetical order for legacy rows)
            .Select(s => new SectionDto(s.Id, s.Name, s.ParentId, s.Position))
            .ToListAsync();
    }

    [HttpPost]
    [EndpointSummary("Create a folder")]
    [EndpointDescription(
        "Adds a folder to a room, or a sub-folder when you pass `parentId`. **Idempotent by name**: if a " +
        "folder with the same name already exists under the same parent, that one is returned instead of a " +
        "duplicate being created - so re-running an import does not litter the tree. Compare the returned id " +
        "with what you expected if that matters to you.\n\n" +
        "Nesting is capped at one level, so a parent that is itself a sub-folder is rejected with 400. Needs " +
        "`ManageContents` in the room; you always hold it in your own personal room.")]
    public async Task<ActionResult<SectionDto>> Create(CreateSectionRequest req, CancellationToken ct = default)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("Section name is required.");

        var (roomId, error) = await AuthorizeManage(req.RoomId, ct);
        if (error is not null) return error;

        // A sub-section's parent must be in the caller's room and must itself be top-level (two-level cap).
        if (req.ParentId is { } parentId)
        {
            var parent = await _db.Sections.FirstOrDefaultAsync(s => s.Id == parentId && s.RoomId == roomId);
            if (parent is null) return NotFound();
            if (parent.ParentId is not null) return BadRequest("Sections can only be nested one level deep.");
        }

        // Reuse an existing same-named section under the same parent rather than creating a duplicate.
        var existing = await _db.Sections.FirstOrDefaultAsync(
            s => s.RoomId == roomId && s.Name == name && s.ParentId == req.ParentId);
        if (existing is not null) return Ok(new SectionDto(existing.Id, existing.Name, existing.ParentId, existing.Position));

        // UserId is still set (its column persists until it is dropped in a later step); RoomId is the scope.
        var section = new Section { Id = Guid.NewGuid(), UserId = UserId, RoomId = roomId, Name = name, ParentId = req.ParentId };
        _db.Sections.Add(section);
        await _db.SaveChangesAsync();
        return Ok(new SectionDto(section.Id, section.Name, section.ParentId, section.Position));
    }

    /// <summary>Drag-and-drop for sections: set the parent and 0-based position of each listed section in
    /// one call (reorder among siblings and/or reparent). Rejects moves that would nest more than one level
    /// deep — either targeting a parent that itself has a parent, or moving a section that has children.</summary>
    [HttpPut("reorder")]
    [EndpointSummary("Reorder or reparent folders")]
    [EndpointDescription(
        "Sets the parent and 0-based position of each listed folder in one call, covering both resequencing " +
        "among siblings and moving folders under a new parent. Pass a null `parentId` to move them to the top " +
        "level.\n\n" +
        "The one-level nesting cap is enforced here too, and in two ways: the target parent may not itself be " +
        "a sub-folder, and a folder that **has** sub-folders may not become one (both 400). A folder cannot be " +
        "its own parent. Every listed id must exist in the room, otherwise the whole call 404s and nothing " +
        "moves. Needs `ManageContents`.")]
    public async Task<IActionResult> Reorder(ReorderSectionsRequest req, CancellationToken ct = default)
    {
        var ids = (req.OrderedIds ?? []).ToList();
        if (ids.Count == 0) return NoContent();

        var (roomId, error) = await AuthorizeManage(req.RoomId, ct);
        if (error is not null) return error;

        if (req.ParentId is { } parentId)
        {
            if (ids.Contains(parentId)) return BadRequest("A section cannot be its own parent.");
            var parent = await _db.Sections.FirstOrDefaultAsync(s => s.Id == parentId && s.RoomId == roomId);
            if (parent is null) return NotFound();
            if (parent.ParentId is not null) return BadRequest("Sections can only be nested one level deep.");
        }

        var sections = await _db.Sections.Where(s => ids.Contains(s.Id) && s.RoomId == roomId).ToListAsync();
        if (sections.Count != ids.Count) return NotFound();

        // Moving a section under a parent is only allowed if that section has no children of its own.
        if (req.ParentId is not null)
        {
            var haveChildren = await _db.Sections.AnyAsync(
                s => s.RoomId == roomId && s.ParentId != null && ids.Contains(s.ParentId.Value));
            if (haveChildren) return BadRequest("A section with sub-sections can't become a sub-section.");
        }

        var byId = sections.ToDictionary(s => s.Id);
        for (var i = 0; i < ids.Count; i++)
        {
            var s = byId[ids[i]];
            s.ParentId = req.ParentId;
            s.Position = i;
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:guid}")]
    [EndpointSummary("Rename a folder")]
    [EndpointDescription(
        "Changes the folder's name. Its position, parent, and contents are untouched. An empty name is " +
        "rejected with 400. Names are not required to be unique, so renaming one folder to match a sibling is " +
        "allowed even though creating a duplicate by name is not. Needs `ManageContents` in the folder's room.")]
    public async Task<IActionResult> Rename(Guid id, RenameSectionRequest req, CancellationToken ct = default)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("Section name is required.");

        var section = await _db.Sections.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (section is null) return NotFound();
        var (_, error) = await AuthorizeManage(section.RoomId, ct);
        if (error is not null) return error;

        section.Name = name;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Delete a folder")]
    [EndpointDescription(
        "Removes the folder. **Recordings are not deleted** - they drop back to Ungrouped in that room, and " +
        "their placements in other rooms are unaffected. Its **sub-folders are deleted too**, and their " +
        "recordings likewise become ungrouped.\n\n" +
        "The folder's own attachments and formula documents go with it. Needs `ManageContents` in the room.")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var section = await _db.Sections.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (section is null) return NotFound();
        var (_, error) = await AuthorizeManage(section.RoomId, ct);
        if (error is not null) return error;

        // The placement FK is ON DELETE SET NULL, so the section's recordings drop back to "Ungrouped".
        _db.Sections.Remove(section);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
