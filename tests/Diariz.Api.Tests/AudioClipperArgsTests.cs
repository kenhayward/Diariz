using System.Globalization;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The ffmpeg command line. Extracted as a pure function precisely so the argument <em>order</em> -
/// which is where ffmpeg's semantics live - can be asserted without ffmpeg being installed.</summary>
public class AudioClipperArgsTests
{
    private const string Url = "http://minio:9000/diariz/audio/abc.webm?X-Amz-Signature=deadbeef&X-Amz-Expires=300";

    private static List<string> Args(long fromMs, long toMs) =>
        FfmpegAudioClipper.Args(Url, fromMs, toMs).ToList();

    [Fact]
    public void Args_seek_before_input_so_the_seek_is_fast()
    {
        // -ss after -i decodes from the start of the file and throws the result away, which on a 200 MB
        // recording is the difference between a clip and a timeout.
        var args = Args(65_000, 70_000);

        var ss = args.IndexOf("-ss");
        var i = args.IndexOf("-i");
        Assert.True(ss >= 0, "-ss missing");
        Assert.True(i >= 0, "-i missing");
        Assert.True(ss < i, "-ss must precede -i");
    }

    [Fact]
    public void Args_express_the_end_as_a_duration()
    {
        // -to after an input seek is ambiguous about whether it is relative to the seek point. A duration
        // is not.
        var args = Args(65_000, 70_000);

        Assert.Equal("65", args[args.IndexOf("-ss") + 1]);
        Assert.Equal("5", args[args.IndexOf("-t") + 1]);
    }

    [Fact]
    public void Args_format_seconds_invariantly()
    {
        // A comma decimal separator under a de-DE culture would make ffmpeg reject the offset outright.
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var args = Args(1_500, 3_250);
            Assert.Equal("1.5", args[args.IndexOf("-ss") + 1]);
            Assert.Equal("1.75", args[args.IndexOf("-t") + 1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public void Args_pass_the_url_as_a_single_argument()
    {
        // A presigned URL contains & and =. Joined into a shell string it would be split; as its own
        // ArgumentList entry it cannot be.
        Assert.Equal(Url, Args(0, 1000)[Args(0, 1000).IndexOf("-i") + 1]);
    }

    [Fact]
    public void Args_cap_an_over_long_request()
    {
        // Assessment plays seconds of speech. Without a cap a caller could ask for a whole meeting as
        // uncompressed WAV.
        Assert.Equal("120", Args(0, 10_000_000)[Args(0, 10_000_000).IndexOf("-t") + 1]);
    }

    [Fact]
    public void Args_clamp_a_negative_start_to_zero()
    {
        Assert.Equal("0", Args(-5_000, 1_000)[Args(-5_000, 1_000).IndexOf("-ss") + 1]);
    }

    [Fact]
    public void Args_produce_a_zero_duration_for_an_inverted_span()
    {
        // Rather than a negative -t, which ffmpeg would reject with a message about the wrong thing.
        Assert.Equal("0", Args(5_000, 1_000)[Args(5_000, 1_000).IndexOf("-t") + 1]);
    }

    [Fact]
    public void Args_produce_mono_16k_audio_only()
    {
        var args = Args(0, 1000);

        Assert.Equal("1", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("16000", args[args.IndexOf("-ar") + 1]);
        Assert.Equal("wav", args[args.IndexOf("-f") + 1]);
        Assert.Contains("-vn", args);
    }

    [Fact]
    public void Args_never_read_stdin()
    {
        // The API has no console. Without -nostdin ffmpeg can consume the process's stdin and hang.
        Assert.Contains("-nostdin", Args(0, 1000));
    }
}
