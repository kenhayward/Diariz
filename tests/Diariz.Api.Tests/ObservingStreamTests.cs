using System.Text;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ObservingStreamTests
{
    private static ObservingStream Wrap(
        Stream inner, List<byte> seen, Action? onFirst = null, Action? onDone = null) =>
        new(inner, b => seen.AddRange(b.ToArray()), onFirst ?? (() => { }), onDone ?? (() => { }));

    [Fact]
    public async Task ForwardsEveryByte_Unchanged()
    {
        var payload = Encoding.UTF8.GetBytes("hello streamed world");
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream(payload), seen);

        using var output = new MemoryStream();
        await s.CopyToAsync(output);

        Assert.Equal(payload, output.ToArray()); // the caller still gets everything
        Assert.Equal(payload, seen.ToArray());   // and the observer saw everything
    }

    [Fact]
    public async Task ReportsTheFirstByteOnce_NotOncePerRead()
    {
        var firstByteCalls = 0;
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream(Encoding.UTF8.GetBytes("abcdef")), seen,
            onFirst: () => firstByteCalls++);

        var buffer = new byte[2];
        while (await s.ReadAsync(buffer) > 0) { }

        Assert.Equal(1, firstByteCalls);
    }

    [Fact]
    public async Task DoesNotReportAFirstByte_ForAnEmptyStream()
    {
        var firstByteCalls = 0;
        var done = 0;
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream([]), seen,
            onFirst: () => firstByteCalls++, onDone: () => done++);

        Assert.Equal(0, await s.ReadAsync(new byte[4]));
        Assert.Equal(0, firstByteCalls);
        // An empty response is still a completed call - it must still produce a usage row.
        Assert.Equal(1, done);
    }

    [Fact]
    public async Task CompletesOnEndOfStream()
    {
        var done = 0;
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream(Encoding.UTF8.GetBytes("abc")), seen, onDone: () => done++);

        var buffer = new byte[8];
        while (await s.ReadAsync(buffer) > 0) { }

        Assert.Equal(1, done);
    }

    [Fact]
    public async Task CompletesOnDispose_WhenTheReaderAbandonsTheStreamEarly()
    {
        // A chat client that stops at [DONE], or a browser that disconnects mid-answer, never reaches EOF.
        // Without this the record for an abandoned turn would never be written at all.
        var done = 0;
        var seen = new List<byte>();
        var s = Wrap(new MemoryStream(Encoding.UTF8.GetBytes("abcdefghij")), seen, onDone: () => done++);

        // CA2022 (avoid inexact read) fires here: the byte count is deliberately unused - this test is
        // about abandoning the stream partway through, not about how many bytes that first read returned.
#pragma warning disable CA2022
        await s.ReadAsync(new byte[2]);
#pragma warning restore CA2022
        await s.DisposeAsync();

        Assert.Equal(1, done);
    }

    [Fact]
    public async Task CompletesExactlyOnce_EvenWhenReadToEndAndThenDisposed()
    {
        var done = 0;
        var seen = new List<byte>();
        var s = Wrap(new MemoryStream(Encoding.UTF8.GetBytes("abc")), seen, onDone: () => done++);

        var buffer = new byte[8];
        while (await s.ReadAsync(buffer) > 0) { }
        await s.DisposeAsync();

        Assert.Equal(1, done);
    }

    [Fact]
    public async Task CompletesOnFault_AndStillLetsTheExceptionThrough()
    {
        var done = 0;
        var seen = new List<byte>();
        await using var s = Wrap(new ThrowingStream(), seen, onDone: () => done++);

        // CA2022 fires here too: the point of the test is the thrown IOException, not the (never-returned)
        // byte count.
#pragma warning disable CA2022
        await Assert.ThrowsAsync<IOException>(async () => await s.ReadAsync(new byte[4]));
#pragma warning restore CA2022
        Assert.Equal(1, done);
    }

    [Fact]
    public async Task AnObserverThatThrows_DoesNotBreakTheStream()
    {
        // Telemetry must never break the call it measures - least of all mid-answer.
        var payload = Encoding.UTF8.GetBytes("abcdef");
        await using var s = new ObservingStream(
            new MemoryStream(payload), _ => throw new InvalidOperationException("observer blew up"),
            () => throw new InvalidOperationException("first-byte blew up"),
            () => throw new InvalidOperationException("completion blew up"));

        using var output = new MemoryStream();
        var ex = await Record.ExceptionAsync(() => s.CopyToAsync(output));

        Assert.Null(ex);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task OnFirstByteThrowing_DoesNotSuppressOnBytes_ForTheSameChunk()
    {
        // Regression: Observe() used to wrap onFirstByte and onBytes in a single Guard(...), so a throw
        // from onFirstByte propagated out of the lambda before onBytes(buffer[..read]) was ever reached -
        // silently dropping that chunk from whatever consumes onBytes. Task 5 feeds onBytes into
        // SseUsageScanner to find the trailing usage chunk, so a throwing first-byte hook would silently
        // drop bytes from the scan for that response. The two callbacks must be guarded independently.
        var payload = Encoding.UTF8.GetBytes("abcdef");
        var seen = new List<byte>();
        await using var s = new ObservingStream(
            new MemoryStream(payload), b => seen.AddRange(b.ToArray()),
            () => throw new InvalidOperationException("first-byte blew up"),
            () => { });

        using var output = new MemoryStream();
        await s.CopyToAsync(output);

        Assert.Equal(payload, seen.ToArray());
    }

    [Fact]
    public void ForwardsEveryByte_ViaSynchronousCopyTo()
    {
        // Judgement-question spot check: Stream.CopyTo's default implementation is documented to funnel
        // through Read(byte[], int, int), not the async override - confirm nothing reaches the inner
        // stream unobserved via that path either.
        var payload = Encoding.UTF8.GetBytes("hello streamed world");
        var seen = new List<byte>();
        using var s = Wrap(new MemoryStream(payload), seen);

        using var output = new MemoryStream();
        s.CopyTo(output);

        Assert.Equal(payload, output.ToArray());
        Assert.Equal(payload, seen.ToArray());
    }

    [Fact]
    public async Task DoesNotBuffer_ObserverSeesChunkOneBeforeTheInnerStreamServesChunkTwo()
    {
        // Constraint #1 for this whole class: it must never buffer. ForwardsEveryByte_Unchanged only
        // checks aggregate totals after the copy completes - an implementation that buffered the entire
        // inner stream and replayed it at the end would pass that test identically. This test asserts
        // ORDERING instead: the observer must see chunk 1 before the inner stream is even asked to serve
        // chunk 2. A buffering implementation drains every inner read first and only then reports, so the
        // recorded event order would differ and this test would fail.
        var events = new List<string>();
        var inner = new SequencedReadStream(events, Encoding.UTF8.GetBytes("chunk1"), Encoding.UTF8.GetBytes("chunk2"));
        await using var s = new ObservingStream(
            inner,
            b => events.Add($"onBytes:{Encoding.UTF8.GetString(b.Span)}"),
            () => { },
            () => { });

        var buffer = new byte[16];
        while (await s.ReadAsync(buffer) > 0) { }

        var firstOnBytes = events.FindIndex(e => e.StartsWith("onBytes:", StringComparison.Ordinal));
        var secondInnerRead = events.IndexOf("innerRead:2");

        Assert.True(firstOnBytes >= 0 && secondInnerRead >= 0, $"expected both events; got: {string.Join(", ", events)}");
        Assert.True(firstOnBytes < secondInnerRead,
            $"expected onBytes for chunk 1 before the inner stream's 2nd read; got: {string.Join(", ", events)}");
    }

    /// <summary>Serves each chunk from a separate <see cref="ReadAsync"/> call and records, in <paramref
    /// name="events"/>, exactly when each inner read is served - so a test can assert the interleaving
    /// between inner reads and observer callbacks, not just the aggregate result.</summary>
    private sealed class SequencedReadStream(List<string> events, params byte[][] chunks) : Stream
    {
        private int _index;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            _index++;
            events.Add($"innerRead:{_index}");
            if (_index > chunks.Length) return ValueTask.FromResult(0);

            var chunk = chunks[_index - 1];
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            throw new IOException("boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
