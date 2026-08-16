using System.Text;

namespace Diariz.Api.Services;

/// <summary>Incrementally scans an SSE byte stream for the final <c>usage</c> chunk that
/// <c>stream_options.include_usage</c> appends after the content.
///
/// Byte-oriented and incremental because a chunk WILL be split across socket reads - and split inside a
/// multi-byte UTF-8 sequence - so a line-oriented scanner would silently miss usage on exactly the long
/// responses whose cost matters most.
///
/// Pure and separable from the stream and the handler so it can be tested without either.
///
/// Retains NO content: the only state kept between feeds is a bounded partial line and the last parsed
/// token counts.</summary>
public sealed class SseUsageScanner
{
    /// <summary>Longest line retained before the scanner gives up on it and resynchronises at the next
    /// newline. A telemetry scanner must not let a hostile or broken endpoint exhaust memory.</summary>
    public const int MaxLineBytes = 64 * 1024;

    private readonly MemoryStream _line = new();
    private bool _skippingOverlongLine;

    /// <summary>Token counts from the most recent usage chunk seen, or null if none has appeared.</summary>
    public LlmUsage? Usage { get; private set; }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        try
        {
            foreach (var b in bytes)
            {
                if (b == (byte)'\n')
                {
                    if (!_skippingOverlongLine) TryParseLine();
                    _line.SetLength(0);
                    _skippingOverlongLine = false;
                    continue;
                }
                if (_skippingOverlongLine) continue;
                if (_line.Length >= MaxLineBytes)
                {
                    // Drop it and wait for the next newline rather than growing without bound.
                    _line.SetLength(0);
                    _skippingOverlongLine = true;
                    continue;
                }
                _line.WriteByte(b);
            }
        }
        catch (Exception)
        {
            // This runs on the chat hot path. Losing a token count is acceptable; throwing into the middle
            // of someone's streamed answer is not.
            _line.SetLength(0);
            _skippingOverlongLine = false;
        }
    }

    private void TryParseLine()
    {
        if (_line.Length == 0) return;

        var text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
        if (!text.StartsWith("data:", StringComparison.Ordinal)) return;

        var data = text["data:".Length..].Trim();
        // Cheap pre-filter: the overwhelming majority of lines are content deltas, and parsing every one
        // of them as JSON would put a real cost on every token of every chat.
        if (data.Length == 0 || data[0] != '{' || !data.Contains("\"usage\"", StringComparison.Ordinal)) return;

        if (LlmUsageParser.TryParse(data, out var usage)) Usage = usage;
    }
}
