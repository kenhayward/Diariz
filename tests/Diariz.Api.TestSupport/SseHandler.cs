using System.Net;
using System.Text;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Serves a canned <c>text/event-stream</c> response one chunk at a time, so a client that is
/// supposed to stream can be told apart from one that is not.
///
/// <see cref="CapturingHandler"/> cannot do this job: it hands back a fully-buffered
/// <see cref="StringContent"/>, against which a buffering client and a streaming client behave
/// identically. Anything that measures time-to-first-token needs the first bytes to arrive genuinely
/// before the last ones, which is what the per-chunk delay here provides.</summary>
public sealed class SseHandler : HttpMessageHandler
{
    private readonly List<(byte[] Data, TimeSpan Delay)> _chunks = [];
    private readonly HttpStatusCode _status;
    private readonly string? _errorBody;

    /// <summary>A streaming 200. Each <c>data:</c> payload becomes one chunk; <paramref name="delayBefore"/>
    /// applies to the chunk at that index, so a delay before the LAST chunk separates first-token time from
    /// total duration by a knowable margin.</summary>
    public SseHandler(IEnumerable<string> dataPayloads, TimeSpan delayBefore = default, int delayAtIndex = -1)
    {
        _status = HttpStatusCode.OK;
        var i = 0;
        foreach (var payload in dataPayloads)
        {
            _chunks.Add((
                Encoding.UTF8.GetBytes($"data: {payload}\n\n"),
                i == delayAtIndex ? delayBefore : TimeSpan.Zero));
            i++;
        }
    }

    /// <summary>A non-2xx response with a plain body, the shape an endpoint uses to reject a parameter.</summary>
    public SseHandler(HttpStatusCode status, string errorBody)
    {
        _status = status;
        _errorBody = errorBody;
    }

    public string? LastBodyRaw { get; private set; }
    public Uri? LastRequestUri { get; private set; }
    public string? LastAuthorization { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        LastRequestUri = request.RequestUri;
        LastAuthorization = request.Headers.Authorization?.ToString();
        if (request.Content is not null) LastBodyRaw = await request.Content.ReadAsStringAsync(ct);

        if (_errorBody is not null)
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_errorBody, Encoding.UTF8, "application/json"),
            };

        var content = new StreamContent(new ChunkedStream(_chunks));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(_status) { Content = content };
    }

    /// <summary>Hands back one queued chunk per read, after that chunk's delay. A short read is legal on any
    /// stream, so a caller that assumes otherwise is broken regardless of this class.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Queue<(byte[] Data, TimeSpan Delay)> _queue;

        public ChunkedStream(IEnumerable<(byte[] Data, TimeSpan Delay)> chunks) => _queue = new(chunks);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_queue.Count == 0) return 0;
            var (data, delay) = _queue.Dequeue();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);

            var n = Math.Min(data.Length, buffer.Length);
            data.AsSpan(0, n).CopyTo(buffer.Span);
            // Re-queue whatever did not fit, with no further delay - it is the same chunk.
            if (n < data.Length) _queue.Enqueue((data[n..], TimeSpan.Zero));
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
