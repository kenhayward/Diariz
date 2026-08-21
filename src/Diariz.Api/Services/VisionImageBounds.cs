namespace Diariz.Api.Services;

/// <summary>How large an image may be before it is worth rescaling for a vision model, and what size it
/// should become.
///
/// <para>Pure arithmetic, deliberately separated from <see cref="VisionImageEncoder"/>: the interesting
/// cases here (extreme aspect ratios, exactly-at-the-bound, never-upscale, never-round-to-zero) are the
/// ones worth testing, and none of them need an image, an encoder, or object storage to exercise.</para>
///
/// <para><b>Why 1920x1080.</b> The vision models this platform is deployed against degrade above it, so
/// sending a 4K capture buys nothing and costs a great deal of context. The bound is a <i>cap</i>, not a
/// target - a dense 4K screenshot loses half its linear resolution and its smallest text may not survive.
/// That is accepted deliberately.</para></summary>
public static class VisionImageBounds
{
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;

    /// <summary>The size this image should be sent at, or <c>null</c> when it already fits and must be
    /// passed through untouched.
    ///
    /// <para>Null is not "no opinion" - it is the instruction to send the stored bytes verbatim, which is
    /// what keeps a small capture lossless and keeps the encoder out of the picture entirely.</para></summary>
    public static (int Width, int Height)? Fit(int width, int height)
    {
        // A row with nonsense dimensions must not become a division by zero or a negative size. Passing it
        // through unresized lets the encoder hand the original bytes to the model, which is the harmless
        // outcome; refusing here would fail a whole chat turn over one bad row.
        if (width <= 0 || height <= 0) return null;

        // The SMALLER scale is the binding one: it is the axis that would still be outside the box under
        // the other. Taking the larger would return something that "fits" on one axis and overshoots on the
        // other, which for a tall strip means barely shrinking it at all.
        var scale = Math.Min((double)MaxWidth / width, (double)MaxHeight / height);
        if (scale >= 1) return null;

        // Floored at 1: a sliver such as 2x100000 scales its short axis to a fraction of a pixel, and no
        // encoder accepts a zero-width image.
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }
}
