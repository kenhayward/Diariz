namespace Diariz.Api.Services;

/// <summary>A read-only pass-through <see cref="Stream"/> that reports what flows through it without
/// buffering any of it.
///
/// WHY THIS EXISTS: a streamed LLM response finishes long after <c>SendAsync</c> returns, because the client
/// reads with <c>ResponseHeadersRead</c>. Anything measured at <c>SendAsync</c> - duration especially - is
/// therefore time-to-headers, not the call. Wrapping the body lets the telemetry record be completed when the
/// stream actually ends.
///
/// It must NEVER buffer: buffering an SSE body would hold every chat token until the model finished, leaving
/// the UI silent. Bytes are forwarded to the caller as they arrive and merely shown to the observer.
///
/// Every observer callback is guarded. Telemetry must not be able to break the answer it is measuring, and a
/// throw from here would land in the middle of someone's streamed reply.
///
/// Only <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> and <see cref="Read(byte[], int, int)"/> are
/// overridden. Every other read path on <see cref="Stream"/> - <see cref="CopyTo"/>, <see cref="CopyToAsync"/>,
/// the legacy Task-based <c>ReadAsync(byte[], int, int, CancellationToken)</c>, <c>ReadByte</c>, and the APM
/// <c>BeginRead</c>/<c>EndRead</c> pair - are, in the current BCL, all implemented in terms of one of these two
/// primitives, so nothing can reach <see cref="_inner"/> unobserved. See task-4-report.md for how that was
/// verified.</summary>
public sealed class ObservingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<ReadOnlyMemory<byte>> _onBytes;
    private readonly Action _onFirstByte;
    private readonly Action _onCompleted;

    private int _completed;
    private bool _sawFirstByte;

    public ObservingStream(
        Stream inner, Action<ReadOnlyMemory<byte>> onBytes, Action onFirstByte, Action onCompleted)
    {
        _inner = inner;
        _onBytes = onBytes;
        _onFirstByte = onFirstByte;
        _onCompleted = onCompleted;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, ct);
        }
        catch
        {
            Complete();
            throw; // the caller's failure is theirs; we only note that the stream ended
        }

        Observe(buffer, read);
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read;
        try
        {
            read = _inner.Read(buffer, offset, count);
        }
        catch
        {
            Complete();
            throw;
        }

        Observe(buffer.AsMemory(offset, count), read);
        return read;
    }

    private void Observe(Memory<byte> buffer, int read)
    {
        if (read <= 0)
        {
            Complete();
            return;
        }

        Guard(() =>
        {
            if (!_sawFirstByte)
            {
                _sawFirstByte = true;
                _onFirstByte();
            }
            _onBytes(buffer[..read]);
        });
    }

    /// <summary>Fires the completion callback at most once, whichever of end-of-stream, dispose or a fault
    /// happens first. A reader that stops at <c>[DONE]</c> never reaches end-of-stream, so dispose has to
    /// count - otherwise an abandoned turn would never be recorded at all.</summary>
    private void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Guard(_onCompleted);
    }

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Deliberately swallowed. See the class comment: this runs mid-answer.
        }
    }

    // Both disposal paths funnel through Dispose(bool) so _inner is disposed exactly once no matter which
    // entry point the caller uses: `Dispose(false)` here (after the inner stream is already disposed
    // asynchronously) deliberately skips the `if (disposing) _inner.Dispose()` branch below. Complete() is
    // idempotent, so the redundant call from that shared path is harmless.
    protected override void Dispose(bool disposing)
    {
        Complete();
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        Dispose(false);
        GC.SuppressFinalize(this);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
