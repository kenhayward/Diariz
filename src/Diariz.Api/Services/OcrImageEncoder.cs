using Diariz.Domain.Entities;
using SkiaSharp;

namespace Diariz.Api.Services;

public interface IOcrImageEncoder
{
    /// <summary>Loads a capture and returns it as a <c>data:</c> URL sized for an OCR model.</summary>
    Task<string> EncodeAsync(MeetingScreenshot shot, int maxEdge, CancellationToken ct = default);
}

/// <summary>Loads a stored screen capture and shapes it for an OCR model's <c>image_url</c> content part.
///
/// <para>Structurally the same two paths as <see cref="VisionImageEncoder"/> - pass through verbatim when
/// it fits, resample when it does not - but it diverges on one deliberate point.</para>
///
/// <para><b>The rescaled path emits PNG, not JPEG.</b> The vision encoder switches to JPEG on the grounds
/// that resampling has already destroyed the flat colour runs PNG relies on, so the lossy encode costs
/// little more. That reasoning does not carry here: JPEG artefacts land on glyph edges, and glyph edges
/// are the entire signal an OCR model is reading. The failure mode this feature already has is
/// character-level - a measured run misread <c>DSP</c> as <c>OSP</c> reproducibly - so spending bytes to
/// avoid adding more of it is the right trade. Screenshots stay compact as PNG in any case.</para>
///
/// <para>Nothing is cached here, because the result is cached a level up: the extracted <i>text</i> is
/// stored on the capture, so a second request never reaches this encoder at all.</para></summary>
public sealed class OcrImageEncoder(IAudioStorage storage) : IOcrImageEncoder
{
    public async Task<string> EncodeAsync(MeetingScreenshot shot, int maxEdge, CancellationToken ct = default)
    {
        var fit = OcrImageBounds.Fit(shot.Width, shot.Height, maxEdge);

        byte[] original;
        await using (var stream = await storage.OpenReadAsync(shot.BlobKey, ct))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            original = ms.ToArray();
        }

        if (fit is null) return DataUrl("image/png", original);

        var (width, height) = fit.Value;
        using var decoded = SKBitmap.Decode(original);
        // A blob Skia cannot read is not worth failing the request over: hand the model the original bytes
        // and let it decide.
        if (decoded is null) return DataUrl("image/png", original);

        using var resized = decoded.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized is null) return DataUrl("image/png", original);

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return DataUrl("image/png", data.ToArray());
    }

    private static string DataUrl(string mediaType, byte[] bytes) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
}
