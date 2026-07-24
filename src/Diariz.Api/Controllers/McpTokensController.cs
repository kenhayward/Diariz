using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Manages the signed-in user's MCP personal access tokens (used by Claude to connect to their own
/// transcripts over <c>/mcp</c>). Authenticated with the normal JWT session; the tokens it mints are a
/// separate credential. The plaintext token is returned only once, at generation — only its hash is stored.</summary>
[ApiController]
[Authorize]
[Route("api/user/mcp-tokens")]
public class McpTokensController : ControllerBase
{
    /// <summary>A generous per-user cap so a runaway client can't create unbounded tokens.</summary>
    public const int MaxTokensPerUser = 20;

    private readonly DiarizDbContext _db;
    private readonly IMcpTokenService _tokens;

    public McpTokensController(DiarizDbContext db, IMcpTokenService tokens)
    {
        _db = db;
        _tokens = tokens;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    [EndpointSummary("List your MCP tokens")]
    [EndpointDescription(
        "Your MCP personal access tokens, newest first - the ones that let Claude Desktop or Claude Code " +
        "reach your meetings. Shows the name, a short prefix, and when each was last used. **The token itself " +
        "is never returned**; only a hash is stored.\n\n" +
        "These are separate from REST API tokens: a `dz_mcp_` token is rejected on `/api/*` and a `dz_api_` " +
        "token on `/mcp`. The claude.ai web connector uses OAuth instead and needs no token here.")]
    public async Task<IReadOnlyList<McpTokenDto>> List() =>
        await _db.McpAccessTokens
            .Where(t => t.UserId == UserId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new McpTokenDto(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt))
            .ToListAsync();

    [HttpPost]
    [EndpointSummary("Create an MCP token")]
    [EndpointDescription(
        "Mints a `dz_mcp_...` token to paste into Claude Desktop or Claude Code. **The token value is " +
        "returned exactly once, in this response** - store it now, because only a hash is kept.\n\n" +
        "Unlike REST API tokens these carry no scope or expiry: an MCP token grants the full tool surface " +
        "over your own meetings until you revoke it. 400 when you have reached the per-user limit.")]
    public async Task<ActionResult<McpTokenCreatedDto>> Create(CreateMcpTokenRequest req)
    {
        var name = string.IsNullOrWhiteSpace(req.Name) ? "MCP token" : req.Name.Trim();
        if (name.Length > 128) name = name[..128];

        if (await _db.McpAccessTokens.CountAsync(t => t.UserId == UserId) >= MaxTokensPerUser)
            return BadRequest("Token limit reached. Revoke an existing token before creating another.");

        var g = _tokens.Generate();
        var row = new McpAccessToken
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = name,
            TokenHash = g.Hash,
            Prefix = g.Prefix,
        };
        _db.McpAccessTokens.Add(row);
        await _db.SaveChangesAsync();

        // The plaintext token is returned exactly once — it is never persisted or retrievable again.
        return new McpTokenCreatedDto(row.Id, row.Name, row.Prefix, g.Token);
    }

    [HttpDelete("{id:guid}")]
    [EndpointSummary("Revoke an MCP token")]
    [EndpointDescription(
        "Deletes the token immediately - the Claude client using it loses access on its next call and will " +
        "need a new one pasted in. No undo. Your other tokens, and any claude.ai OAuth connection, are " +
        "unaffected.")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var row = await _db.McpAccessTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);
        if (row is null) return NotFound();
        _db.McpAccessTokens.Remove(row);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
