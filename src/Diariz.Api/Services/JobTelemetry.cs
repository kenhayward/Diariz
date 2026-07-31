namespace Diariz.Api.Services;

/// <summary>Wraps one background job in a Sentry transaction.
///
/// WHY THIS IS NEEDED. Sentry's ASP.NET integration creates a transaction per incoming HTTP request and
/// nothing else. A <see cref="BackgroundService"/> has no request, so it had no transaction - and a span with
/// no parent transaction is dropped by the SDK. That is why <see cref="LlmTelemetryHandler"/> alone is not
/// enough: without this, every summary, minutes run, tag extraction, action extraction, embedding batch and
/// formula run would open a span into nothing.
///
/// It is also what finally gives a formula run an end-to-end time. The HTTP endpoint that starts one reports
/// only the enqueue (~132 ms measured); the model call it kicks off happens here.
///
/// Deliberately a plain static rather than an injected abstraction: it must be usable from the middle of an
/// existing worker loop without changing that worker's constructor, and it is a no-op when telemetry is off.
/// </summary>
public static class JobTelemetry
{
    /// <summary>Sentry's convention for queue-consumer transactions.</summary>
    public const string Op = "queue.task";

    public static async Task TraceAsync(string name, Func<Task> job)
    {
        await TraceAsync(name, async () => { await job(); return 0; });
    }

    /// <summary>Scoped form: <c>using var _ = JobTelemetry.Begin("summarize");</c> at the top of a job body.
    ///
    /// Preferred inside the existing worker loops because it is a single inserted line - wrapping their
    /// multi-line processor calls in a lambda instead would mean re-balancing parentheses in eight files for
    /// no behavioural gain.
    ///
    /// Note the transaction finishes as Ok even if the job throws: <see cref="IDisposable.Dispose"/> cannot
    /// see the in-flight exception. That is acceptable here because every one of these workers already
    /// catches its own failure and records it (which reaches Sentry as an event in its own right); what was
    /// missing, and what this restores, is the timing.</summary>
    public static IDisposable Begin(string name) => new JobScope(name);

    private sealed class JobScope : IDisposable
    {
        private readonly IDisposable _scope;
        private readonly ITransactionTracer _transaction;

        public JobScope(string name)
        {
            _scope = SentrySdk.PushScope();
            _transaction = SentrySdk.StartTransaction(name, Op);
            SentrySdk.ConfigureScope(s => s.Transaction = _transaction);
        }

        public void Dispose()
        {
            _transaction.Finish(SpanStatus.Ok);
            _scope.Dispose();
        }
    }

    public static async Task<T> TraceAsync<T>(string name, Func<Task<T>> job)
    {
        // Push a scope so the transaction below is visible to SentrySdk.GetSpan() for the duration of this
        // job only - these workers process jobs in a loop on a long-lived task, so binding to the ambient
        // scope would leak one job's transaction into the next.
        using var scope = SentrySdk.PushScope();

        var transaction = SentrySdk.StartTransaction(name, Op);
        SentrySdk.ConfigureScope(s => s.Transaction = transaction);

        try
        {
            var result = await job();
            transaction.Finish(SpanStatus.Ok);
            return result;
        }
        catch (Exception ex)
        {
            // Finish and rethrow untouched. Every caller catches its own failure to decide whether to XACK
            // (avoiding poison-message loops), so swallowing or wrapping here would change job semantics.
            transaction.Finish(ex);
            throw;
        }
    }
}
