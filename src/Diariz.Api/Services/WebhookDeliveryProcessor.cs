using Diariz.Api.Configuration;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Delivers due <see cref="WebhookDelivery"/> rows: signs + POSTs the stored body, then records the
/// outcome, schedules a retry with backoff on failure, and auto-disables a subscription after the failure
/// threshold. Takes `now` explicitly for deterministic tests.</summary>
public sealed class WebhookDeliveryProcessor
{
    private readonly IWebhookSecretProtector _protector;
    private readonly WebhookOptions _opts;
    private readonly ILogger<WebhookDeliveryProcessor> _log;

    public WebhookDeliveryProcessor(
        IWebhookSecretProtector protector, IOptions<WebhookOptions> opts, ILogger<WebhookDeliveryProcessor> log)
    { _protector = protector; _opts = opts.Value; _log = log; }

    public async Task ProcessDueAsync(DiarizDbContext db, HttpClient http, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.WebhookDeliveries
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(_opts.BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        foreach (var d in due)
        {
            if (ct.IsCancellationRequested) break; // shutting down; stop starting new deliveries

            var sub = await db.Webhooks.FirstOrDefaultAsync(s => s.Id == d.SubscriptionId, ct);
            if (sub is null) { d.Status = WebhookDeliveryStatus.Failed; continue; } // orphan; cascade should prevent

            d.AttemptCount++;
            int? responseStatus = null;
            string? error = null;
            try
            {
                var secret = _protector.Unprotect(sub.SecretEncrypted) ?? "";
                var ts = now.ToUnixTimeSeconds();
                using var req = new HttpRequestMessage(HttpMethod.Post, sub.Url)
                {
                    Content = new StringContent(d.PayloadJson, System.Text.Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("webhook-id", d.EventId);
                req.Headers.TryAddWithoutValidation("webhook-timestamp", ts.ToString());
                req.Headers.TryAddWithoutValidation("webhook-signature", WebhookSigner.Sign(secret, d.EventId, ts, d.PayloadJson));
                using var resp = await http.SendAsync(req, ct);
                responseStatus = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode) error = $"HTTP {responseStatus}";
            }
            // Shutdown cancelled the in-flight request (not a genuine failure/timeout - HttpClient timeouts throw
            // TaskCanceledException that is NOT linked to `ct`, so those still fall through to the catch below and
            // correctly count). Revert the attempt and leave the row untouched for the next run.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                d.AttemptCount--;
                break;
            }
            catch (Exception ex) { error = ex.Message; }

            d.ResponseStatus = responseStatus;
            d.LastError = error;
            sub.LastDeliveryAt = now;
            sub.LastStatus = error ?? "Delivered";

            if (error is null)
            {
                d.Status = WebhookDeliveryStatus.Delivered;
                sub.ConsecutiveFailures = 0;
            }
            else if (d.AttemptCount >= WebhookBackoff.MaxAttempts)
            {
                d.Status = WebhookDeliveryStatus.Failed;
                sub.ConsecutiveFailures++;
                if (sub.ConsecutiveFailures >= _opts.AutoDisableThreshold)
                {
                    sub.IsActive = false;
                    sub.DisabledReason = $"Auto-disabled after {sub.ConsecutiveFailures} consecutive failures.";
                }
            }
            else
            {
                d.NextAttemptAt = now.Add(WebhookBackoff.NextDelay(d.AttemptCount));
            }
        }
        // Persist with a non-cancelled token: deliveries completed earlier in this batch must not be lost just
        // because a later one was interrupted by shutdown.
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
