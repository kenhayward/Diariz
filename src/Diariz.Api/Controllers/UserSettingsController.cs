using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/user/settings")]
public class UserSettingsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IApiKeyProtector _protector;

    public UserSettingsController(DiarizDbContext db, IApiKeyProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<UserSettingsDto> Get()
    {
        var s = await _db.UserSettings.FindAsync(UserId);
        return new UserSettingsDto(s?.SummaryApiBase, s?.SummaryModel,
            !string.IsNullOrEmpty(s?.SummaryApiKeyEncrypted));
    }

    [HttpPut]
    public async Task<IActionResult> Update(UpdateUserSettingsRequest req)
    {
        var s = await _db.UserSettings.FindAsync(UserId);
        if (s is null)
        {
            s = new UserSettings { UserId = UserId };
            _db.UserSettings.Add(s);
        }

        s.SummaryApiBase = Blank(req.ApiBase);
        s.SummaryModel = Blank(req.Model);

        // Tri-state key: null leaves it unchanged, empty clears it, anything else replaces it.
        if (req.ApiKey is not null)
            s.SummaryApiKeyEncrypted = req.ApiKey.Length == 0 ? null : _protector.Protect(req.ApiKey);

        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
