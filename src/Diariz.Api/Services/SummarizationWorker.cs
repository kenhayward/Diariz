using Diariz.Api.Services.Llm;
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
    private readonly IPromptTemplateProvider _prompts;
    private readonly SummarizationOptions _opts;
    private readonly string _publicUrl;
    private readonly ILogger<SummarizationWorker> _log;

    public SummarizationWorker(
        IServiceScopeFactory scopes, IConnectionMultiplexer redis, IHubContext<TranscriptionHub> hub,
        IPromptTemplateProvider prompts, IOptions<SummarizationOptions> opts,
        IOptions<AppPublicOptions> appOpts, ILogger<SummarizationWorker> log)
    {
        _scopes = scopes;
        _redis = redis;
        _hub = hub;
        _prompts = prompts;
        _opts = opts.Value;
        _publicUrl = appOpts.Value.PublicUrl;
        _log = log;
    }

    /// <summary>Recovers jobs orphaned when a previous instance was killed mid-job.</summary>
    private readonly StreamReclaimer _reclaimer = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Always listen: even with no server-default endpoint, individual users may configure their own
        // (per-user settings). The Summarize endpoint still gates enqueue on the effective config.
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
                JsonSerializer.Deserialize<SummarizationJob>((string)payload!) is { } job)
            {
                using var scope = _scopes.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                var client = scope.ServiceProvider.GetRequiredService<ISummarizationClient>();
                var resolver = scope.ServiceProvider.GetRequiredService<ILlmSettingsResolver>();
                var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookPublisher>();
                // Read the (editable) template per job so edits apply without an API restart.
                var template = _prompts.Get("summarise", SummarizationPrompt.DefaultTemplate);
                using var jobTx = JobTelemetry.Begin("summarize");
                await SummarizationProcessor.ProcessAsync(
                    ctx, client, resolver, _hub, job, template, _log, webhooks, _publicUrl, ct);
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

    /// <summary>Settles the recording behind a message the queue has given up on. Without this the drop is
    /// silent: the recording keeps the Summarizing the enqueue gave it, the Summarize endpoint no-ops while
    /// that is set, and the only way out is a re-transcribe.
    ///
    /// <para>Passing the shutdown token on is deliberate. If it cancels, this throws, the reclaimer's ack
    /// never runs, and the message stays pending to be abandoned again on the next pass - so the recording
    /// is settled by whichever instance gets there, rather than lost with the entry.</para></summary>
    private async Task AbandonEntryAsync(StreamEntry entry, CancellationToken ct)
    {
        var payload = entry["job"];
        if (!payload.HasValue ||
            JsonSerializer.Deserialize<SummarizationJob>((string)payload!) is not { } job) return;

        using var scope = _scopes.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
        await SummarizationProcessor.AbandonAsync(ctx, _hub, job, _log, ct);
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
