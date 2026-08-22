namespace Diariz.Api.Services;

/// <summary>How large a capture may be before it is worth rescaling for an OCR model, and what size it
/// should become.
///
/// <para>Pure arithmetic, deliberately separate from <see cref="OcrImageEncoder"/> for the same reason
/// <see cref="VisionImageBounds"/> is: the interesting cases (extreme aspect ratios, exactly-at-the-cap,
/// never-upscale, never-round-to-zero, a misconfigured cap) are the ones worth testing, and none of them
/// need an image, an encoder, or object storage.</para>
///
/// <para><b>Why the cap is a parameter here and a constant there.</b> The vision bound answers "how much
/// can a chat VLM usefully take", which is roughly the same for all of them. This one answers "what did
/// this specific OCR model score best at", and that was measured to be different for every model tried:
/// olmOCR-2 reads a dense capture correctly at 2048 and <i>degrades</i> at 2560 (it starts substituting
/// image placeholders for numbers), GLM-OCR wants 2560, and Qwen3-VL improves right up to it. Quality is
/// not monotonic in resolution for any of them - at one size a model invented an entire column of
/// plausible scores - so this is a calibration, not a maximum, and a constant would have been wrong for
/// somebody whatever value it took.</para></summary>
public static class OcrImageBounds
{
    /// <summary>The size this capture should be sent at, or <c>null</c> when it already fits and must be
    /// passed through untouched.
    ///
    /// <para>Null is the instruction to send the stored bytes verbatim. That matters more here than on the
    /// vision path: resampling measurably hurt every OCR model tested, so "do not touch it" is the good
    /// case and rescaling is the concession.</para></summary>
    /// <param name="maxEdge">Longest permitted edge. Zero or negative means no cap - a misconfigured
    /// parameter must pass the capture through rather than shrink it to nothing.</param>
    public static (int Width, int Height)? Fit(int width, int height, int maxEdge)
    {
        // A row with nonsense dimensions must not become a division by zero or a negative size. Passing it
        // through unresized lets the encoder hand the original bytes to the model, which is the harmless
        // outcome; refusing here would fail the whole request over one bad row.
        if (width <= 0 || height <= 0) return null;
        if (maxEdge <= 0) return null;

        // The SMALLER scale is the binding one: it is the axis that would still be outside the box under
        // the other. Taking the larger would return something that "fits" on one axis and overshoots on
        // the other, which for a tall strip means barely shrinking it at all.
        var scale = Math.Min((double)maxEdge / width, (double)maxEdge / height);
        if (scale >= 1) return null;

        // Floored at 1: a sliver such as 2x100000 scales its short axis to a fraction of a pixel, and no
        // encoder accepts a zero-width image.
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }
}
