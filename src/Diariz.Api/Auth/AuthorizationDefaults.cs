using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Diariz.Api.Auth;

/// <summary>The API's authorization policies, and its default for endpoints that name none.</summary>
public static class AuthorizationDefaults
{
    public static void Configure(AuthorizationOptions o)
    {
        // Deny by default. An endpoint that forgets [Authorize] requires a signed-in user instead of quietly becoming
        // public; anything meant to be public says so with [AllowAnonymous] / .AllowAnonymous(). The controller set is
        // pinned by AuthorizationDefaultsTests.
        o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        // Platform authority, resolved from the caller's group membership. Each policy requires ANY of the flags
        // it names.
        o.AddPolicy("ManageRooms", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageRooms)));
        o.AddPolicy("ManageUsers", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageUsers)));
        o.AddPolicy("ManagePlatform", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManagePlatform)));
        o.AddPolicy("ManageFormulas", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageFormulas)));
        o.AddPolicy("ManagePeople", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManagePeople)));
        o.AddPolicy("ManageVoiceprints", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageVoiceprints)));
        // Reading platform settings: the Manage Users modal shows the default quota, so an Administrator
        // (ManageUsers, no ManagePlatform) must still be able to GET them. Writes remain ManagePlatform.
        o.AddPolicy("ReadAdminSettings", p => p.AddRequirements(
            new PermissionRequirement(PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform)));

        // The /mcp endpoint authenticates only with the MCP token scheme (not the browser's JWT).
        o.AddPolicy(McpBearerAuthenticationHandler.SchemeName, p =>
        {
            p.AddAuthenticationSchemes(McpBearerAuthenticationHandler.SchemeName);
            p.RequireAuthenticatedUser();
        });
    }
}
