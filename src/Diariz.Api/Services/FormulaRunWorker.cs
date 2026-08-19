using Diariz.Api.Services.Llm;
using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.Services;

/// <summary>Consumes the formula-run Redis stream and runs each job through
/// <see cref="FormulaRunProcessor"/>. Singleton (BackgroundService) - a fresh DI scope is created per job.
/// Mirrors <see cref="SectionSummaryWorker"/>.</summary>
public class FormulaRunWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly FormulaRunOptions _opts;
    private readonly string _publicUrl;
    private readonly ILogger<FormulaRunWorker> _log;

    public FormulaRunWorker(
        IServiceScopeFactory scopes, IConnectionMultiplexer redis, IHubContext<TranscriptionHub> hub,
        IOptions<FormulaRunOptions> opts, IOptions<AppPublicOptions> appOpts, ILogger<FormulaRunWorker> log)
    {
        _scopes = scopes;
        _redis = redis;
        _hub = hub;
        _opts = opts.Value;
        _publicUrl = appOpts.Value.PublicUrl;
        _log = log;
    }

    /// <summary>Recovers jobs orphaned when a previous instance was killed mid-job.</summary>
    private readonly StreamReclaimer _reclaimer = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();
        await EnsureGroupAsync(db);
        _log.LogInformation("FormulaRunWorker listening on stream {Stream}", _opts.StreamKey);

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
                _log.LogError(ex, "Error reading the formula-run stream");
                await Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            if (entries.Length == 0)
            {
                // Idle is the moment to pick up anything an instance that was killed mid-job left behind.
                entries = await _reclaimer.ReclaimDueAsync(
                    db, _opts.StreamKey, _opts.ConsumerGroup, _opts.ConsumerName, _log,
                    onAbandoned: e => AbandonEntryAsync(e, stoppingToken));
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
                JsonSerializer.Deserialize<FormulaRunJob>((string)payload!) is { } job)
            {
                using var scope = _scopes.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var chat = scope.ServiceProvider.GetRequiredService<IChatStreamClient>();
                var resolver = scope.ServiceProvider.GetRequiredService<ILlmSettingsResolver>();
                var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookPublisher>();
                using var jobTx = JobTelemetry.Begin("formula-run");
                await FormulaRunProcessor.ProcessAsync(
                    ctx, chat, resolver, _hub, job, _log, webhooks, _publicUrl, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error processing formula-run entry {Id}", entry.Id);
        }
        finally
        {
            await db.StreamAcknowledgeAsync(_opts.StreamKey, _opts.ConsumerGroup, entry.Id);
        }
    }

    /// <summary>Settles the result row behind a message the queue has given up on. Without this the drop is
    /// silent and the card sits on "Generating..." for good.
    ///
    /// <para>Passing the shutdown token on is deliberate. If it cancels, this throws, the reclaimer's ack
    /// never runs, and the message stays pending to be abandoned again on the next pass - so the result is
    /// settled by whichever instance gets there, rather than lost with the entry.</para></summary>
    private async Task AbandonEntryAsync(StreamEntry entry, CancellationToken ct)
    {
        var payload = entry["job"];
        if (!payload.HasValue ||
            JsonSerializer.Deserialize<FormulaRunJob>((string)payload!) is not { } job) return;

        using var scope = _scopes.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        await FormulaRunProcessor.AbandonAsync(ctx, _hub, job, _log, ct);
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
