using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class VoiceprintsTests
{
    [Fact]
    public void Centroid_OfEmpty_IsNull() =>
        Assert.Null(Voiceprints.Centroid([]));

    [Fact]
    public void Centroid_IsMeanThenL2Normalised()
    {
        // Mean of (3,0) and (0,0) is (1.5,0); normalised → (1,0).
        var centroid = Voiceprints.Centroid([new float[] { 3f, 0f }, new float[] { 0f, 0f }]);

        Assert.NotNull(centroid);
        var v = centroid!.ToArray();
        Assert.Equal(1f, v[0], 5);
        Assert.Equal(0f, v[1], 5);
    }

    [Fact]
    public void Centroid_ResultIsUnitLength()
    {
        var centroid = Voiceprints.Centroid([new float[] { 1f, 2f, 3f }, new float[] { 4f, 5f, 6f }]);

        var norm = Math.Sqrt(centroid!.ToArray().Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, 5);
    }
}
