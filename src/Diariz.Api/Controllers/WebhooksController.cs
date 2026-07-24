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
    [EndpointSummary("List your automations")]
    [EndpointDescription(
        "Your outbound webhook subscriptions, newest first, with the events each is subscribed to, whether it " +
        "is active, and its recent delivery health.\n\n" +
        "Only your **personal** automations - platform-wide ones wired to Workflow Signals by an " +
        "administrator are not listed here. The whole section returns 403 when the platform's Automations " +
        "toggle is off; check `webhooksEnabled` on your profile before showing any of it.")]
    public async Task<ActionResult<IReadOnlyList<WebhookSubscriptionDto>>> List()
    {
        if (!await EnabledAsync()) return Forbid();
        var rows = await _db.Webhooks
            .Where(s => s.OwnerUserId == UserId && s.Scope == WebhookScope.Personal)
            .OrderByDescending(s => s.CreatedAt).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost]
    [EndpointSummary("Create an automation")]
    [EndpointDescription(
        "Registers a URL to be called when the events you choose happen - a recording created, transcribed or " +
        "failed, a formula finished or failed. Point it at a Zapier or n8n webhook trigger, or your own " +
        "endpoint.\n\n" +
        "**The signing secret (`dz_whsec_...`) is returned exactly once, in this response.** Store it now: it " +
        "is encrypted at rest and never returned again. Deliveries are signed over it, so verify the " +
        "signature rather than trusting the payload.\n\n" +
        "The URL must be reachable and public - internal, loopback and cloud-metadata addresses are refused, " +
        "so an automation cannot be aimed at the server's own network. 400 for a bad or disallowed URL, no " +
        "events, an unknown event type, or reaching the per-user limit.")]
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
    [EndpointSummary("Edit an automation")]
    [EndpointDescription(
        "Changes the name, destination URL, subscribed events, or active flag. The event list **replaces** " +
        "the old one rather than adding to it. A changed URL is re-validated against the same rules as " +
        "creation.\n\n" +
        "**Re-activating clears the failure count**, which is how you recover an automation that was " +
        "auto-paused after repeated failures - fix the endpoint, then set it active again. The signing secret " +
        "is never changed or returned here.")]
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
    [EndpointSummary("Delete an automation")]
    [EndpointDescription(
        "Removes the subscription and its delivery log; nothing further is sent. Its signing secret is gone " +
        "too, so re-creating the same URL later issues a **new** secret that your receiver must be updated " +
        "with. To stop deliveries reversibly, set it inactive instead.")]
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
    [EndpointSummary("Send a test event")]
    [EndpointDescription(
        "Queues a `ping` event to the automation's URL so you can confirm the endpoint is reachable and your " +
        "signature check works, without waiting for a real meeting.\n\n" +
        "Returns 202 - it is **queued, not sent inline**, and travels the same signed, retried delivery path " +
        "as a real event. Watch the delivery log for the outcome. It counts as a delivery, including toward " +
        "the failure count that auto-pauses a broken automation.")]
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
    [EndpointSummary("Read an automation's delivery log")]
    [EndpointDescription(
        "The **50 most recent** deliveries for one automation, newest first, with each event's type, status, " +
        "attempt count, the HTTP status your endpoint returned, the last error, and when the next retry is " +
        "due. This is the place to diagnose an automation that is not firing as expected.\n\n" +
        "Failed deliveries are retried with a growing backoff, so a `Pending` row with a future retry time is " +
        "normal rather than stuck. The payload bodies are not included.")]
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
        s.DisabledReason, s.LastDeliveryAt, s.LastStatus, s.CreatedAt,
        Scope: "Personal", SignalFilter: WebhookSignals.Split(s.SignalFilter));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
