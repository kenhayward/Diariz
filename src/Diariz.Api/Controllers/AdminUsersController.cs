using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>
/// Admin-only user management. Server enforces the authority rules regardless of the client:
/// the Platform Administrator can never be demoted/disabled/deleted, and no one may demote, disable,
/// or delete themselves (prevents lock-out).
/// </summary>
[ApiController]
[Authorize(Policy = "ManageUsers")]
[Route("api/admin/users")]
public class AdminUsersController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IEmailSender _email;
    private readonly DiarizDbContext _db;
    private readonly IPlatformSettingsService _platform;
    private readonly AppPublicOptions _appOpts;
    private readonly IUserPermissions _permissions;

    public AdminUsersController(
        UserManager<ApplicationUser> users, IEmailSender email, DiarizDbContext db,
        IPlatformSettingsService platform, IOptions<AppPublicOptions> appOpts, IUserPermissions permissions)
    {
        _users = users;
        _email = email;
        _db = db;
        _platform = platform;
        _appOpts = appOpts.Value;
        _permissions = permissions;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IReadOnlyList<AdminUserDto>> List()
    {
        var users = await _users.Users.OrderBy(u => u.Email).ToListAsync();

        // Used storage per user in one grouped query, then zip with the user rows.
        var usage = (await _db.Recordings
                .GroupBy(r => r.UserId)
                .Select(g => new { UserId = g.Key, Used = g.Sum(r => r.SizeBytes) })
                .ToListAsync())
            .ToDictionary(x => x.UserId, x => x.Used);

        var dtos = new List<AdminUserDto>(users.Count);
        foreach (var u in users)
            dtos.Add(new AdminUserDto(u.Id, u.Email ?? "", u.FullName, await AccountTypeOf(u), u.Status, u.IsEnabled,
                u.QuotaBytes, usage.TryGetValue(u.Id, out var used) ? used : 0, u.GoogleSubject is not null));
        return dtos;
    }

    /// <summary>Admin-created user: provide an email, the account is created and onboarded (a one-time
    /// setup link is emailed, or returned for the admin to share when SMTP is unconfigured).</summary>
    [HttpPost]
    public async Task<ActionResult<GrantResultDto>> Add(AddUserRequest req)
    {
        var email = req.Email?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(email)) return BadRequest("An email address is required.");
        if (await _users.FindByEmailAsync(email) is not null)
            return BadRequest("A user with that email already exists.");

        var user = new ApplicationUser
        {
            UserName = email, Email = email,
            FullName = string.IsNullOrWhiteSpace(req.FullName) ? null : req.FullName.Trim(),
            Status = UserStatus.Invited, IsEnabled = true, EmailConfirmed = false,
            QuotaBytes = (await _platform.GetAsync()).StarterQuotaBytes,
        };
        var result = await _users.CreateAsync(user); // no password until setup
        if (!result.Succeeded) return BadRequest(result.Errors.Select(e => e.Description));
        await _users.AddToRoleAsync(user, Roles.Standard);

        return await IssueSetupLinkAsync(user);
    }

    /// <summary>Raise (or lower) a user's storage quota. Any administrator may do this, up to the
    /// platform maximum set by the Platform Administrator.</summary>
    [HttpPut("{id:guid}/quota")]
    public async Task<IActionResult> SetQuota(Guid id, SetQuotaRequest req)
    {
        if (req.QuotaBytes < 0) return BadRequest("Quota can't be negative.");
        var max = (await _platform.GetAsync()).MaxQuotaBytes;
        if (req.QuotaBytes > max)
            return BadRequest($"Quota can't exceed the platform maximum of {max} bytes.");

        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        user.QuotaBytes = req.QuotaBytes;
        await _users.UpdateAsync(user);
        return NoContent();
    }

    [HttpPost("{id:guid}/grant")]
    public async Task<ActionResult<GrantResultDto>> Grant(Guid id)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (user.Status != UserStatus.Requested)
            return BadRequest("Only a pending access request can be granted.");

        // A Google-linked account already has a credential + a Google-verified email, so there is nothing to
        // set up — activate it directly (no setup link/email). Password requests still go via Invited→setup.
        if (user.GoogleSubject is not null)
        {
            user.Status = UserStatus.Active;
            user.EmailConfirmed = true;
            await _users.UpdateAsync(user);
            return new GrantResultDto(Emailed: false, SetupUrl: null);
        }

        user.Status = UserStatus.Invited;
        await _users.UpdateAsync(user);
        return await IssueSetupLinkAsync(user);
    }

    /// <summary>Generates the one-time setup link and emails it; returns the link on the no-SMTP fallback.</summary>
    private async Task<GrantResultDto> IssueSetupLinkAsync(ApplicationUser user)
    {
        var token = await _users.GenerateUserTokenAsync(user, TokenOptions.DefaultProvider, AccountSetup.TokenPurpose);
        var url = AccountSetup.BuildUrl(BaseUrl(), user.Email!, token);
        var emailed = await _email.SendAsync(user.Email!, "Set up your Diariz account",
            $"You've been granted access to Diariz. Set up your account here: <a href=\"{url}\">{url}</a>");
        return new GrantResultDto(emailed, emailed ? null : url);
    }

    [HttpPost("{id:guid}/deny")]
    public async Task<IActionResult> Deny(Guid id)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (user.Status != UserStatus.Requested)
            return BadRequest("Only a pending access request can be denied.");
        await _users.DeleteAsync(user);
        return NoContent();
    }

    [HttpPut("{id:guid}/role")]
    public async Task<IActionResult> SetRole(Guid id, SetRoleRequest req)
    {
        if (req.Role != Roles.Standard && req.Role != Roles.Administrator)
            return BadRequest("Role must be Standard or Administrator.");

        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (await IsPlatformAdmin(user)) return Forbidden("The Platform Administrator's role can't be changed.");
        if (user.Id == CurrentUserId) return Forbidden("You can't change your own role.");

        // Authority now comes from group membership, not Identity roles: promoting means joining the
        // Administrators group, demoting means leaving it. The Platform Administrators group is never
        // touched here - the guard above refuses to change a platform admin at all.
        var admins = await _db.UserGroups.FirstOrDefaultAsync(g => g.Name == Seeder.AdminsGroup);
        if (admins is null) return Problem("The Administrators group is missing.");

        var member = await _db.UserGroupMembers.FirstOrDefaultAsync(m => m.GroupId == admins.Id && m.UserId == user.Id);
        if (req.Role == Roles.Administrator && member is null)
            _db.UserGroupMembers.Add(new UserGroupMember { GroupId = admins.Id, UserId = user.Id });
        else if (req.Role == Roles.Standard && member is not null)
            _db.UserGroupMembers.Remove(member);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:guid}/enabled")]
    public async Task<IActionResult> SetEnabled(Guid id, SetEnabledRequest req)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (!req.IsEnabled)
        {
            if (await IsPlatformAdmin(user)) return Forbidden("The Platform Administrator can't be disabled.");
            if (user.Id == CurrentUserId) return Forbidden("You can't disable your own account.");
        }
        user.IsEnabled = req.IsEnabled;
        await _users.UpdateAsync(user);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        if (user is null) return NotFound();
        if (await IsPlatformAdmin(user)) return Forbidden("The Platform Administrator can't be deleted.");
        if (user.Id == CurrentUserId) return Forbidden("You can't delete your own account.");

        // RoomMember.PrincipalId has no FK (it points at either AspNetUsers or UserGroups), so the database
        // cannot cascade. Sweep the user's membership rows here, or a stale row survives - inert on their
        // orphaned personal room today, but a live grant in a shared room once those have members (Phase 4).
        var memberships = await _db.RoomMembers
            .Where(m => m.PrincipalType == RoomPrincipalType.User && m.PrincipalId == user.Id)
            .ToListAsync();
        _db.RoomMembers.RemoveRange(memberships);
        await _db.SaveChangesAsync();

        await _users.DeleteAsync(user); // cascades the user's recordings/sections/chat
        return NoContent();
    }

    private Task<bool> IsPlatformAdmin(ApplicationUser u) =>
        _permissions.HasAsync(u.Id, PlatformPermission.ManagePlatform);

    /// <summary>The account type a user's group membership adds up to. Derived, not stored: ManagePlatform is
    /// what the Platform Administrator group confers, ManageUsers what Administrators confers.</summary>
    private async Task<string> AccountTypeOf(ApplicationUser u)
    {
        var perms = await _permissions.ForAsync(u.Id);
        if (perms.HasFlag(PlatformPermission.ManagePlatform)) return Roles.PlatformAdministrator;
        if (perms.HasFlag(PlatformPermission.ManageUsers)) return Roles.Administrator;
        return Roles.Standard;
    }

    private string BaseUrl() =>
        !string.IsNullOrWhiteSpace(_appOpts.PublicUrl) ? _appOpts.PublicUrl : $"{Request.Scheme}://{Request.Host}";

    private IActionResult Forbidden(string message) => StatusCode(StatusCodes.Status403Forbidden, message);
}
