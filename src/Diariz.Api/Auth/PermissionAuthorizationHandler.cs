using System.Security.Claims;
using Diariz.Api.Services;
using Microsoft.AspNetCore.Authorization;

namespace Diariz.Api.Auth;

/// <summary>Backs the platform-permission policies. Reads the caller's group flags from the database on every
/// request, so adding or removing a user from a group takes effect immediately rather than at token expiry.</summary>
public class PermissionAuthorizationHandler(IUserPermissions permissions)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var raw = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(raw, out var userId)) return; // anonymous or malformed: fail closed

        if (await permissions.HasAsync(userId, requirement.AnyOf))
            context.Succeed(requirement);
    }
}
