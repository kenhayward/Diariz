using StackExchange.Redis;

namespace Diariz.Api.Services;

/// <summary>Takes over stream messages left pending by an API instance that was killed mid-job.
///
/// <para>Every stream worker acks in a <c>finally</c>, which covers a job that <em>throws</em> but not one
/// whose process <em>dies</em> - a container restart, an OOM. The message then stays in the consumer
/// group's pending list, and since the workers only ever read <c>"&gt;"</c> (new messages) nothing looks at
/// it again: the recording sits in Summarizing (or Transcribing, on the Python worker's stream) forever,
/// with no error and no way forward but a manual re-run.</para>
///
/// <para>One instance of this is held per worker, so the throttle is per-stream.</para></summary>
public sealed class StreamReclaimer
{
    /// <summary>How long a message must have been idle before it is considered abandoned.
    ///
    /// <para>An in-flight job's message looks idle for as long as the job takes, so this has to clear the
    /// longest run the API can legitimately produce or a reclaim would steal work from a healthy instance
    /// and duplicate it. That length is set by the LLM timeout, which is <em>not</em> a platform-wide
    /// constant: it resolves per call as the recording owner's <c>UserSettings.LlmTimeoutSeconds</c> ??
    /// <c>PlatformSettings.LlmTimeoutSeconds</c> ?? the server option, so any user can put their own jobs
    /// past the platform default - and because the timeout is an inactivity allowance reset per streamed
    /// chunk, not a cap on the whole call, even that value is a floor rather than a ceiling on the run.</para>
    ///
    /// <para>Half an hour therefore replaces the old ten minutes: it clears the slow-local-model timeouts
    /// this is really about (a user who sets 900s to let a large local model answer) with a wide margin. It
    /// is still only a margin. If a user's timeout exceeds it, their message can be reclaimed while the call
    /// is genuinely in flight and the LLM call runs a second time - duplicated work and a duplicated charge,
    /// bounded by <see cref="MaxDeliveries"/> and then abandoned. Deliberately not wired to the per-user
    /// setting: that would couple queue hygiene to user preferences to shave a rare duplicate. Note there is
    /// no operator escape hatch either - every worker constructs <c>new StreamReclaimer()</c>, and the
    /// constructor's thresholds exist only for tests - so changing this constant is the only lever.</para>
    ///
    /// <para>The Python transcription worker has no such margin available - a long recording runs for tens
    /// of minutes - which is why it refreshes its own claim while working instead of relying on a
    /// threshold.</para></summary>
    public static readonly TimeSpan DefaultMinIdle = TimeSpan.FromMinutes(30);

    /// <summary>How often a worker bothers to look. The idle branch runs about once a second per worker
    /// across eight workers, and checking every time would add a constant XPENDING drumbeat to a
    /// completely idle deployment for no benefit.</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>Deliveries after which a message is abandoned rather than retried.</summary>
    public const int MaxDeliveries = 3;

    private DateTimeOffset? _lastCheck;

    /// <summary>Both thresholds are constructor-injected purely so tests can drive the real code path
    /// without waiting ten minutes; production always uses the defaults.</summary>
    public StreamReclaimer(TimeSpan? minIdle = null, TimeSpan? checkInterval = null)
    {
        MinIdle = minIdle ?? DefaultMinIdle;
        CheckInterval = checkInterval ?? DefaultCheckInterval;
    }

    public TimeSpan MinIdle { get; }
    public TimeSpan CheckInterval { get; }

    /// <summary>Whether a message that has been delivered this many times should be dropped instead of
    /// retried. Reclaiming reintroduces exactly the poison-message loop that acking-in-finally exists to
    /// prevent: a job that kills the process would be handed straight to its replacement, forever. Past
    /// the cap the likelier explanation is that the message is the cause of the deaths, not a casualty.</summary>
    public static bool ShouldAbandon(long deliveryCount) => deliveryCount > MaxDeliveries;

    /// <summary>Whether enough time has passed to look again. The first call is always due - a restart is
    /// the most likely moment to find an orphan, so recovery must not sit behind the interval.</summary>
    public bool IsDue(DateTimeOffset now)
    {
        if (_lastCheck is { } last && now - last < CheckInterval) return false;
        _lastCheck = now;
        return true;
    }

    /// <summary>Claim any abandoned messages, returning them to be processed as though freshly read.
    /// Returns empty when not due, when nothing is stale, or when Redis is unhappy - recovery is
    /// opportunistic and must never take down the loop it is helping.</summary>
    /// <param name="onAbandoned">Invoked with a message about to be dropped past the delivery cap, before
    /// it is acked. Dropping the entry is the right call, but on its own it is silent: whatever the job was
    /// for is left in whatever in-flight state the enqueue put it in, with nothing left to move it on. The
    /// drop belongs here; what the drop <em>means</em> belongs to the worker, which is the only thing that
    /// knows how to read the payload. Its failure aborts this pass, deliberately - the message stays
    /// pending and is abandoned again on the next check, so the notification retries rather than being
    /// lost with the entry.</param>
    public async Task<StreamEntry[]> ReclaimDueAsync(
        IDatabase db, string streamKey, string group, string consumer, ILogger log, DateTimeOffset? now = null,
        Func<StreamEntry, Task>? onAbandoned = null)
    {
        if (!IsDue(now ?? DateTimeOffset.UtcNow)) return [];

        try
        {
            var pending = await db.StreamPendingMessagesAsync(
                streamKey, group, count: 10, consumerName: RedisValue.Null);
            if (pending.Length == 0) return [];

            var claim = new List<RedisValue>();
            foreach (var p in pending)
            {
                if (p.IdleTimeInMilliseconds < MinIdle.TotalMilliseconds) continue;

                if (ShouldAbandon(p.DeliveryCount))
                {
                    log.LogError(
                        "Abandoning {Id} on {Stream} after {Count} deliveries - it is more likely the cause "
                        + "of the failures than a casualty of them", p.MessageId, streamKey, p.DeliveryCount);
                    if (onAbandoned is not null)
                    {
                        // XPENDING carries no payload, so read the entry back before the ack - the handler
                        // needs the job to know which recording to settle. Notify first, ack second: an ack
                        // that landed first would drop the only record of the job on a failed handler.
                        var entry = await db.StreamRangeAsync(streamKey, p.MessageId, p.MessageId, 1);
                        if (entry.Length > 0) await onAbandoned(entry[0]);
                    }
                    await db.StreamAcknowledgeAsync(streamKey, group, p.MessageId);
                    continue;
                }

                log.LogWarning(
                    "Reclaiming {Id} on {Stream}, abandoned by {Consumer} after {Idle}ms idle",
                    p.MessageId, streamKey, p.ConsumerName, p.IdleTimeInMilliseconds);
                claim.Add(p.MessageId);
            }

            return claim.Count == 0
                ? []
                : await db.StreamClaimAsync(streamKey, group, consumer,
                    minIdleTimeInMs: (long)MinIdle.TotalMilliseconds, messageIds: [.. claim]);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not reclaim pending messages on {Stream}", streamKey);
            return [];
        }
    }
}
