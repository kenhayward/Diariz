using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Diariz.Api.Services;

/// <summary>Deletes usage rows past the retention window. Set-based (ExecuteDeleteAsync) because this
/// table gets a row per LLM call and loading a month of them to delete them would be absurd.</summary>
public static class LlmUsageRetentionSweep
{
    public static async Task<int> RunAsync(
        DiarizDbContext db, DateTimeOffset nowUtc, int retentionDays, ILogger logger,
        CancellationToken ct = default)
    {
        // 0 means keep forever. Without this guard it would mean "delete everything older than now",
        // which destroys the log the first night after an admin types 0 meaning "no limit".
        if (retentionDays <= 0) return 0;

        var cutoff = nowUtc.AddDays(-retentionDays).ToUniversalTime();
        var deleted = await db.LlmCalls.Where(c => c.StartedAt < cutoff).ExecuteDeleteAsync(ct);

        if (deleted > 0)
            logger.LogInformation("LLM usage retention: deleted {Deleted} row(s) older than {Days}d.",
                deleted, retentionDays);
        return deleted;
    }
}

/// <summary>Runs the sweep once a day at the same server-local time as the audio-retention job, reusing
/// its schedule helper. Opens its own DI scope per run because the host is a singleton.</summary>
public class LlmUsageRetentionWorker(IServiceProvider services, ILogger<LlmUsageRetentionWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeOnly timeOfDay;
            try
            {
                using var scope = services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<IPlatformSettingsService>();
                timeOfDay = (await settings.GetAsync(stoppingToken)).AudioDeletionTimeOfDay;
            }
            catch (Exception e)
            {
                logger.LogError(e, "LLM usage retention: could not read settings; retrying in 1h.");
                if (!await Delay(TimeSpan.FromHours(1), stoppingToken)) return;
                continue;
            }

            var next = AudioRetentionSchedule.NextRun(DateTimeOffset.Now, timeOfDay);
            if (!await Delay(next - DateTimeOffset.Now, stoppingToken)) return;

            try
            {
                using var scope = services.CreateScope();
                var settings = await scope.ServiceProvider
                    .GetRequiredService<IPlatformSettingsService>().GetAsync(stoppingToken);
                var db = scope.ServiceProvider.GetRequiredService<DiarizDbContext>();
                await LlmUsageRetentionSweep.RunAsync(
                    db, DateTimeOffset.UtcNow, settings.LlmUsageRetentionDays, logger, stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "LLM usage retention sweep failed.");
            }
        }
    }

    /// <summary>Cancellation-aware delay; returns false if the wait was cancelled (host shutting down).
    /// Mirrors <see cref="AudioRetentionWorker"/>'s helper: clamping a negative span (e.g. a scheduled time
    /// that has already elapsed by the time Delay runs) avoids an ArgumentOutOfRangeException that a plain
    /// try/catch around TaskCanceledException would not catch and would otherwise crash this loop.</summary>
    private static async Task<bool> Delay(TimeSpan delay, CancellationToken ct)
    {
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
