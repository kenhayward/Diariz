using Diariz.Domain;

namespace Diariz.Api.Services;

/// <summary>Polls the webhook delivery table and dispatches due deliveries. Postgres-backed (not Redis) so that
/// scheduled retries and a durable delivery history come for free.</summary>
public sealed class WebhookDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WebhookDeliveryWorker> _log;

    public WebhookDeliveryWorker(
        IServiceScopeFactory scopes, IHttpClientFactory http, ILogger<WebhookDeliveryWorker> log)
    { _scopes = scopes; _http = http; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var processor = scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>();
                var client = _http.CreateClient("webhooks");
                await processor.ProcessDueAsync(db, client, DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex) { _log.LogError(ex, "Webhook delivery tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (TaskCanceledException) { /* shutting down */ }
        }
    }
}
