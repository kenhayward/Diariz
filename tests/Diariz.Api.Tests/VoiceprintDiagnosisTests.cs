using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Which of a person's enrolled samples look like each other, and which do not.
///
/// <para>Measured on the live instance, of 108 samples belonging to people with more than one, 37 sit further
/// than 0.45 from their nearest sibling, and the widest same-person pair is 1.134 - orthogonal, which two
/// recordings of one human cannot be. Some of that spread is a phone against a meeting room; some of it is a
/// different person enrolled under one name. Clustering cannot tell those apart, and would promote the second
/// kind from a diluted nuisance into a confident false-accept. This is what tells them apart first.</para></summary>
public class VoiceprintDiagnosisTests
{
    private static readonly IdentificationThresholds T = new(0.30, 0.40, 0.05, 3000);

    /// <summary>A unit vector at a chosen cosine distance from <see cref="Origin"/>, so every distance in
    /// these tests is one the arithmetic can be checked against by hand.</summary>
    private static float[] At(double distance)
    {
        var cos = 1 - distance;
        var v = new float[192];
        v[0] = (float)cos;
        v[1] = (float)Math.Sqrt(Math.Max(0, 1 - (cos * cos)));
        return v;
    }

    private static float[] Origin() => At(0);

    [Fact]
    public void A_lone_sample_has_nothing_to_compare_against()
    {
        // Reporting it as an outlier would be the most common false alarm in the directory: most people have
        // exactly one sample, and one sample is not evidence of anything.
        var id = Guid.NewGuid();

        var d = Assert.Single(VoiceprintDiagnosis.Diagnose([(id, Origin())], T));

        Assert.Equal(SampleVerdict.Only, d.Verdict);
        Assert.Null(d.NearestSiblingDistance);
        Assert.Null(d.DistanceToOthers);
    }

    [Fact]
    public void Two_close_samples_are_both_core()
    {
        List<(Guid, float[])> samples = [(Guid.NewGuid(), Origin()), (Guid.NewGuid(), At(0.1))];

        Assert.All(
            VoiceprintDiagnosis.Diagnose(samples, T),
            x => Assert.Equal(SampleVerdict.Core, x.Verdict));
    }

    [Fact]
    public void A_sample_within_the_band_is_a_variant_not_an_outlier()
    {
        // The same voice on a different microphone. It belongs, and calling it an outlier would invite
        // someone to delete the very sample that teaches a second recording condition.
        List<(Guid, float[])> samples = [(Guid.NewGuid(), Origin()), (Guid.NewGuid(), At(0.35))];

        Assert.All(
            VoiceprintDiagnosis.Diagnose(samples, T),
            x => Assert.Equal(SampleVerdict.Variant, x.Verdict));
    }

    [Fact]
    public void A_sample_beyond_the_band_sits_alone()
    {
        var lone = Guid.NewGuid();
        List<(Guid, float[])> samples =
            [(Guid.NewGuid(), Origin()), (Guid.NewGuid(), At(0.05)), (lone, At(0.9))];

        var d = VoiceprintDiagnosis.Diagnose(samples, T);

        Assert.Equal(SampleVerdict.Alone, d.Single(x => x.SampleId == lone).Verdict);
        Assert.All(d.Where(x => x.SampleId != lone), x => Assert.Equal(SampleVerdict.Core, x.Verdict));
    }

    [Fact]
    public void Nearest_sibling_is_the_closest_one_not_the_average()
    {
        // A pair inside a scattered set is still a pair. Averaging would drown that signal and report three
        // outliers where there are two that agree plus one that does not.
        var a = Guid.NewGuid();
        List<(Guid, float[])> samples =
            [(a, Origin()), (Guid.NewGuid(), At(0.05)), (Guid.NewGuid(), At(0.95))];

        var d = VoiceprintDiagnosis.Diagnose(samples, T).Single(x => x.SampleId == a);

        Assert.NotNull(d.NearestSiblingDistance);
        Assert.True(d.NearestSiblingDistance < 0.1, $"expected the close sibling to win, got {d.NearestSiblingDistance}");
    }

    [Fact]
    public void Distance_to_others_excludes_the_sample_itself()
    {
        // Leave-one-out, or it is not a test. Including the sample pulls the centroid toward it and every
        // sample then looks like it belongs - the exact failure this whole phase exists to avoid.
        var odd = Guid.NewGuid();
        List<(Guid, float[])> samples =
        [
            (Guid.NewGuid(), Origin()), (Guid.NewGuid(), Origin()), (Guid.NewGuid(), Origin()), (odd, At(0.8)),
        ];

        var d = VoiceprintDiagnosis.Diagnose(samples, T).Single(x => x.SampleId == odd);

        Assert.NotNull(d.DistanceToOthers);
        Assert.True(d.DistanceToOthers > 0.7, $"expected the odd one out to be far from the rest, got {d.DistanceToOthers}");
    }

    [Fact]
    public void The_two_numbers_can_disagree_and_both_are_reported()
    {
        // The case that makes both worth showing: a sample with one close companion, where the pair together
        // sits well away from the person's centre of mass. "Has company" and "resembles the rest" are
        // different questions, and only reporting one hides half of this.
        var a = Guid.NewGuid();
        List<(Guid, float[])> samples =
        [
            (Guid.NewGuid(), Origin()), (Guid.NewGuid(), Origin()), (Guid.NewGuid(), Origin()),
            (a, At(0.8)), (Guid.NewGuid(), At(0.82)),
        ];

        var d = VoiceprintDiagnosis.Diagnose(samples, T).Single(x => x.SampleId == a);

        Assert.True(d.NearestSiblingDistance < 0.1, "it has a close companion");
        Assert.True(d.DistanceToOthers > 0.5, "and the pair still sits away from the rest");
    }

    [Fact]
    public void Nothing_at_all_diagnoses_nothing()
    {
        Assert.Empty(VoiceprintDiagnosis.Diagnose([], T));
    }

    [Fact]
    public void Every_sample_is_diagnosed_exactly_once()
    {
        List<(Guid, float[])> samples =
            [(Guid.NewGuid(), Origin()), (Guid.NewGuid(), At(0.2)), (Guid.NewGuid(), At(0.9))];

        var d = VoiceprintDiagnosis.Diagnose(samples, T);

        Assert.Equal(3, d.Count);
        Assert.Equal(3, d.Select(x => x.SampleId).Distinct().Count());
    }
}
