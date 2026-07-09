using System.Text.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Hubs;
using Diariz.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.Services;

/// <summary>Consumes the folder-minutes Redis stream and runs each job through
/// <see cref="SectionMinutesProcessor"/>. Singleton (BackgroundService) - a fresh DI scope is created per job.
/// Mirrors <see cref="MeetingMinutesWorker"/>.</summary>
public class SectionMinutesWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly IPromptTemplateProvider _prompts;
    private readonly SectionMinutesOptions _opts;
    private readonly MeetingMinutesOptions _minutesOpts;
    private readonly ILogger<SectionMinutesWorker> _log;

    public SectionMinutesWorker(
        IServiceScopeFactory scopes, IConnectionMultiplexer redis, IHubContext<TranscriptionHub> hub,
        IPromptTemplateProvider prompts, IOptions<SectionMinutesOptions> opts,
        IOptions<MeetingMinutesOptions> minutesOpts, ILogger<SectionMinutesWorker> log)
    {
        _scopes = scopes;
        _redis = redis;
        _hub = hub;
        _prompts = prompts;
        _opts = opts.Value;
        _minutesOpts = minutesOpts.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();
        await EnsureGroupAsync(db);
        _log.LogInformation("SectionMinutesWorker listening on stream {Stream}", _opts.StreamKey);

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
                _log.LogError(ex, "Error reading the section-minutes stream");
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
                JsonSerializer.Deserialize<SectionMinutesJob>((string)payload!) is { } job)
            {
                using var scope = _scopes.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var generator = scope.ServiceProvider.GetRequiredService<IMeetingTypeMinutesGenerator>();
                var combiner = scope.ServiceProvider.GetRequiredService<IMeetingMinutesClient>();
                var resolver = scope.ServiceProvider.GetRequiredService<ISummarizationSettingsResolver>();
                var folderTemplate = _prompts.Get(FolderMinutesPrompt.TemplateName, FolderMinutesPrompt.DefaultTemplate);
                await SectionMinutesProcessor.ProcessAsync(
                    ctx, generator, combiner, resolver, _hub, folderTemplate,
                    job, _minutesOpts.TranscriptCharBudget, _opts.CombineCharBudget, _log, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error processing section-minutes entry {Id}", entry.Id);
        }
        finally
        {
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
