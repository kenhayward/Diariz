using System.Diagnostics;
using System.Globalization;

namespace Diariz.Api.Services;

/// <summary>Cuts a span out of a stored recording as 16 kHz mono WAV, for judging by ear whether a voice
/// really is a given person.
///
/// <para>Given a presigned URL, ffmpeg range-seeks into the object and transfers only what it reads, so a
/// five-second clip out of a 200 MB recording costs a few hundred kilobytes rather than the whole file. The
/// URL is an input to a local subprocess and never reaches a client.</para></summary>
public interface IAudioClipper
{
    Task<byte[]> ClipAsync(string sourceUrl, long fromMs, long toMs, CancellationToken ct = default);
}

public class FfmpegAudioClipper : IAudioClipper
{
    /// <summary>Longest clip this will produce. Assessment plays seconds of speech; without a cap a caller
    /// could ask for a whole meeting as uncompressed WAV.</summary>
    public const long MaxClipMs = 120_000;

    /// <summary>The command line, pure so its argument order - where ffmpeg's semantics live - is testable
    /// without ffmpeg installed.
    ///
    /// <para><c>-ss</c> precedes <c>-i</c> so the seek happens on input rather than by decoding and
    /// discarding everything before it, and the end is a duration (<c>-t</c>) rather than <c>-to</c>, which
    /// would otherwise be ambiguous about whether it is relative to the seek point.</para>
    ///
    /// <para>The output path is <b>not</b> included: the caller appends it, so this stays a pure description
    /// of the transform.</para></summary>
    public static IReadOnlyList<string> Args(string sourceUrl, long fromMs, long toMs)
    {
        var start = Math.Max(0, fromMs);
        var duration = Math.Clamp(toMs - start, 0, MaxClipMs);
        return
        [
            "-nostdin", "-loglevel", "error", "-y",
            "-ss", Seconds(start),
            "-i", sourceUrl,
            "-t", Seconds(duration),
            "-vn", "-ac", "1", "-ar", "16000",
            "-f", "wav",
        ];
    }

    private static string Seconds(long ms) =>
        (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    public async Task<byte[]> ClipAsync(
        string sourceUrl, long fromMs, long toMs, CancellationToken ct = default)
    {
        // A temp file rather than a pipe: writing WAV to a pipe leaves the RIFF length field unset, because
        // ffmpeg cannot seek back to fill it in, and browsers handle that inconsistently. Clips are seconds
        // long, so the file is small and the response gets a real Content-Length.
        var temp = Path.Combine(Path.GetTempPath(), $"diariz-clip-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // ArgumentList, never a joined string: a presigned URL carries & and = that a shell would split.
            foreach (var a in Args(sourceUrl, fromMs, toMs)) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(temp);

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("ffmpeg did not start");

            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {stderr}");

            return await File.ReadAllBytesAsync(temp, ct);
        }
        finally
        {
            // Best effort. A leaked temp file is untidy; throwing here would replace a served clip with a 500.
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (IOException)
            {
                // ignored
            }
        }
    }
}
