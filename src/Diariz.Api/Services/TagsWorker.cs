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
/// Consumes the tag-extraction Redis stream and runs each job through <see cref="TagsProcessor"/>.
/// Singleton (BackgroundService) — a fresh DI scope (and scoped DbContext/client) is created per job.
/// Mirrors <see cref="ActionsWorker"/>; the summary, minutes, actions, and tags workers run independently
/// on separate streams (all part of the post-transcription pipeline).
/// </summary>
public class TagsWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConnectionMultiplexer _redis;
    private readonly IHubContext<TranscriptionHub> _hub;
    private readonly IPromptTemplateProvider _prompts;
    private readonly TagsOptions _opts;
    private readonly string _publicUrl;
    private readonly ILogger<TagsWorker> _log;

    public TagsWorker(
        IServiceScopeFactory scopes, IConnectionMultiplexer redis, IHubContext<TranscriptionHub> hub,
        IPromptTemplateProvider prompts, IOptions<TagsOptions> opts, IOptions<AppPublicOptions> appOpts,
        ILogger<TagsWorker> log)
    {
        _scopes = scopes;
        _redis = redis;
        _hub = hub;
        _prompts = prompts;
        _opts = opts.Value;
        _publicUrl = appOpts.Value.PublicUrl;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Always listen: even with no server-default endpoint, individual users may configure their own
        // (per-user settings). The processor gates the actual extraction on the effective config.
        var db = _redis.GetDatabase();
        await EnsureGroupAsync(db);
        _log.LogInformation("TagsWorker listening on stream {Stream}", _opts.StreamKey);

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
                _log.LogError(ex, "Error reading the tags stream");
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
                JsonSerializer.Deserialize<TagsJob>((string)payload!) is { } job)
            {
                using var scope = _scopes.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var client = scope.ServiceProvider.GetRequiredService<ITagsClient>();
                var resolver = scope.ServiceProvider.GetRequiredService<ISummarizationSettingsResolver>();
                var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookPublisher>();
                // Read the (editable) template per job so edits apply without an API restart.
                await TagsProcessor.ProcessAsync(
                    ctx, client, resolver, _hub, job,
                    _prompts.Get("tagcloud", TagsPrompt.DefaultTemplate), _log, webhooks, _publicUrl, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected error processing tags entry {Id}", entry.Id);
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
