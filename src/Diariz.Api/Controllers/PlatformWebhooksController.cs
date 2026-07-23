using System.Security.Claims;
using System.Security.Cryptography;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Management API for admin-owned, signal-routed Platform webhook subscriptions (Phase 3
/// "Workflow Signals"): CRUD only - no manual test ping or delivery log (see <see cref="WebhooksController"/>
/// for a user's own Personal subscriptions). Every action is gated on
/// <see cref="PlatformSettings.WebhooksEnabled"/>. Unlike <see cref="WebhooksController"/>, reads/writes are
/// NOT owner-scoped: any Platform Administrator can manage any Platform subscription, since these route by
/// Workflow Signal across all users rather than belonging to one.</summary>
[ApiController]
[Authorize(Policy = "ManagePlatform")]
[Route("api/admin/webhooks")]
public class PlatformWebhooksController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IPlatformSettingsService _platform;
    private readonly IWebhookSecretProtector _protector;
    private readonly IWebhookUrlValidator _urls;

    public PlatformWebhooksController(DiarizDbContext db, IPlatformSettingsService platform,
        IWebhookSecretProtector protector, IWebhookUrlValidator urls)
    { _db = db; _platform = platform; _protector = protector; _urls = urls; }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> EnabledAsync() => (await _platform.GetAsync()).WebhooksEnabled;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WebhookSubscriptionDto>>> List()
    {
        if (!await EnabledAsync()) return Forbid();
        var rows = await _db.Webhooks
            .Where(s => s.Scope == WebhookScope.Platform)
            .OrderByDescending(s => s.CreatedAt).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<WebhookCreatedDto>> Create(CreatePlatformWebhookRequest req)
    {
        if (!await EnabledAsync()) return Forbid();
        var invalid = Validate(req.Url, req.EventTypes, req.SignalFilter, out var events, out var signals, out var reason);
        if (invalid is null && !(await _urls.ValidateAsync(req.Url)).Ok) reason = "That address is not allowed.";
        if (reason is not null) return BadRequest(reason);

        var secret = "dz_whsec_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = UserId, Scope = WebhookScope.Platform,
            Name = string.IsNullOrWhiteSpace(req.Name) ? "Automation" : req.Name.Trim(),
            Url = req.Url.Trim(), SecretEncrypted = _protector.Protect(secret)!, EventTypes = WebhookEventTypes.Join(events),
            SignalFilter = WebhookSignals.Join(signals),
        };
        _db.Webhooks.Add(row);
        await _db.SaveChangesAsync();
        return new WebhookCreatedDto(row.Id, row.Name, row.Url, events, secret);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WebhookSubscriptionDto>> Update(Guid id, UpdatePlatformWebhookRequest req)
    {
        if (!await EnabledAsync()) return Forbid();
        var row = await _db.Webhooks.FirstOrDefaultAsync(s => s.Id == id && s.Scope == WebhookScope.Platform);
        if (row is null) return NotFound();
        var invalid = Validate(req.Url, req.EventTypes, req.SignalFilter, out var events, out var signals, out var reason);
        if (invalid is null && !(await _urls.ValidateAsync(req.Url)).Ok) reason = "That address is not allowed.";
        if (reason is not null) return BadRequest(reason);

        row.Name = string.IsNullOrWhiteSpace(req.Name) ? "Automation" : req.Name.Trim();
        row.Url = req.Url.Trim();
        row.EventTypes = WebhookEventTypes.Join(events);
        row.SignalFilter = WebhookSignals.Join(signals);
        if (req.IsActive && !row.IsActive) { row.ConsecutiveFailures = 0; row.DisabledReason = null; }
        row.IsActive = req.IsActive;
        await _db.SaveChangesAsync();
        return ToDto(row);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!await EnabledAsync()) return Forbid();
        var row = await _db.Webhooks.FirstOrDefaultAsync(s => s.Id == id && s.Scope == WebhookScope.Platform);
        if (row is null) return NotFound();
        _db.Webhooks.Remove(row);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string? Validate(
        string url, string[] types, string[] signalFilter, out string[] events, out string[] signals, out string? reason)
    {
        events = (types ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        signals = (signalFilter ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(url)) { reason = "A destination URL is required."; return reason; }
        if (events.Length == 0) { reason = "Choose at least one event."; return reason; }
        if (events.Any(t => !WebhookEventTypes.Subscribable.Contains(t)))
        { reason = "Unknown event type."; return reason; }
        if (signals.Length == 0)
        { reason = "Choose at least one signal - a platform automation with no signal never fires."; return reason; }
        reason = null; return null;
    }

    private static WebhookSubscriptionDto ToDto(WebhookSubscription s) => new(
        s.Id, s.Name, s.Url, WebhookEventTypes.Split(s.EventTypes), s.IsActive, s.ConsecutiveFailures,
        s.DisabledReason, s.LastDeliveryAt, s.LastStatus, s.CreatedAt,
        Scope: "Platform", SignalFilter: WebhookSignals.Split(s.SignalFilter));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
