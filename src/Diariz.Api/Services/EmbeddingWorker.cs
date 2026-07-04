using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Domain;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.Services;

/// <summary>
/// Consumes the embedding Redis stream and runs each job through <see cref="EmbeddingProcessor"/>.
/// Singleton (BackgroundService) - a fresh DI scope (scoped DbContext/client/resolver) per job. Always
/// listens (per-user endpoints mean embedding can be configured even with no server default); the processor
/// no-ops when the owner has no endpoint. XACKs even on failure so a poison message can't loop.
/// </summary>
public class EmbeddingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly EmbeddingOptions _opts;
    private readonly ILogger<EmbeddingWorker> _log;

    public EmbeddingWorker(
        IServiceScopeFactory scopes, IConnectionMultiplexer redis,
        IOptions<EmbeddingOptions> opts, ILogger<EmbeddingWorker> log)
    {
        _scopes = scopes;
        _redis = redis;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();
        await EnsureGroupAsync(db);
        _log.LogInformation("EmbeddingWorker listening on stream {Stream}", _opts.StreamKey);

        while (!stoppingToken.IsCancellationRequested)
        {
            StreamEntry[] entries;
            try
            {
                entries = await db.StreamReadGroupAsync(
                    _opts.StreamKey, _opts.ConsumerGroup, _opts.ConsumerName, ">", count: 1);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error reading the embedding stream");
                await Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            if (entries.Length == 0)
            {
                await Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            foreach (var entry in entries)
                await HandleEntryAsync(db, entry, stoppingToken);
        }
    }

    private async Task HandleEntryAsync(IDatabase db, StreamEntry entry, CancellationToken ct)
    {
        try
        {
            var payload = entry["job"];
            if (payload.HasValue &&
                JsonSerializer.Deserialize<EmbeddingJob>((string)payload!) is { } job)
            {
                using var scope = _scopes.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var client = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();
                var resolver = scope.ServiceProvider.GetRequiredService<IEmbeddingSettingsResolver>();
                await EmbeddingProcessor.ProcessAsync(ctx, client, resolver, job, _log, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error processing embedding entry {Id}", entry.Id);
        }
        finally
        {
            // Ack even on failure: reprocessing a poison message would loop forever. Chunks are refreshed on
            // the next (re)transcription anyway.
            await db.StreamAcknowledgeAsync(_opts.StreamKey, _opts.ConsumerGroup, entry.Id);
        }
    }

    private async Task EnsureGroupAsync(IDatabase db)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(_opts.StreamKey, _opts.ConsumerGroup, "0", createStream: true);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Group already exists — fine.
        }
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (TaskCanceledException) { /* shutting down */ }
    }
}
