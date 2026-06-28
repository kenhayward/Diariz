using System.Text;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class AudioFormatsTests
{
    // Build a header from an ASCII string (use \0 for don't-care bytes like size fields).
    private static byte[] H(string ascii) => Encoding.ASCII.GetBytes(ascii);

    [Fact]
    public void Detect_RecognisesContainersByMagicBytes()
    {
        Assert.Equal("wav", AudioFormats.Detect(H("RIFF\0\0\0\0WAVEfmt ")));
        Assert.Equal("flac", AudioFormats.Detect(H("fLaC\0\0\0\0\0\0\0\0")));
        Assert.Equal("ogg", AudioFormats.Detect(H("OggS\0\0\0\0\0\0\0\0")));
        Assert.Equal("m4a", AudioFormats.Detect(H("\0\0\0\u0018ftypM4A \0\0")));
        Assert.Equal("webm", AudioFormats.Detect(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Detect_RecognisesMp3ByID3TagOrFrameSync()
    {
        Assert.Equal("mp3", AudioFormats.Detect(H("ID3\x04\0\0\0\0\0\0\0\0")));
        Assert.Equal("mp3", AudioFormats.Detect(new byte[] { 0xFF, 0xFB, 0x90, 0, 0, 0, 0, 0, 0, 0, 0, 0 })); // MPEG-1 L3
    }

    [Fact]
    public void Detect_ReturnsNullForUnknownOrTooShort()
    {
        Assert.Null(AudioFormats.Detect(H("%PDF-1.7 not audio")));
        Assert.Null(AudioFormats.Detect(new byte[] { 0, 1, 2 })); // too short
        Assert.Null(AudioFormats.Detect(System.Array.Empty<byte>()));
    }

    [Fact]
    public void IsAllowed_RoyaltyFreeAndMp3AlwaysAllowed()
    {
        foreach (var f in new[] { "wav", "mp3", "flac", "ogg", "webm" })
        {
            Assert.True(AudioFormats.IsAllowed(f, allowAac: false));
            Assert.True(AudioFormats.IsAllowed(f, allowAac: true));
        }
    }

    [Fact]
    public void IsAllowed_M4aGatedByAllowAac()
    {
        Assert.True(AudioFormats.IsAllowed("m4a", allowAac: true));
        Assert.False(AudioFormats.IsAllowed("m4a", allowAac: false));
    }

    [Fact]
    public void Validate_AcceptsKnownAllowed_RejectsUnknownAndDisabledM4a()
    {
        var (ok, fmt, _) = AudioFormats.Validate(H("RIFF\0\0\0\0WAVEfmt "), allowAac: true);
        Assert.True(ok);
        Assert.Equal("wav", fmt);

        var unknown = AudioFormats.Validate(H("%PDF-1.7 not audio"), allowAac: true);
        Assert.False(unknown.Ok);
        Assert.NotNull(unknown.Reason);

        var m4aOff = AudioFormats.Validate(H("\0\0\0\u0018ftypM4A \0\0"), allowAac: false);
        Assert.False(m4aOff.Ok);
        Assert.Equal("m4a", m4aOff.Format);
    }
}
