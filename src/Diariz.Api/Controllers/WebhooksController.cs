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

/// <summary>Management API for a user's outbound webhook subscriptions ("Automations"): CRUD, a manual test
/// ping, and the delivery log. Every action is gated on <see cref="PlatformSettings.WebhooksEnabled"/> and
/// scoped to the caller's own subscriptions.</summary>
[ApiController]
[Authorize]
[Route("api/user/webhooks")]
public class WebhooksController : ControllerBase
{
    public const int MaxPerUser = 20;

    private readonly DiarizDbContext _db;
    private readonly IPlatformSettingsService _platform;
    private readonly IWebhookSecretProtector _protector;
    private readonly IWebhookUrlValidator _urls;

    public WebhooksController(DiarizDbContext db, IPlatformSettingsService platform,
        IWebhookSecretProtector protector, IWebhookUrlValidator urls)
    { _db = db; _platform = platform; _protector = protector; _urls = urls; }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<bool> EnabledAsync() => (await _platform.GetAsync()).WebhooksEnabled;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WebhookSubscriptionDto>>> List()
    {
        if (!await EnabledAsync()) return Forbid();
        var rows = await _db.Webhooks
            .Where(s => s.OwnerUserId == UserId && s.Scope == WebhookScope.Personal)
            .OrderByDescending(s => s.CreatedAt).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<WebhookCreatedDto>> Create(CreateWebhookRequest req)
    {
        if (!await EnabledAsync()) return Forbid();
        var invalid = Validate(req.Url, req.EventTypes, out var events, out var reason);
        if (invalid is null && !(await _urls.ValidateAsync(req.Url)).Ok) reason = "That address is not allowed.";
        if (reason is not null) return BadRequest(reason);
        if (await _db.Webhooks.CountAsync(s => s.OwnerUserId == UserId) >= MaxPerUser)
            return BadRequest("Automation limit reached. Delete one before adding another.");

        var secret = "dz_whsec_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = UserId, Scope = WebhookScope.Personal,
            Name = string.IsNullOrWhiteSpace(req.Name) ? "Automation" : req.Name.Trim(),
            Url = req.Url.Trim(), SecretEncrypted = _protector.Protect(secret)!, EventTypes = WebhookEventTypes.Join(events),
        };
        _db.Webhooks.Add(row);
        await _db.SaveChangesAsync();
        return new WebhookCreatedDto(row.Id, row.Name, row.Url, events, secret);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WebhookSubscriptionDto>> Update(Guid id, UpdateWebhookRequest req)
    {
        if (!await EnabledAsync()) return Forbid();
        var row = await _db.Webhooks.FirstOrDefaultAsync(
            s => s.Id == id && s.OwnerUserId == UserId && s.Scope == WebhookScope.Personal);
        if (row is null) return NotFound();
        var invalid = Validate(req.Url, req.EventTypes, out var events, out var reason);
        if (invalid is null && !(await _urls.ValidateAsync(req.Url)).Ok) reason = "That address is not allowed.";
        if (reason is not null) return BadRequest(reason);

        row.Name = string.IsNullOrWhiteSpace(req.Name) ? "Automation" : req.Name.Trim();
        row.Url = req.Url.Trim();
        row.EventTypes = WebhookEventTypes.Join(events);
        if (req.IsActive && !row.IsActive) { row.ConsecutiveFailures = 0; row.DisabledReason = null; }
        row.IsActive = req.IsActive;
        await _db.SaveChangesAsync();
        return ToDto(row);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!await EnabledAsync()) return Forbid();
        var row = await _db.Webhooks.FirstOrDefaultAsync(
            s => s.Id == id && s.OwnerUserId == UserId && s.Scope == WebhookScope.Personal);
        if (row is null) return NotFound();
        _db.Webhooks.Remove(row);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> SendTest(Guid id)
    {
        if (!await EnabledAsync()) return Forbid();
        var row = await _db.Webhooks.FirstOrDefaultAsync(
            s => s.Id == id && s.OwnerUserId == UserId && s.Scope == WebhookScope.Personal);
        if (row is null) return NotFound();
        var eventId = "evt_" + Guid.NewGuid().ToString("N");
        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = row.Id, EventId = eventId, EventType = WebhookEventTypes.Ping,
            PayloadJson = WebhookPayload.Build(eventId, WebhookEventTypes.Ping, DateTimeOffset.UtcNow,
                new { message = "This is a test event from Diariz." }),
            Status = WebhookDeliveryStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();
        return Accepted();
    }

    [HttpGet("{id:guid}/deliveries")]
    public async Task<ActionResult<IReadOnlyList<WebhookDeliveryDto>>> Deliveries(Guid id)
    {
        if (!await EnabledAsync()) return Forbid();
        if (!await _db.Webhooks.AnyAsync(s => s.Id == id && s.OwnerUserId == UserId && s.Scope == WebhookScope.Personal))
            return NotFound();
        var rows = await _db.WebhookDeliveries.Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt).Take(50).ToListAsync();
        return Ok(rows.Select(d => new WebhookDeliveryDto(
            d.Id, d.EventType, d.Status.ToString(), d.AttemptCount, d.ResponseStatus, d.LastError,
            d.CreatedAt, d.NextAttemptAt)).ToList());
    }

    private static string? Validate(string url, string[] types, out string[] events, out string? reason)
    {
        events = (types ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if (string.IsNullOrWhiteSpace(url)) { reason = "A destination URL is required."; return reason; }
        if (events.Length == 0) { reason = "Choose at least one event."; return reason; }
        if (events.Any(t => !WebhookEventTypes.Subscribable.Contains(t)))
        { reason = "Unknown event type."; return reason; }
        reason = null; return null;
    }

    private static WebhookSubscriptionDto ToDto(WebhookSubscription s) => new(
        s.Id, s.Name, s.Url, WebhookEventTypes.Split(s.EventTypes), s.IsActive, s.ConsecutiveFailures,
        s.DisabledReason, s.LastDeliveryAt, s.LastStatus, s.CreatedAt);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
