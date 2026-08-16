using System.Threading.Channels;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Pure draining helper, separated from the hosted service so batching can be tested without a
/// host or a database - the same separation as pipeline._shape_segments in the worker.</summary>
public static class LlmUsageBatch
{
    /// <summary>Waits for at least one record, then takes up to <paramref name="max"/> without waiting
    /// for more. Returns an empty list when the channel completes.</summary>
    public static async Task<List<LlmCall>> DrainAsync(
        ChannelReader<LlmCall> reader, int max, CancellationToken ct)
    {
        var batch = new List<LlmCall>();
        if (!await reader.WaitToReadAsync(ct)) return batch;

        while (batch.Count < max && reader.TryRead(out var call)) batch.Add(call);
        return batch;
    }
}

/// <summary>Persists recorded LLM calls off the call path.
///
/// Opens its OWN DI scope per batch. The handler cannot hold a DbContext: it is registered transient but
/// HttpClientFactory pools handler instances for about two minutes, so an injected scoped dependency
/// would be captive and eventually used after disposal.
///
/// The LlmUsageLoggingEnabled switch is enforced HERE rather than in the handler, so the call path never
/// pays for a settings lookup. Records made while logging is off are drained and discarded.</summary>
public class LlmUsageWriter(
    ChannelLlmUsageSink sink, IServiceProvider services, ILogger<LlmUsageWriter> logger)
    : BackgroundService
{
    private const int MaxBatch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            List<LlmCall> batch;
            try
            {
                batch = await LlmUsageBatch.DrainAsync(sink.Reader, MaxBatch, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // host shutting down
            }

            if (batch.Count == 0) continue;

            try
            {
                using var scope = services.CreateScope();
                var settings = await scope.ServiceProvider
                    .GetRequiredService<IPlatformSettingsService>().GetAsync(stoppingToken);
                if (!settings.LlmUsageLoggingEnabled) continue; // drained and discarded

                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                foreach (var call in batch)
                {
                    // Npgsql rejects a non-zero offset bound to timestamptz, and the in-memory provider
                    // will not catch it - so normalise here rather than trusting every producer.
                    call.StartedAt = call.StartedAt.ToUniversalTime();
                    call.CompletedAt = call.CompletedAt.ToUniversalTime();
                }
                db.LlmCalls.AddRange(batch);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception e)
            {
                // A failed write must never take the writer down: the next batch should still get a
                // chance, and nothing here may affect the calls being measured.
                logger.LogWarning(e, "LLM usage: could not persist {Count} record(s).", batch.Count);
            }
        }
    }
}
