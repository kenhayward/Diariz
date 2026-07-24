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

    /// <summary>Recheck interval a delivery is deferred by when it is paced back by the per-subscription rate cap.
    /// The one-minute counting window (not this value) sets the effective ceiling; this just controls how soon a
    /// paced delivery is reconsidered as capacity frees up.</summary>
    private static readonly TimeSpan PaceDelay = TimeSpan.FromSeconds(15);

    public async Task ProcessDueAsync(DiarizDbContext db, HttpClient http, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.WebhookDeliveries
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(_opts.BatchSize)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        // Per-subscription rolling-minute rate cap: how many deliveries has each subscription in this batch already
        // been contacted for in the last minute? Combined with a running per-pass tally, this keeps a single
        // fan-out target (a platform automation) under MaxPerSubscriptionPerMinute.
        var windowStart = now.AddMinutes(-1);
        var subIds = due.Select(d => d.SubscriptionId).Distinct().ToList();
        var recent = (await db.WebhookDeliveries
                .Where(x => subIds.Contains(x.SubscriptionId) && x.LastAttemptAt >= windowStart)
                .GroupBy(x => x.SubscriptionId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.Key, x => x.Count);
        var sentThisPass = new Dictionary<Guid, int>();

        foreach (var d in due)
        {
            if (ct.IsCancellationRequested) break; // shutting down; stop starting new deliveries

            var sub = await db.Webhooks.FirstOrDefaultAsync(s => s.Id == d.SubscriptionId, ct);
            if (sub is null) { d.Status = WebhookDeliveryStatus.Failed; continue; } // orphan; cascade should prevent

            // Rate cap: if this subscription already hit its per-minute quota, pace this delivery forward without
            // contacting the endpoint or consuming a retry attempt.
            var used = recent.GetValueOrDefault(d.SubscriptionId) + sentThisPass.GetValueOrDefault(d.SubscriptionId);
            if (used >= _opts.MaxPerSubscriptionPerMinute)
            {
                d.NextAttemptAt = now.Add(PaceDelay);
                continue;
            }
            sentThisPass[d.SubscriptionId] = sentThisPass.GetValueOrDefault(d.SubscriptionId) + 1;

            d.AttemptCount++;
            int? responseStatus = null;
            string? error = null;
            TimeSpan? retryAfter = null;
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
                if (responseStatus == 429)
                    retryAfter = ParseRetryAfter(resp.Headers.RetryAfter, now);
                else if (!resp.IsSuccessStatusCode)
                    error = $"HTTP {responseStatus}";
            }
            // Shutdown cancelled the in-flight request (not a genuine failure/timeout - HttpClient timeouts throw
            // TaskCanceledException that is NOT linked to `ct`, so those still fall through to the catch below and
            // correctly count). Revert the attempt and leave the row untouched for the next run.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                d.AttemptCount--;
                sentThisPass[d.SubscriptionId]--;
                break;
            }
            catch (Exception ex) { error = ex.Message; }

            d.LastAttemptAt = now;
            d.ResponseStatus = responseStatus;

            // Throttled: the endpoint asked us to slow down. Honor Retry-After and treat it as neither a failure
            // (no ConsecutiveFailures, no auto-disable) nor a consumed retry attempt.
            if (responseStatus == 429)
            {
                d.AttemptCount--;
                d.LastError = null;
                d.NextAttemptAt = now.Add(retryAfter ?? TimeSpan.FromSeconds(_opts.RetryAfterFallbackSeconds));
                sub.LastDeliveryAt = now;
                sub.LastStatus = "Throttled (429)";
                continue;
            }

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

    /// <summary>Resolve a 429 <c>Retry-After</c> header (delta-seconds or an HTTP-date) to a delay; null if absent.</summary>
    private static TimeSpan? ParseRetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date) { var d = date - now; return d > TimeSpan.Zero ? d : TimeSpan.Zero; }
        return null;
    }
}
