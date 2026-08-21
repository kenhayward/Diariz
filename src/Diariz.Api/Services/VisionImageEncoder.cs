using Diariz.Domain.Entities;
using SkiaSharp;

namespace Diariz.Api.Services;

/// <summary>One capture, ready to hand to a vision model. <paramref name="Width"/> and
/// <paramref name="Height"/> describe the image ACTUALLY encoded, not the stored capture - the context
/// meter bills from them, and after a rescale those are different numbers.</summary>
public sealed record VisionImage(string DataUrl, int Width, int Height);

public interface IVisionImageEncoder
{
    /// <summary>Loads a capture and returns it as a <c>data:</c> URL sized for a vision model.</summary>
    Task<VisionImage> EncodeAsync(MeetingScreenshot shot, CancellationToken ct = default);
}

/// <summary>Loads a stored screen capture and shapes it for an OpenAI-compatible <c>image_url</c> content
/// part.
///
/// <para><b>Two paths, and the split matters.</b> A capture already inside
/// <see cref="VisionImageBounds"/> is streamed out of storage and base64'd <i>verbatim</i> - never decoded,
/// never re-encoded - so a small capture reaches the model exactly as it was taken. Only a capture that
/// genuinely needs shrinking pays for a decode.</para>
///
/// <para><b>Why the rescaled path emits JPEG.</b> Resampling antialiases text, which destroys the flat
/// colour runs PNG relies on: a downscaled 1920x1080 screenshot can encode several times larger as PNG
/// than as JPEG of the same pixels. The resample has already given up pixel-exactness, so the lossy encode
/// costs little more. The pass-through path has given up nothing, so it stays lossless.</para>
///
/// <para>Nothing is cached. Under the sticky-attachment model the same capture re-encodes on every
/// follow-up turn, which is tens of milliseconds against a multi-second model call - cheaper than holding
/// megabytes of image in API memory or minting a third blob per capture with its own quota
/// accounting.</para></summary>
public sealed class VisionImageEncoder(IAudioStorage storage) : IVisionImageEncoder
{
    /// <summary>High enough that resampled text stays clean; low enough that the payload is a fraction of
    /// the equivalent PNG.</summary>
    private const int JpegQuality = 92;

    public async Task<VisionImage> EncodeAsync(MeetingScreenshot shot, CancellationToken ct = default)
    {
        var fit = VisionImageBounds.Fit(shot.Width, shot.Height);

        byte[] original;
        await using (var stream = await storage.OpenReadAsync(shot.BlobKey, ct))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            original = ms.ToArray();
        }

        if (fit is null)
            return new VisionImage(DataUrl("image/png", original), shot.Width, shot.Height);

        var (width, height) = fit.Value;
        using var decoded = SKBitmap.Decode(original);
        // A blob Skia cannot read is not worth failing a whole chat turn over: hand the model the original
        // bytes and let it decide. The stored dimensions are the only size we can honestly report.
        if (decoded is null)
            return new VisionImage(DataUrl("image/png", original), shot.Width, shot.Height);

        using var resized = decoded.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized is null)
            return new VisionImage(DataUrl("image/png", original), shot.Width, shot.Height);

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return new VisionImage(DataUrl("image/jpeg", data.ToArray()), width, height);
    }

    private static string DataUrl(string mediaType, byte[] bytes) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
}
