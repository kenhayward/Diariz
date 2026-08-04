using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>
/// The transaction wrapper exists so spans opened inside a BackgroundService have a parent - without one the
/// SDK drops them, which is why every LLM call made from a worker was invisible. These tests pin the part
/// that matters regardless of whether telemetry is configured: the job still runs, and failures still
/// propagate exactly as before.
/// </summary>
[Collection(SentryHubCollection.Name)]
public class JobTelemetryTests
{
    [Fact]
    public async Task RunsTheJob()
    {
        var ran = false;

        await JobTelemetry.TraceAsync("summarize", async () =>
        {
            await Task.Yield();
            ran = true;
        });

        Assert.True(ran);
    }

    [Fact]
    public async Task PropagatesFailures_Unchanged()
    {
        // Every one of these workers XACKs on failure to avoid poison-message loops, and decides that by
        // catching the exception itself. Swallowing or wrapping it here would silently change job semantics
        // - a telemetry wrapper must be invisible to the code it wraps.
        var boom = new InvalidOperationException("model refused");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => JobTelemetry.TraceAsync("summarize", () => throw boom));

        Assert.Same(boom, thrown);
    }

    [Fact]
    public async Task WorksWithTelemetryDisabled()
    {
        // The default state for most deployments: no DSN, so the SDK is disabled. The wrapper must be a
        // no-op rather than throwing on a null transaction.
        var ex = await Record.ExceptionAsync(() => JobTelemetry.TraceAsync("tags", () => Task.CompletedTask));
        Assert.Null(ex);
    }

    [Fact]
    public async Task ReturnsTheJobsResult()
    {
        var result = await JobTelemetry.TraceAsync("minutes", () => Task.FromResult(42));
        Assert.Equal(42, result);
    }
}
