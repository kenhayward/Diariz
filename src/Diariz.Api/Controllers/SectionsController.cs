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

    [HttpGet]
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
    public async Task<ActionResult<SectionDto>> Create(CreateSectionRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("Section name is required.");

        var roomId = await _rooms.PersonalRoomIdAsync(UserId);

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
    public async Task<IActionResult> Reorder(ReorderSectionsRequest req)
    {
        var ids = (req.OrderedIds ?? []).ToList();
        if (ids.Count == 0) return NoContent();

        var roomId = await _rooms.PersonalRoomIdAsync(UserId);

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
    public async Task<IActionResult> Rename(Guid id, RenameSectionRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("Section name is required.");

        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var section = await _db.Sections.FirstOrDefaultAsync(s => s.Id == id && s.RoomId == roomId);
        if (section is null) return NotFound();

        section.Name = name;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var section = await _db.Sections.FirstOrDefaultAsync(s => s.Id == id && s.RoomId == roomId);
        if (section is null) return NotFound();

        // The placement FK is ON DELETE SET NULL, so the section's recordings drop back to "Ungrouped".
        _db.Sections.Remove(section);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
