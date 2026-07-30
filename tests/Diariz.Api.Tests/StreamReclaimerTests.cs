using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The decision logic behind orphaned-job recovery. The Redis calls themselves are covered by
/// <c>StreamReclaimIntegrationTests</c> against a real server; what is unit-tested here is <em>when</em>
/// a reclaim is attempted and <em>which</em> messages are abandoned rather than retried - the two rules
/// that decide whether recovery is a fix or a footgun.</summary>
public class StreamReclaimerTests
{
    [Fact]
    public void Abandons_a_message_delivered_more_times_than_the_cap()
    {
        // Reclaiming reintroduces the poison-message loop that acking-in-finally exists to prevent: a job
        // that kills the worker gets handed to the next one, forever. Past the cap it is dropped instead.
        Assert.False(StreamReclaimer.ShouldAbandon(1));
        Assert.False(StreamReclaimer.ShouldAbandon(StreamReclaimer.MaxDeliveries));
        Assert.True(StreamReclaimer.ShouldAbandon(StreamReclaimer.MaxDeliveries + 1));
    }

    [Fact]
    public void First_check_is_due_immediately()
    {
        // Startup is the most likely moment to find an orphan (the API was just restarted), so the first
        // idle poll must not sit behind the throttle interval.
        var reclaimer = new StreamReclaimer();
        Assert.True(reclaimer.IsDue(new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Throttles_repeat_checks_so_an_idle_worker_does_not_poll_redis_every_second()
    {
        // The idle branch runs about once a second, per worker, across eight workers. Without this the
        // recovery path would add ~8 XPENDING calls a second to a completely idle deployment.
        var reclaimer = new StreamReclaimer();
        var t0 = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

        Assert.True(reclaimer.IsDue(t0));
        Assert.False(reclaimer.IsDue(t0.AddSeconds(1)));
        Assert.False(reclaimer.IsDue(t0 + reclaimer.CheckInterval - TimeSpan.FromSeconds(1)));
        Assert.True(reclaimer.IsDue(t0 + reclaimer.CheckInterval));
    }

    [Fact]
    public void Idle_threshold_clears_the_longest_job_the_api_can_legitimately_run()
    {
        // An in-flight job's message looks idle for as long as it takes. If the threshold were shorter
        // than a legitimate run, a healthy worker's job would be stolen and duplicated. The API's LLM
        // calls are capped by PlatformSettings.LlmTimeoutSeconds (default 120s), so 10 minutes is a wide
        // margin. (The Python worker cannot rely on a margin - transcription runs for tens of minutes -
        // so it refreshes its claim while working instead.)
        Assert.True(StreamReclaimer.DefaultMinIdle >= TimeSpan.FromMinutes(5));
        Assert.Equal(StreamReclaimer.DefaultMinIdle, new StreamReclaimer().MinIdle);
    }
}
