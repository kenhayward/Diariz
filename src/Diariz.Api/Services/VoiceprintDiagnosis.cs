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

    /// <summary>Sits closer to a sample belonging to <b>somebody else</b> than to any of this person's own.
    ///
    /// <para>Strictly more serious than <see cref="Alone"/>, and checked first: if a sibling were closer the
    /// question could not arise. Measured live, 27 of 92 samples belonging to multi-sample people were in
    /// this state and 9 of those sat within the accept distance of that other person - so clustering the set
    /// would have promoted each from a diluted nuisance into a confident match for the wrong human.</para>
    ///
    /// <para>Still not a verdict of <em>wrong</em>: two people can genuinely sound alike. It is a verdict of
    /// <em>this is the one to listen to first</em>, and it names who it sounds like so the existing reassign
    /// control can act on it.</para></summary>
    Impostor,
}

/// <summary>One sample in the training set, with whose voiceprint it belongs to.
///
/// <para>The diagnosis takes the <b>whole</b> set rather than one person's slice, because "is somebody else
/// closer?" cannot be answered from a slice - and that question is the only thing that separates a second
/// microphone from a second human, which distances to siblings provably cannot do.</para></summary>
public record TrainingSample(Guid Id, Guid PersonId, float[] Embedding);

/// <param name="NearestSiblingDistance">Cosine distance to the closest other sample. Null when there is no
/// other sample.</param>
/// <param name="DistanceToOthers">Cosine distance to the centroid of <b>the person's other samples</b> - a
/// true leave-one-out. Null when there is no other sample.</param>
/// <param name="NearestImpostorDistance">Cosine distance to the closest sample belonging to <b>anyone
/// else</b>. Null in a directory holding only this person. Reported even when it is not a finding - the
/// number is worth seeing.</param>
/// <param name="NearestImpostorPersonId">Who that was, so the finding can be acted on rather than only
/// read.</param>
public record SampleDiagnosis(
    Guid SampleId, double? NearestSiblingDistance, double? DistanceToOthers, SampleVerdict Verdict,
    double? NearestImpostorDistance = null, Guid? NearestImpostorPersonId = null);

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
    /// <param name="personId">Whose samples to diagnose. Everyone else's are the impostor pool.</param>
    /// <param name="all">The whole training set. Order is irrelevant.</param>
    /// <param name="t">Reuses the calibrated identification thresholds rather than inventing a second set of
    /// constants to keep in step with them.</param>
    public static IReadOnlyList<SampleDiagnosis> Diagnose(
        Guid personId, IReadOnlyList<TrainingSample> all, IdentificationThresholds t)
    {
        var samples = all.Where(s => s.PersonId == personId).ToList();
        if (samples.Count == 0) return [];

        var impostors = all.Where(s => s.PersonId != personId).ToList();
        var result = new List<SampleDiagnosis>(samples.Count);

        foreach (var (id, _, embedding) in samples)
        {
            // Reported whether or not it is a finding. Null only in a directory holding one person.
            double? impostor = impostors.Count == 0
                ? null
                : impostors.Min(o => Voiceprints.CosineDistance(embedding, o.Embedding));
            var impostorId = impostor is null
                ? (Guid?)null
                : impostors
                    .OrderBy(o => Voiceprints.CosineDistance(embedding, o.Embedding))
                    .First().PersonId;

            var others = samples.Where(s => s.Id != id).ToList();
            if (others.Count == 0)
            {
                // Deliberately not an Impostor however close the neighbour. The evidence for a
                // misattribution is relative - closer to them than to their own - and one sample has no
                // "own" to compare against. Most of the directory is in this state, so flagging on absolute
                // distance would bury the real findings under the whole single-sample population.
                result.Add(new SampleDiagnosis(id, null, null, SampleVerdict.Only, impostor, impostorId));
                continue;
            }

            // The *closest* other sample, not the average: a pair inside an otherwise scattered set is still
            // a pair, and averaging would drown that and report every member as an outlier.
            var nearest = others.Min(o => Voiceprints.CosineDistance(embedding, o.Embedding));

            // Leave-one-out. Including the sample in its own centroid pulls the centre toward it, after which
            // everything resembles itself and the diagnosis says nothing.
            var centroid = Voiceprints.Centroid(others.Select(o => o.Embedding).ToList());
            var toOthers = centroid is null ? (double?)null : Voiceprints.CosineDistance(embedding, centroid.ToArray());

            result.Add(new SampleDiagnosis(
                id, nearest, toOthers, Verdict(nearest, impostor, t), impostor, impostorId));
        }

        return result;
    }

    private static SampleVerdict Verdict(double nearest, double? impostor, IdentificationThresholds t) =>
        // Checked first, and relative rather than absolute: two people can genuinely sound alike, so what
        // makes a misattribution is somebody else being closer than the person's own recordings - not merely
        // being close. It replaces Alone for most of the samples that had it, and catches the ones no
        // sibling-only verdict could: a sample sitting comfortably inside its own cluster while sitting
        // closer still to somebody else's.
        impostor is { } other && other < nearest ? SampleVerdict.Impostor
        : nearest <= t.Accept ? SampleVerdict.Core
        : nearest <= t.Suggest ? SampleVerdict.Variant
        : SampleVerdict.Alone;
}
