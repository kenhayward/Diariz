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
    private readonly IRoomScope _rooms;

    public MeetingTypesController(DiarizDbContext db, IUserPermissions permissions, IRoomScope rooms)
    {
        _db = db;
        _permissions = permissions;
        _rooms = rooms;
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
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var types = await _db.MeetingTypes
            .Include(m => m.AdditionalFormulas)
            .Where(m => m.RoomId == null || m.RoomId == roomId)
            .OrderBy(m => m.GroupName)
            .ThenBy(m => m.Title)
            .ToListAsync();
        var isPlatformAdmin = await IsPlatformAdminAsync();
        return types.Select(m => ToDto(m, isPlatformAdmin, roomId)).ToList();
    }

    /// <summary>Create a meeting type. A normal user always gets a Personal type they own; only a Platform
    /// Administrator may create a shared Platform type (<c>IsPlatform</c>).</summary>
    [HttpPost]
    public async Task<ActionResult<MeetingTypeDto>> Create(MeetingTypeRequest req)
    {
        if (Validate(req) is { } error) return BadRequest(error);

        var isPlatformAdmin = await IsPlatformAdminAsync();
        var platform = req.IsPlatform && isPlatformAdmin;
        if (await ValidateFormulasAsync(platform, req) is { } formulaError) return BadRequest(formulaError);

        var m = new MeetingType
        {
            Id = Guid.NewGuid(),
            UserId = platform ? null : UserId, // still written until the UserId column is dropped
            // Personal types get the owner's room (now the scope); platform types stay null.
            RoomId = platform ? null : await _rooms.PersonalRoomIdAsync(UserId),
            GroupName = req.GroupName.Trim(),
            Title = req.Title.Trim(),
            Overview = req.Overview?.Trim() ?? string.Empty,
            Icon = req.Icon,
            Color = req.Color,
            PrimaryFormulaId = req.PrimaryFormulaId,
        };
        SetAdditional(m, req.AdditionalFormulaIds);
        _db.MeetingTypes.Add(m);
        await _db.SaveChangesAsync();
        return Ok(ToDto(m, isPlatformAdmin, m.RoomId ?? Guid.Empty));
    }

    /// <summary>Replace a meeting type's fields and template atomically. A Platform type needs a Platform
    /// Administrator; a Personal type needs ownership. The Platform/Personal scope itself is not changed.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MeetingTypeDto>> Update(Guid id, MeetingTypeRequest req)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var m = await _db.MeetingTypes
            .Include(x => x.AdditionalFormulas)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (m is null || (m.RoomId is not null && m.RoomId != roomId)) return NotFound();
        var isPlatformAdmin = await IsPlatformAdminAsync();
        if (m.RoomId is null && !isPlatformAdmin) return Forbidden("Only a Platform Administrator can edit a shared type.");
        if (Validate(req) is { } error) return BadRequest(error);

        // The type's OWN scope decides which formulas it may reference - not the payload's IsPlatform, which
        // Update ignores (the scope is fixed at create).
        if (await ValidateFormulasAsync(m.RoomId is null, req) is { } formulaError) return BadRequest(formulaError);

        m.GroupName = req.GroupName.Trim();
        m.Title = req.Title.Trim();
        m.Overview = req.Overview?.Trim() ?? string.Empty;
        m.Icon = req.Icon;
        m.Color = req.Color;
        m.PrimaryFormulaId = req.PrimaryFormulaId;
        SetAdditional(m, req.AdditionalFormulaIds);
        m.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(m, isPlatformAdmin, roomId));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var roomId = await _rooms.PersonalRoomIdAsync(UserId);
        var m = await _db.MeetingTypes.FirstOrDefaultAsync(x => x.Id == id);
        if (m is null || (m.RoomId is not null && m.RoomId != roomId)) return NotFound();
        if (m.RoomId is null && !await IsPlatformAdminAsync())
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
        return null;
    }

    /// <summary>Every formula the type references must be one that a run of it could actually use.
    ///
    /// <para>Minutes generate as the <b>recording owner</b>, and a Personal formula may only be run by its owner
    /// (see <c>FormulaRunner.EnsureCanRun</c>). So a <b>Platform</b> type pointing at someone's Personal formula
    /// would produce no minutes for every other user on the instance. Catch that here, at save - rather than
    /// letting it fail silently at generation time for people who never touched the template.</para>
    ///
    /// <para>A <b>Personal</b> type may reference anything its owner can run: their own formula, one they have
    /// subscribed to, or an enabled Platform/Diariz formula.</para></summary>
    private async Task<string?> ValidateFormulasAsync(bool platform, MeetingTypeRequest req)
    {
        var ids = new List<Guid>();
        if (req.PrimaryFormulaId is { } primary) ids.Add(primary);
        ids.AddRange(req.AdditionalFormulaIds ?? []);

        foreach (var id in ids.Distinct())
        {
            var f = await _db.Formulas.FirstOrDefaultAsync(x => x.Id == id);
            if (f is null) return "That formula no longer exists.";

            if (platform)
            {
                if (f.Scope == FormulaScope.Personal)
                    return $"\"{f.Name}\" is a personal formula, so a shared meeting type can't use it - " +
                           "everyone else would get no minutes. Use a platform or built-in formula.";
                if (!f.Enabled) return $"\"{f.Name}\" is disabled.";
                continue;
            }

            var canRun = f.Scope != FormulaScope.Personal
                ? f.Enabled
                : f.OwnerUserId == UserId
                  || (f.Shared && await _db.FormulaSubscriptions.AnyAsync(sub => sub.FormulaId == f.Id && sub.UserId == UserId));
            if (!canRun) return $"You can't use \"{f.Name}\".";
        }

        return null;
    }

    /// <summary>Replace the type's additional formulas with <paramref name="ids"/>, in the order given.</summary>
    private static void SetAdditional(MeetingType m, IReadOnlyList<Guid>? ids)
    {
        m.AdditionalFormulas.Clear();
        var ordinal = 0;
        foreach (var id in (ids ?? []).Distinct())
            m.AdditionalFormulas.Add(new MeetingTypeFormula { Id = Guid.NewGuid(), FormulaId = id, Ordinal = ordinal++ });
    }

    private static bool IsHexColor(string? s) =>
        s is not null && System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9a-fA-F]{6}$");

    private ObjectResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);

    private MeetingTypeDto ToDto(MeetingType m, bool isPlatformAdmin, Guid roomId)
    {
        var isPlatform = m.RoomId is null;
        var canEdit = isPlatform ? isPlatformAdmin : m.RoomId == roomId;
        return new MeetingTypeDto(
            m.Id, isPlatform, canEdit, m.GroupName, m.Title, m.Overview, m.Icon, m.Color,
            m.PrimaryFormulaId,
            m.AdditionalFormulas.OrderBy(f => f.Ordinal).Select(f => f.FormulaId).ToList(),
            m.Key == MeetingType.GeneralKey);
    }
}
