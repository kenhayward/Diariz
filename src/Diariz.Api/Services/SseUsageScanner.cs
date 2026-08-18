using System.Text;
using System.Text.Json;

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

    /// <summary>The most recent non-null <c>finish_reason</c> seen, or null if none has appeared.
    /// <c>length</c> is the one that matters: it means a token cap cut the reply off, which otherwise
    /// looks identical to a model that simply answered nothing.</summary>
    public string? FinishReason { get; private set; }

    /// <summary>Bytes currently held in the partial-line buffer. Exposed only so the bound and the
    /// per-line reset are testable through a public seam - this repo tests through public API rather than
    /// <c>InternalsVisibleTo</c>. Not used by any production caller.</summary>
    public int BufferedBytes => (int)_line.Length;

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
        if (data.Length == 0 || data[0] != '{') return;

        // Cheap pre-filter: the overwhelming majority of lines are content deltas, and parsing every one
        // of them as JSON would put a real cost on every token of every chat.
        var hasUsage = data.Contains("\"usage\"", StringComparison.Ordinal);
        var hasReason = HasStringFinishReason(data);
        if (!hasUsage && !hasReason) return;

        if (hasUsage && LlmUsageParser.TryParse(data, out var usage)) Usage = usage;

        if (!hasReason) return;
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (LlmFinishReasonParser.FromRoot(doc.RootElement) is { } reason) FinishReason = reason;
        }
        catch (JsonException)
        {
            // Same rule as everywhere else on this path: an unparseable chunk costs a measurement, not a call.
        }
    }

    /// <summary>True only when the chunk carries a finish_reason with a STRING value.
    ///
    /// Matching the bare key would defeat the pre-filter completely: virtually every delta chunk carries
    /// <c>"finish_reason":null</c>, so a key-name test would JSON-parse every token of every chat. This
    /// walks past the colon and any whitespace and requires an opening quote, which also tolerates the
    /// spaced-out formatting some servers emit.</summary>
    internal static bool HasStringFinishReason(string data)
    {
        const string key = "\"finish_reason\"";
        var i = data.IndexOf(key, StringComparison.Ordinal);
        while (i >= 0)
        {
            var j = i + key.Length;
            while (j < data.Length && char.IsWhiteSpace(data[j])) j++;
            if (j < data.Length && data[j] == ':')
            {
                j++;
                while (j < data.Length && char.IsWhiteSpace(data[j])) j++;
                if (j < data.Length && data[j] == '"') return true;
            }
            i = data.IndexOf(key, i + key.Length, StringComparison.Ordinal);
        }
        return false;
    }
}
