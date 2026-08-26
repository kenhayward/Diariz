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

    private static readonly Guid Ada = Guid.NewGuid();
    private static readonly Guid Grace = Guid.NewGuid();

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

        var d = Assert.Single(VoiceprintDiagnosis.Diagnose(Ada, [new TrainingSample(id, Ada, Origin())], T));

        Assert.Equal(SampleVerdict.Only, d.Verdict);
        Assert.Null(d.NearestSiblingDistance);
        Assert.Null(d.DistanceToOthers);
    }

    [Fact]
    public void Two_close_samples_are_both_core()
    {
        List<TrainingSample> samples = [new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, At(0.1))];

        Assert.All(
            VoiceprintDiagnosis.Diagnose(Ada, samples, T),
            x => Assert.Equal(SampleVerdict.Core, x.Verdict));
    }

    [Fact]
    public void A_sample_within_the_band_is_a_variant_not_an_outlier()
    {
        // The same voice on a different microphone. It belongs, and calling it an outlier would invite
        // someone to delete the very sample that teaches a second recording condition.
        List<TrainingSample> samples = [new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, At(0.35))];

        Assert.All(
            VoiceprintDiagnosis.Diagnose(Ada, samples, T),
            x => Assert.Equal(SampleVerdict.Variant, x.Verdict));
    }

    [Fact]
    public void A_sample_beyond_the_band_sits_alone()
    {
        var lone = Guid.NewGuid();
        List<TrainingSample> samples =
            [new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, At(0.05)), new(lone, Ada, At(0.9))];

        var d = VoiceprintDiagnosis.Diagnose(Ada, samples, T);

        Assert.Equal(SampleVerdict.Alone, d.Single(x => x.SampleId == lone).Verdict);
        Assert.All(d.Where(x => x.SampleId != lone), x => Assert.Equal(SampleVerdict.Core, x.Verdict));
    }

    [Fact]
    public void Nearest_sibling_is_the_closest_one_not_the_average()
    {
        // A pair inside a scattered set is still a pair. Averaging would drown that signal and report three
        // outliers where there are two that agree plus one that does not.
        var a = Guid.NewGuid();
        List<TrainingSample> samples =
            [new(a, Ada, Origin()), new(Guid.NewGuid(), Ada, At(0.05)), new(Guid.NewGuid(), Ada, At(0.95))];

        var d = VoiceprintDiagnosis.Diagnose(Ada, samples, T).Single(x => x.SampleId == a);

        Assert.NotNull(d.NearestSiblingDistance);
        Assert.True(d.NearestSiblingDistance < 0.1, $"expected the close sibling to win, got {d.NearestSiblingDistance}");
    }

    [Fact]
    public void Distance_to_others_excludes_the_sample_itself()
    {
        // Leave-one-out, or it is not a test. Including the sample pulls the centroid toward it and every
        // sample then looks like it belongs - the exact failure this whole phase exists to avoid.
        var odd = Guid.NewGuid();
        List<TrainingSample> samples =
        [
            new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, Origin()), new(odd, Ada, At(0.8)),
        ];

        var d = VoiceprintDiagnosis.Diagnose(Ada, samples, T).Single(x => x.SampleId == odd);

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
        List<TrainingSample> samples =
        [
            new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, Origin()),
            new(a, Ada, At(0.8)), new(Guid.NewGuid(), Ada, At(0.82)),
        ];

        var d = VoiceprintDiagnosis.Diagnose(Ada, samples, T).Single(x => x.SampleId == a);

        Assert.True(d.NearestSiblingDistance < 0.1, "it has a close companion");
        Assert.True(d.DistanceToOthers > 0.5, "and the pair still sits away from the rest");
    }

    [Fact]
    public void Nothing_at_all_diagnoses_nothing()
    {
        Assert.Empty(VoiceprintDiagnosis.Diagnose(Ada, [], T));
    }

    [Fact]
    public void Every_sample_is_diagnosed_exactly_once()
    {
        List<TrainingSample> samples =
            [new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, At(0.2)), new(Guid.NewGuid(), Ada, At(0.9))];

        var d = VoiceprintDiagnosis.Diagnose(Ada, samples, T);

        Assert.Equal(3, d.Count);
        Assert.Equal(3, d.Select(x => x.SampleId).Distinct().Count());
    }

    // ---- Is somebody else closer? The question the sibling-only verdicts could never ask.
    //
    // Measured live: of 92 samples belonging to people with more than one, 27 sit closer to a different
    // person than to any of their own, and 9 of those are within the accept distance of that person.
    // Clustering promotes each from a diluted nuisance into a confident match for the wrong human. ----

    [Fact]
    public void A_sample_closer_to_someone_else_says_so()
    {
        var odd = Guid.NewGuid();
        List<TrainingSample> all =
        [
            new(Guid.NewGuid(), Ada, Origin()),
            new(odd, Ada, At(0.9)),
            new(Guid.NewGuid(), Grace, At(0.95)),   // nearer to `odd` than Ada's own sample is
        ];

        var d = VoiceprintDiagnosis.Diagnose(Ada, all, T).Single(x => x.SampleId == odd);

        Assert.Equal(SampleVerdict.Impostor, d.Verdict);
        Assert.Equal(Grace, d.NearestImpostorPersonId);
    }

    [Fact]
    public void A_sample_that_looks_healthy_is_still_caught_when_someone_else_is_closer()
    {
        // The case that no sibling-only verdict can reach, and the reason the impostor check is worth
        // building: this sample sits comfortably inside its own person's cluster - close enough to be rated
        // "Matches their other recordings" - while sitting closer still to somebody else's voice. Exactly
        // one live sample was in this state, reading as healthy while being the strongest misattribution
        // signal in the directory.
        var mine = Guid.NewGuid();
        List<TrainingSample> all =
        [
            new(mine, Ada, Origin()),
            new(Guid.NewGuid(), Ada, At(0.25)),     // inside the accept distance - would be Core
            new(Guid.NewGuid(), Grace, At(0.10)),   // but somebody else is closer still
        ];

        var d = VoiceprintDiagnosis.Diagnose(Ada, all, T).Single(x => x.SampleId == mine);

        Assert.Equal(SampleVerdict.Impostor, d.Verdict);
    }

    [Fact]
    public void A_close_impostor_does_not_outrank_a_closer_sibling()
    {
        // Two people can genuinely sound alike. What makes a misattribution is the impostor being closer
        // than the person's own recordings, not merely being close - so the test is relative, and so is
        // the rule.
        var mine = Guid.NewGuid();
        List<TrainingSample> all =
        [
            new(mine, Ada, Origin()),
            new(Guid.NewGuid(), Ada, At(0.10)),
            new(Guid.NewGuid(), Grace, At(0.20)),
        ];

        var d = VoiceprintDiagnosis.Diagnose(Ada, all, T).Single(x => x.SampleId == mine);

        Assert.Equal(SampleVerdict.Core, d.Verdict);
        // Reported anyway: the number is worth seeing even when it is not a finding.
        Assert.NotNull(d.NearestImpostorDistance);
    }

    [Fact]
    public void A_lone_sample_is_never_an_impostor_however_close_the_neighbour()
    {
        // Deliberate. The evidence for a misattribution is relative - closer to them than to their own -
        // and one sample has no "own" to compare against. Flagging on absolute distance alone would open
        // every single-sample person in the directory (61 of 86 live) on the weakest possible evidence and
        // bury the 27 real ones.
        var lone = Guid.NewGuid();
        List<TrainingSample> all = [new(lone, Ada, Origin()), new(Guid.NewGuid(), Grace, At(0.05))];

        var d = VoiceprintDiagnosis.Diagnose(Ada, all, T).Single();

        Assert.Equal(SampleVerdict.Only, d.Verdict);
    }

    [Fact]
    public void A_directory_of_one_person_has_no_impostor_to_report()
    {
        List<TrainingSample> all = [new(Guid.NewGuid(), Ada, Origin()), new(Guid.NewGuid(), Ada, At(0.1))];

        Assert.All(
            VoiceprintDiagnosis.Diagnose(Ada, all, T),
            d => Assert.Null(d.NearestImpostorDistance));
    }

    [Fact]
    public void Only_the_named_persons_samples_are_diagnosed()
    {
        List<TrainingSample> all =
        [
            new(Guid.NewGuid(), Ada, Origin()),
            new(Guid.NewGuid(), Ada, At(0.1)),
            new(Guid.NewGuid(), Grace, At(0.5)),
        ];

        Assert.Equal(2, VoiceprintDiagnosis.Diagnose(Ada, all, T).Count);
    }
}
