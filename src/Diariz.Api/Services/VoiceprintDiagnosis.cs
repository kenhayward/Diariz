namespace Diariz.Api.Services;

/// <summary>How well one enrolled sample resembles the person's others.</summary>
public enum SampleVerdict
{
    /// <summary>The only sample this person has. Nothing to compare against, which is not the same as a
    /// problem - most of the directory is in this state.</summary>
    Only,

    /// <summary>Close to at least one other sample. This is what a healthy training set looks like.</summary>
    Core,

    /// <summary>Further out, but within the confirmation band. Most likely the same voice in a different
    /// recording condition - a phone, a car, a room microphone - which is exactly the audio a voiceprint
    /// benefits from having.</summary>
    Variant,

    /// <summary>Resembles none of the others. Either a condition nothing else covers, or a different person
    /// enrolled under this name. <b>Only listening tells you which</b>, which is why the verdict says where
    /// to look rather than what to do.</summary>
    Alone,
}

/// <param name="NearestSiblingDistance">Cosine distance to the closest other sample. Null when there is no
/// other sample.</param>
/// <param name="DistanceToOthers">Cosine distance to the centroid of <b>the person's other samples</b> - a
/// true leave-one-out. Null when there is no other sample.</param>
public record SampleDiagnosis(
    Guid SampleId, double? NearestSiblingDistance, double? DistanceToOthers, SampleVerdict Verdict);

/// <summary>Tells a recording condition apart from a misattributed sample.
///
/// <para>Measured on the live instance, a third of samples belonging to multi-sample people sit further than
/// 0.45 from their nearest sibling, and the widest same-person pair is 1.134 - orthogonal, which two
/// recordings of one human cannot be. The design assumed that spread was device variation; the overlap with
/// the impostor range says some of it is other people enrolled under one name.</para>
///
/// <para>That distinction has to be made before anything clusters this data. Today a wrong sample is diluted
/// into the centroid and mostly does nothing; give it a template of its own and it becomes a sharp
/// false-accept for whoever that voice really belongs to.</para>
///
/// <para>Pure, and deliberately needs no worker: every sample already carries a <c>vector(192)</c>
/// embedding, so this is arithmetic over data that exists.</para></summary>
public static class VoiceprintDiagnosis
{
    /// <param name="samples">One person's samples. Order is irrelevant; every one is diagnosed.</param>
    /// <param name="t">Reuses the calibrated identification thresholds rather than inventing a second set of
    /// constants to keep in step with them.</param>
    public static IReadOnlyList<SampleDiagnosis> Diagnose(
        IReadOnlyList<(Guid Id, float[] Embedding)> samples, IdentificationThresholds t)
    {
        if (samples.Count == 0) return [];

        var result = new List<SampleDiagnosis>(samples.Count);

        foreach (var (id, embedding) in samples)
        {
            var others = samples.Where(s => s.Id != id).ToList();
            if (others.Count == 0)
            {
                result.Add(new SampleDiagnosis(id, null, null, SampleVerdict.Only));
                continue;
            }

            // The *closest* other sample, not the average: a pair inside an otherwise scattered set is still
            // a pair, and averaging would drown that and report every member as an outlier.
            var nearest = others.Min(o => Voiceprints.CosineDistance(embedding, o.Embedding));

            // Leave-one-out. Including the sample in its own centroid pulls the centre toward it, after which
            // everything resembles itself and the diagnosis says nothing.
            var centroid = Voiceprints.Centroid(others.Select(o => o.Embedding).ToList());
            var toOthers = centroid is null ? (double?)null : Voiceprints.CosineDistance(embedding, centroid.ToArray());

            result.Add(new SampleDiagnosis(id, nearest, toOthers, Verdict(nearest, t)));
        }

        return result;
    }

    private static SampleVerdict Verdict(double nearest, IdentificationThresholds t) =>
        nearest <= t.Accept ? SampleVerdict.Core
        : nearest <= t.Suggest ? SampleVerdict.Variant
        : SampleVerdict.Alone;
}
