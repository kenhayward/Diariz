using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Diariz.Api.Auth;

/// <summary>Requires the caller to hold ANY of <paramref name="AnyOf"/>. "Any", not "all", so a single policy
/// can express "manage users OR manage platform" - which reading platform settings needs.</summary>
public record PermissionRequirement(PlatformPermission AnyOf) : IAuthorizationRequirement;
