using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using SkiaSharp;

namespace Diariz.Api.Tests;

/// <summary>Turning a stored capture into something a vision model can be handed.
///
/// The behaviour worth pinning is the SPLIT: a capture already inside the box must be handed over
/// byte-for-byte, and one outside it must come back genuinely smaller. Asserting only "the result is a
/// PNG" would pass while the encoder silently re-encoded every image it was given.</summary>
public class VisionImageEncoderTests
{
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        // Some real content, so the encoders have something to compress and a blank-image shortcut cannot
        // make the two paths accidentally agree.
        using var paint = new SKPaint { Color = SKColors.DarkSlateBlue, IsAntialias = false };
        for (var i = 0; i < width; i += 7)
            canvas.DrawRect(i, (i * 3) % Math.Max(1, height), 4, 20, paint);
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (VisionImageEncoder Encoder, FakeAudioStorage Storage, MeetingScreenshot Shot) Make(
        int width, int height)
    {
        var storage = new FakeAudioStorage();
        var key = $"screenshots/{Guid.NewGuid()}.png";
        storage.Objects[key] = Png(width, height);
        var shot = new MeetingScreenshot
        {
            Id = Guid.NewGuid(),
            BlobKey = key,
            Width = width,
            Height = height,
        };
        return (new VisionImageEncoder(storage), storage, shot);
    }

    private static (string MediaType, byte[] Bytes) Parse(string dataUrl)
    {
        Assert.StartsWith("data:", dataUrl);
        var comma = dataUrl.IndexOf(',');
        var header = dataUrl[5..comma];
        Assert.EndsWith(";base64", header);
        return (header[..^";base64".Length], Convert.FromBase64String(dataUrl[(comma + 1)..]));
    }

    [Fact]
    public async Task Encode_OversizedCapture_ReturnsJpegThatFitsTheBox()
    {
        var (encoder, _, shot) = Make(3840, 2160);

        var result = await encoder.EncodeAsync(shot, CancellationToken.None);

        var (mediaType, bytes) = Parse(result.DataUrl);
        Assert.Equal("image/jpeg", mediaType);

        // The DECODED image, not the reported numbers: the point is what the model actually receives.
        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal(1920, decoded.Width);
        Assert.Equal(1080, decoded.Height);

        // And the reported dimensions must describe that same image - the context meter bills from them.
        Assert.Equal(decoded.Width, result.Width);
        Assert.Equal(decoded.Height, result.Height);
    }

    [Fact]
    public async Task Encode_SmallCapture_PassesTheStoredBytesThroughUntouched()
    {
        var (encoder, storage, shot) = Make(800, 600);
        var stored = storage.Objects[shot.BlobKey];

        var result = await encoder.EncodeAsync(shot, CancellationToken.None);

        var (mediaType, bytes) = Parse(result.DataUrl);
        Assert.Equal("image/png", mediaType);
        // Byte identity, not "it decodes to 800x600" - a re-encode would satisfy the latter.
        Assert.Equal(stored, bytes);
        Assert.Equal(800, result.Width);
        Assert.Equal(600, result.Height);
    }

    [Fact]
    public async Task Encode_ExactlyAtTheBound_TakesThePassThroughPath()
    {
        var (encoder, storage, shot) = Make(1920, 1080);
        var stored = storage.Objects[shot.BlobKey];

        var result = await encoder.EncodeAsync(shot, CancellationToken.None);

        var (mediaType, bytes) = Parse(result.DataUrl);
        Assert.Equal("image/png", mediaType);
        Assert.Equal(stored, bytes);
    }

    [Fact]
    public async Task Encode_TallStrip_ClampsOnHeightAndKeepsItsRatio()
    {
        var (encoder, _, shot) = Make(600, 4000);

        var result = await encoder.EncodeAsync(shot, CancellationToken.None);

        using var decoded = SKBitmap.Decode(Parse(result.DataUrl).Bytes);
        Assert.Equal(1080, decoded.Height);
        Assert.Equal(162, decoded.Width);
    }

}
