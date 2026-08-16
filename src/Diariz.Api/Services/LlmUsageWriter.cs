using System.Threading.Channels;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Pure draining/persisting helpers, separated from the hosted service so batching and
/// persistence are both testable without a host or a real database - the same separation as
/// pipeline._shape_segments in the worker.</summary>
public static class LlmUsageBatch
{
    /// <summary>Waits for at least one record, then (if <paramref name="flushWindow"/> is positive) waits
    /// that long so records arriving in the window coalesce into the same batch, then takes up to
    /// <paramref name="max"/> without waiting for more. Returns an empty list when the channel completes
    /// or the initial wait is cancelled.</summary>
    public static async Task<List<LlmCall>> DrainAsync(
        ChannelReader<LlmCall> reader, int max, TimeSpan flushWindow, CancellationToken ct)
    {
        var batch = new List<LlmCall>();
        if (!await reader.WaitToReadAsync(ct)) return batch;

        if (flushWindow > TimeSpan.Zero) await Task.Delay(flushWindow, ct);

        while (batch.Count < max && reader.TryRead(out var call)) batch.Add(call);
        return batch;
    }

    /// <summary>Normalises timestamps and saves an already-drained batch, returning the number of rows
    /// saved. When <paramref name="loggingEnabled"/> is false the batch is discarded (0 is returned) -
    /// this is where the master switch is enforced, per <see cref="ILlmUsageSink"/>'s contract, so the
    /// call path never pays for a settings lookup. Extracted out of <see cref="LlmUsageWriter"/> so both
    /// the settings gate and the UTC normalisation are directly unit/integration-testable.</summary>
    public static async Task<int> PersistAsync(
        DiarizDbContext db, IReadOnlyList<LlmCall> batch, bool loggingEnabled, CancellationToken ct)
    {
        if (!loggingEnabled) return 0; // drained and discarded

        foreach (var call in batch)
        {
            // Npgsql rejects a non-zero offset bound to timestamptz, and the in-memory provider will not
            // catch it - so normalise here rather than trusting every producer.
            call.StartedAt = call.StartedAt.ToUniversalTime();
            call.CompletedAt = call.CompletedAt.ToUniversalTime();
        }
        db.LlmCalls.AddRange(batch);
        await db.SaveChangesAsync(ct);
        return batch.Count;
    }
}

/// <summary>Persists recorded LLM calls off the call path, flushing every ~2 seconds or 200 rows
/// (whichever comes first) so steady sub-max traffic is batched rather than paying a DI-scope +
/// settings-query + SaveChanges round trip per record.
///
/// Opens its OWN DI scope per batch. The handler cannot hold a DbContext: it is registered transient but
/// HttpClientFactory pools handler instances for about two minutes, so an injected scoped dependency
/// would be captive and eventually used after disposal.
///
/// The LlmUsageLoggingEnabled switch is enforced in <see cref="LlmUsageBatch.PersistAsync"/> rather than
/// in the handler, so the call path never pays for a settings lookup. Records made while logging is off
/// are drained and discarded.</summary>
public class LlmUsageWriter(
    ChannelLlmUsageSink sink, IServiceProvider services, ILogger<LlmUsageWriter> logger)
    : BackgroundService
{
    private const int MaxBatch = 200;
    private static readonly TimeSpan FlushWindow = TimeSpan.FromSeconds(2);

    // On a persist failure (e.g. the database is down), back off before the next iteration. DrainAsync
    // returns immediately once records are already buffered, so without this delay a sustained outage
    // would spin: scope-create + settings-query + doomed-save + log, over and over with no pause.
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);

    // Once a batch has left the channel (DrainAsync already removed it), it can no longer be recovered
    // there - so persisting it must survive shutdown starting mid-save, rather than losing up to MaxBatch
    // records to an ordinary stop. The settings read and the save therefore deliberately do NOT observe
    // stoppingToken; they share a short-lived token instead, which still caps how long a wedged database
    // (or a hung settings query) can hold up shutdown. Do not "fix" this back to stoppingToken.
    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            List<LlmCall> batch;
            try
            {
                batch = await LlmUsageBatch.DrainAsync(sink.Reader, MaxBatch, FlushWindow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // host shutting down, nothing drained yet
            }

            if (batch.Count == 0) continue;

            try
            {
                using var scope = services.CreateScope();
                using var persistCts = new CancellationTokenSource(PersistTimeout);

                var settings = await scope.ServiceProvider.GetRequiredService<IPlatformSettingsService>()
                    .GetAsync(persistCts.Token);
                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                await LlmUsageBatch.PersistAsync(db, batch, settings.LlmUsageLoggingEnabled, persistCts.Token);
            }
            catch (Exception e)
            {
                // A failed write must never take the writer down: the next batch should still get a
                // chance, and nothing here may affect the calls being measured.
                logger.LogWarning(e, "LLM usage: could not persist {Count} record(s).", batch.Count);
                try
                {
                    await Task.Delay(FailureBackoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return; // host shutting down during the backoff
                }
            }
        }
    }
}
