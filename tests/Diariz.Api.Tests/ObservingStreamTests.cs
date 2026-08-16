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
        var seen = new List<byte>();
        await using var s = Wrap(new MemoryStream([]), seen, onFirst: () => firstByteCalls++);

        Assert.Equal(0, await s.ReadAsync(new byte[4]));
        Assert.Equal(0, firstByteCalls);
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
    public void ForwardsEveryByte_ViaSynchronousCopyTo()
    {
        // Judgement-question spot check (task-4-report.md): Stream.CopyTo's default implementation is
        // documented to funnel through Read(byte[], int, int), not the async override - confirm nothing
        // reaches the inner stream unobserved via that path either.
        var payload = Encoding.UTF8.GetBytes("hello streamed world");
        var seen = new List<byte>();
        using var s = Wrap(new MemoryStream(payload), seen);

        using var output = new MemoryStream();
        s.CopyTo(output);

        Assert.Equal(payload, output.ToArray());
        Assert.Equal(payload, seen.ToArray());
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
