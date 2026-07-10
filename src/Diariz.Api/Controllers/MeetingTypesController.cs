using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Meeting types (minutes templates). Every signed-in user can read the shared Platform types plus
/// their own Personal types. Writes (PR2) are gated: a Platform type needs a Platform Administrator; a Personal
/// type needs ownership.</summary>
[ApiController]
[Authorize]
[Route("api/meeting-types")]
public class MeetingTypesController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IUserPermissions _permissions;

    public MeetingTypesController(DiarizDbContext db, IUserPermissions permissions)
    {
        _db = db;
        _permissions = permissions;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Platform authority now lives in the caller's group membership, so this is a database read
    /// rather than a claim check. Awaited once per action and passed down, keeping ToDto synchronous.</summary>
    private Task<bool> IsPlatformAdminAsync() => _permissions.HasAsync(UserId, PlatformPermission.ManagePlatform);

    /// <summary>The Platform types (shared) plus the caller's own Personal types, grouped-ready (ordered by
    /// group then title).</summary>
    [HttpGet]
    public async Task<IReadOnlyList<MeetingTypeDto>> List()
    {
        var types = await _db.MeetingTypes
            .Where(m => m.UserId == null || m.UserId == UserId)
            .OrderBy(m => m.GroupName)
            .ThenBy(m => m.Title)
            .ToListAsync();
        var isPlatformAdmin = await IsPlatformAdminAsync();
        return types.Select(m => ToDto(m, isPlatformAdmin)).ToList();
    }

    /// <summary>Create a meeting type. A normal user always gets a Personal type they own; only a Platform
    /// Administrator may create a shared Platform type (<c>IsPlatform</c>).</summary>
    [HttpPost]
    public async Task<ActionResult<MeetingTypeDto>> Create(MeetingTypeRequest req)
    {
        if (Validate(req) is { } error) return BadRequest(error);

        var isPlatformAdmin = await IsPlatformAdminAsync();
        var platform = req.IsPlatform && isPlatformAdmin;
        var m = new MeetingType
        {
            Id = Guid.NewGuid(),
            UserId = platform ? null : UserId,
            GroupName = req.GroupName.Trim(),
            Title = req.Title.Trim(),
            Overview = req.Overview?.Trim() ?? string.Empty,
            Icon = req.Icon,
            Color = req.Color,
            ContentJson = req.Content.Serialize(),
        };
        _db.MeetingTypes.Add(m);
        await _db.SaveChangesAsync();
        return Ok(ToDto(m, isPlatformAdmin));
    }

    /// <summary>Replace a meeting type's fields and template atomically. A Platform type needs a Platform
    /// Administrator; a Personal type needs ownership. The Platform/Personal scope itself is not changed.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MeetingTypeDto>> Update(Guid id, MeetingTypeRequest req)
    {
        var m = await _db.MeetingTypes.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null || (m.UserId is not null && m.UserId != UserId)) return NotFound();
        var isPlatformAdmin = await IsPlatformAdminAsync();
        if (m.UserId is null && !isPlatformAdmin) return Forbidden("Only a Platform Administrator can edit a shared type.");
        if (Validate(req) is { } error) return BadRequest(error);

        m.GroupName = req.GroupName.Trim();
        m.Title = req.Title.Trim();
        m.Overview = req.Overview?.Trim() ?? string.Empty;
        m.Icon = req.Icon;
        m.Color = req.Color;
        m.ContentJson = req.Content.Serialize();
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(m, isPlatformAdmin));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var m = await _db.MeetingTypes.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null || (m.UserId is not null && m.UserId != UserId)) return NotFound();
        if (m.UserId is null && !await IsPlatformAdminAsync())
            return Forbidden("Only a Platform Administrator can delete a shared type.");

        // Recordings that used it fall back to the General default (FK is ON DELETE SET NULL).
        _db.MeetingTypes.Remove(m);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Field-level validation shared by create/update. Returns the first problem, or null when valid.</summary>
    private static string? Validate(MeetingTypeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return "A title is required.";
        if (string.IsNullOrWhiteSpace(req.GroupName)) return "A group name is required.";
        if (!MeetingTypeIcons.IsValid(req.Icon)) return "Unknown icon.";
        if (!IsHexColor(req.Color)) return "The colour must be a hex value like #5C6BC0.";
        var (ok, error) = (req.Content ?? MeetingTypeContent.Empty).Validate();
        return ok ? null : error;
    }

    private static bool IsHexColor(string? s) =>
        s is not null && System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9a-fA-F]{6}$");

    private ObjectResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);

    private MeetingTypeDto ToDto(MeetingType m, bool isPlatformAdmin)
    {
        var isPlatform = m.UserId is null;
        var canEdit = isPlatform ? isPlatformAdmin : m.UserId == UserId;
        return new MeetingTypeDto(
            m.Id, isPlatform, canEdit, m.GroupName, m.Title, m.Overview, m.Icon, m.Color,
            MeetingTypeContent.Parse(m.ContentJson), m.Key == MeetingType.GeneralKey);
    }
}
