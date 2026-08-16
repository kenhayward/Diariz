using System.Threading.Channels;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>Where a finished LLM call goes. An interface so the handler can be unit-tested without a
/// database - see FakeLlmUsageSink.
///
/// INVARIANT: <see cref="Record"/> MUST NOT throw. It is called from <c>LlmTelemetryHandler</c> on both
/// the success and the transport-failure path of every outbound LLM call, and the handler's entire design
/// is that a telemetry operation can never break the call it measures. The handler guards the call with a
/// try/catch as a last line of defence, but an implementation that can throw defeats the intent of that
/// guard existing at all - handle your own failures (log, drop, whatever) rather than propagating them.</summary>
public interface ILlmUsageSink
{
    void Record(LlmCall call);
}

/// <summary>Hands the record to a background writer through a bounded in-memory channel.
///
/// WHY NOT WRITE DIRECTLY: the handler runs on the LLM call path. An awaited insert there would add a
/// round-trip to every summary and would let a database problem degrade transcription and chat - turning
/// a monitoring feature into an availability risk.
///
/// The trade is that records still buffered during a hard crash are lost, and that a sustained burst
/// past Capacity drops the oldest rows. Both are acceptable for a usage log and neither can affect the
/// call being measured.</summary>
public sealed class ChannelLlmUsageSink : ILlmUsageSink
{
    public const int Capacity = 10_000;

    private long _dropped;
    private readonly Channel<LlmCall> _channel;

    public ChannelLlmUsageSink()
    {
        _channel = Channel.CreateBounded<LlmCall>(
            new BoundedChannelOptions(Capacity)
            {
                // Drop rather than block: TryWrite must always return immediately on the call path.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            // The channel is built in the constructor, not a field initialiser, because the
            // itemDropped callback closes over _dropped.
            itemDropped: _ => Interlocked.Increment(ref _dropped));
    }

    public ChannelReader<LlmCall> Reader => _channel.Reader;

    /// <summary>How many records have been dropped because the buffer was full. Surfaced so a persistent
    /// backlog is visible rather than silent.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Record(LlmCall call) => _channel.Writer.TryWrite(call);
}
