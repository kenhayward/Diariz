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
/// Consumes the meeting-minutes Redis stream and runs each job through <see cref="MeetingMinutesProcessor"/>.
/// Singleton (BackgroundService) — a fresh DI scope (and scoped DbContext/client) is created per job. Mirrors
/// <see cref="SummarizationWorker"/>; the two run independently on separate streams.
/// </summary>
public class MeetingMinutesWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly MeetingMinutesOptions _opts;
    private readonly string _publicUrl;
    private readonly ILogger<MeetingMinutesWorker> _log;

    public MeetingMinutesWorker(
        IServiceScopeFactory scopes, IConnectionMultiplexer redis, IHubContext<TranscriptionHub> hub,
        IOptions<MeetingMinutesOptions> opts, IOptions<AppPublicOptions> appOpts, ILogger<MeetingMinutesWorker> log)
    {
        _scopes = scopes;
        _redis = redis;
        _hub = hub;
        _opts = opts.Value;
        _publicUrl = appOpts.Value.PublicUrl;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Always listen: even with no server-default endpoint, individual users may configure their own
        // (per-user settings). The generate endpoint still gates enqueue on the effective config.
        var db = _redis.GetDatabase();
        await EnsureGroupAsync(db);
        _log.LogInformation("MeetingMinutesWorker listening on stream {Stream}", _opts.StreamKey);

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
                _log.LogError(ex, "Error reading the meeting-minutes stream");
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
                JsonSerializer.Deserialize<MeetingMinutesJob>((string)payload!) is { } job)
            {
                using var scope = _scopes.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var generator = scope.ServiceProvider.GetRequiredService<IMeetingTypeMinutesGenerator>();
                var resolver = scope.ServiceProvider.GetRequiredService<ISummarizationSettingsResolver>();
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookPublisher>();
                await MeetingMinutesProcessor.ProcessAsync(
                    ctx, generator, resolver, _hub, queue, job, _opts.TranscriptCharBudget, _log,
                    webhooks, _publicUrl, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error processing meeting-minutes entry {Id}", entry.Id);
        }
        finally
        {
            // Ack even on failure: reprocessing a poison message would loop forever.
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
