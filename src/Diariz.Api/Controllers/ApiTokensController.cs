using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Manages the signed-in user's personal REST-API tokens (used to call the Diariz API as themselves).
/// Authenticated with the normal JWT session; the tokens it mints are a separate credential. The plaintext is
/// returned only once, at generation - only its hash is stored.</summary>
[ApiController]
[Authorize]
[Route("api/user/api-tokens")]
public class ApiTokensController : ControllerBase
{
    /// <summary>A generous per-user cap so a runaway client can't create unbounded tokens.</summary>
    public const int MaxTokensPerUser = 20;

    private readonly DiarizDbContext _db;
    private readonly IApiTokenService _tokens;

    public ApiTokensController(DiarizDbContext db, IApiTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    [EndpointSummary("List your API tokens")]
    [EndpointDescription(
        "Your personal REST API tokens, newest first, with each one's name, scope, expiry, and when it was " +
        "last used - handy for spotting a token nothing is using any more.\n\n" +
        "**Only a short prefix is shown, never the token.** Only a hash is stored, so a lost token cannot be " +
        "recovered; create a new one and revoke the old.")]
    public async Task<IReadOnlyList<ApiTokenDto>> List() =>
        await _db.ApiAccessTokens
            .Where(t => t.UserId == UserId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new ApiTokenDto(
                t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.Scope.ToString(), t.ExpiresAt))
            .ToListAsync();

    [HttpPost]
    [EndpointSummary("Create an API token")]
    [EndpointDescription(
        "Mints a `dz_api_...` token for calling this API as yourself. **The token value is returned exactly " +
        "once, in this response** - store it now, because only a hash is kept and it can never be retrieved " +
        "again.\n\n" +
        "`readOnly` limits it to safe methods: any POST, PUT, PATCH or DELETE made with it is refused with " +
        "403, which is worth using for anything that only reads. `expiresAt` is optional (omit for a token " +
        "that never expires) and must be in the future. **Neither can be changed later** - replace the token " +
        "instead.\n\n" +
        "400 when the expiry is in the past or you have reached the per-user token limit.")]
    public async Task<ActionResult<ApiTokenCreatedDto>> Create(CreateApiTokenRequest req)
    {
        var name = string.IsNullOrWhiteSpace(req.Name) ? "API token" : req.Name.Trim();
        if (name.Length > 128) name = name[..128];

        if (await _db.ApiAccessTokens.CountAsync(t => t.UserId == UserId) >= MaxTokensPerUser)
            return BadRequest("Token limit reached. Revoke an existing token before creating another.");

        if (req.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
            return BadRequest("Expiry must be in the future.");

        var g = _tokens.Generate();
        var row = new ApiAccessToken
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = name,
            TokenHash = g.Hash,
            Prefix = g.Prefix,
            Scope = req.ReadOnly ? ApiTokenScope.ReadOnly : ApiTokenScope.ReadWrite,
            // Npgsql rejects a non-zero-offset DateTimeOffset for a `timestamptz` column, so normalise to UTC
            // before storing (same pattern as RecordingsController's calendar-link Start/End).
            ExpiresAt = req.ExpiresAt?.ToUniversalTime(),
        };
        _db.ApiAccessTokens.Add(row);
        await _db.SaveChangesAsync();

        // The plaintext token is returned exactly once - it is never persisted or retrievable again.
        return new ApiTokenCreatedDto(row.Id, row.Name, row.Prefix, g.Token);
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Revoke an API token")]
    [EndpointDescription(
        "Deletes the token immediately - anything using it starts getting 401 on its next call. There is no " +
        "grace period and no undo, so a revoked token cannot be restored; issue a new one. Your other tokens " +
        "and your password are unaffected.")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var row = await _db.ApiAccessTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);
        if (row is null) return NotFound();
        _db.ApiAccessTokens.Remove(row);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
