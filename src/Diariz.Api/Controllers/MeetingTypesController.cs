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

    public MeetingTypesController(DiarizDbContext db) => _db = db;

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsPlatformAdmin => User.IsInRole(Roles.PlatformAdministrator);

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
        return types.Select(ToDto).ToList();
    }

    private MeetingTypeDto ToDto(MeetingType m)
    {
        var isPlatform = m.UserId is null;
        var canEdit = isPlatform ? IsPlatformAdmin : m.UserId == UserId;
        return new MeetingTypeDto(
            m.Id, isPlatform, canEdit, m.GroupName, m.Title, m.Overview, m.Icon, m.Color,
            MeetingTypeContent.Parse(m.ContentJson));
    }
}
