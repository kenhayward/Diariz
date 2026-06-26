using System.Security.Claims;
using Diariz.Api.Contracts;
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

    public SectionsController(DiarizDbContext db) => _db = db;

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IReadOnlyList<SectionDto>> List() =>
        await _db.Sections
            .Where(s => s.UserId == UserId)
            .OrderBy(s => s.Name)
            .Select(s => new SectionDto(s.Id, s.Name))
            .ToListAsync();

    [HttpPost]
    public async Task<ActionResult<SectionDto>> Create(CreateSectionRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("Section name is required.");

        // Reuse an existing same-named section rather than creating a duplicate.
        var existing = await _db.Sections.FirstOrDefaultAsync(s => s.UserId == UserId && s.Name == name);
        if (existing is not null) return Ok(new SectionDto(existing.Id, existing.Name));

        var section = new Section { Id = Guid.NewGuid(), UserId = UserId, Name = name };
        _db.Sections.Add(section);
        await _db.SaveChangesAsync();
        return Ok(new SectionDto(section.Id, section.Name));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, RenameSectionRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("Section name is required.");

        var section = await _db.Sections.FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId);
        if (section is null) return NotFound();

        section.Name = name;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var section = await _db.Sections.FirstOrDefaultAsync(s => s.Id == id && s.UserId == UserId);
        if (section is null) return NotFound();

        // The FK is ON DELETE SET NULL, so the section's recordings drop back to "Ungrouped".
        _db.Sections.Remove(section);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
