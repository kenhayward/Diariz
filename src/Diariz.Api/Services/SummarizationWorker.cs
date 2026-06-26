using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.Services;

/// <summary>
/// Consumes the summarisation Redis stream and runs each job through <see cref="SummarizationProcessor"/>.
/// Singleton (BackgroundService) — a fresh DI scope (and scoped DbContext/client) is created per job.
/// </summary>
public class SummarizationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly SummarizationOptions _opts;
    private readonly ILogger<SummarizationWorker> _log;

    public SummarizationWorker(
        IServiceScopeFactory scopes, IConnectionMultiplexer redis, IHubContext<TranscriptionHub> hub,
        IOptions<SummarizationOptions> opts, ILogger<SummarizationWorker> log)
    {
        _scopes = scopes;
        _redis = redis;
        _hub = hub;
        _opts = opts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.Enabled)
        {
            _log.LogWarning(
                "Summarization is disabled (no Summarization:ApiBase configured); SummarizationWorker is idle.");
            return;
        }

        var db = _redis.GetDatabase();
        await EnsureGroupAsync(db);
        _log.LogInformation("SummarizationWorker listening on stream {Stream}", _opts.StreamKey);

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
                _log.LogError(ex, "Error reading the summarization stream");
                await Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            // StackExchange.Redis has no blocking read; poll with a short delay when idle.
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
                JsonSerializer.Deserialize<SummarizationJob>((string)payload!) is { } job)
            {
                using var scope = _scopes.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var client = scope.ServiceProvider.GetRequiredService<ISummarizationClient>();
                await SummarizationProcessor.ProcessAsync(ctx, client, _hub, _opts.Model, job, _log, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error processing summarization entry {Id}", entry.Id);
        }
        finally
        {
            // Ack even on failure: the processor already records a Failed status, and reprocessing
            // a poison message would loop forever.
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
