using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>
/// The one genuinely hard decision in live speaker identity, tested with hand-built vectors so the
/// distances are known rather than discovered.
///
/// <para>pyannote clusters over whatever audio it is given, so its labels mean something only *within*
/// one chunk: the person it called SPEAKER_00 in this half minute is a coin flip away from being
/// SPEAKER_01 in the next. The stitcher is what gives a voice one label for the whole meeting.</para>
/// </summary>
public class LiveSpeakerStitcherTests
{
    private static readonly StitchThresholds Default = new(Threshold: 0.35, Margin: 0.05);

    /// A unit vector in the plane spanned by the first two axes, at <paramref name="degrees"/> from the
    /// first. Cosine distance between two of these is 1 - cos(difference), so every distance in these
    /// tests is arithmetic rather than something the implementation gets to define.
    private static float[] At(double degrees)
    {
        var r = degrees * Math.PI / 180.0;
        var v = new float[192];
        v[0] = (float)Math.Cos(r);
        v[1] = (float)Math.Sin(r);
        return v;
    }

    /// A vector with the given components set, for cases where the distance must be exact rather than
    /// merely close - integers up to 2^24 are exact in float, so a 3-4-5 triple gives a cosine of
    /// exactly 0.6 where trigonometry would give something a rounding away from it.
    private static float[] Vector(params (int Index, float Value)[] components)
    {
        var v = new float[192];
        foreach (var (i, value) in components) v[i] = value;
        return v;
    }

    private static SessionCentroid Known(string label, double degrees, int samples = 1) =>
        new(label, At(degrees), samples);

    private static ChunkSpeaker Incoming(string label, double degrees, long speechMs = 20_000) =>
        new(label, At(degrees), speechMs);

    [Fact]
    public void AVoiceCloseToAKnownCentroid_JoinsIt()
    {
        // 10 degrees apart: cosine distance 0.015, comfortably inside the 0.35 threshold.
        var decisions = LiveSpeakerStitcher.Stitch(
            [Known("S1", 0)], [Incoming("SPEAKER_00", 10)], Default);

        Assert.Equal("S1", Assert.Single(decisions).SessionLabel);
        Assert.False(decisions[0].IsNew);
    }

    [Fact]
    public void AVoiceCloseToNothing_MintsANewSessionLabel()
    {
        // 90 degrees: cosine distance 1.0. Nothing like the known voice.
        var decisions = LiveSpeakerStitcher.Stitch(
            [Known("S1", 0)], [Incoming("SPEAKER_00", 90)], Default);

        Assert.True(Assert.Single(decisions).IsNew);
        Assert.NotEqual("S1", decisions[0].SessionLabel);
    }

    [Fact]
    public void TheFirstChunkMintsALabelForEveryVoice()
    {
        // Nothing to match against yet, and two voices must not collapse into one.
        var decisions = LiveSpeakerStitcher.Stitch(
            [], [Incoming("SPEAKER_00", 0), Incoming("SPEAKER_01", 90)], Default);

        Assert.All(decisions, d => Assert.True(d.IsNew));
        Assert.Equal(2, decisions.Select(d => d.SessionLabel).Distinct().Count());
    }

    [Fact]
    public void AVoiceCloseToTwoCentroids_WithNoClearWinner_MintsRatherThanGuessing()
    {
        // Sitting between two known voices, near-equidistant. Picking one would be a coin flip
        // presented as a decision, and every later chunk would inherit it.
        var decisions = LiveSpeakerStitcher.Stitch(
            [Known("S1", 0), Known("S2", 20)], [Incoming("SPEAKER_00", 10)], Default);

        Assert.True(Assert.Single(decisions).IsNew);
    }

    [Fact]
    public void ExactlyAtTheThreshold_DoesNotMatch()
    {
        // The boundary is a case, not a rounding detail: accepting AT the threshold makes the setting
        // mean something other than what it says, which is drift an administrator calibrating it cannot
        // see.
        //
        // Built from a 3-4-5 triple rather than from trigonometry, so the distance really is the
        // threshold. Going through Math.Acos and back through a float cast lands *near* 0.35 on
        // whichever side the rounding falls, and a boundary test that is only approximately at the
        // boundary tests nothing - the first version of this passed against both `>=` and `>`.
        var a = Vector((0, 1f));
        var b = Vector((0, 3f), (1, 4f));   // dot 3, norms 1 and 5, so cosine 0.6 and distance exactly 0.4
        Assert.Equal(0.4, LiveSpeakerStitcher.CosineDistance(a, b), 12);

        var t = new StitchThresholds(Threshold: 0.4, Margin: 0.0);
        var decisions = LiveSpeakerStitcher.Stitch(
            [new SessionCentroid("S1", a, 1)], [new ChunkSpeaker("SPEAKER_00", b, 20_000)], t);

        Assert.True(Assert.Single(decisions).IsNew);
    }

    [Fact]
    public void JustInsideTheMargin_DoesNotMatch_AndAtItDoes()
    {
        // The margin boundary follows IdentificationRules.Decide exactly: a gap of *less than* Margin is
        // too ambiguous, a gap of Margin is clear air. That convention is copied rather than chosen -
        // "margin" is one number an administrator calibrates, and having it mean "strictly more than"
        // for naming a person but "at least" for stitching a voice would be a difference nobody could
        // see and everybody would eventually trip over.
        var t = new StitchThresholds(Threshold: 0.9, Margin: 0.05);
        double Degrees(double distance) => Math.Acos(1 - distance) * 180.0 / Math.PI;

        // Gap 0.04: inside the margin, so ambiguous.
        var ambiguous = LiveSpeakerStitcher.Stitch(
            [Known("S1", -Degrees(0.05)), Known("S2", Degrees(0.09))], [Incoming("SPEAKER_00", 0)], t);
        Assert.True(Assert.Single(ambiguous).IsNew, "a gap under the margin is a coin flip");

        // Gap 0.06: clear air.
        var clear = LiveSpeakerStitcher.Stitch(
            [Known("S1", -Degrees(0.05)), Known("S2", Degrees(0.11))], [Incoming("SPEAKER_00", 0)], t);
        Assert.Equal("S1", Assert.Single(clear).SessionLabel);
    }

    [Fact]
    public void ThreeChunkLabelsCanCollapseOntoTwoSessionLabels()
    {
        // Measured during the S0 benchmark: pyannote found THREE speakers in a 20 s clip containing two.
        // Short-window clustering over-segments, so the stitcher must never assume a bijection between
        // chunk labels and session labels - two of these are the same person heard twice.
        var decisions = LiveSpeakerStitcher.Stitch(
            [Known("S1", 0), Known("S2", 90)],
            [Incoming("SPEAKER_00", 3), Incoming("SPEAKER_01", 92), Incoming("SPEAKER_02", 174)],
            Default);

        Assert.Equal("S1", decisions[0].SessionLabel);
        Assert.Equal("S2", decisions[1].SessionLabel);
        Assert.True(decisions[2].IsNew, "a genuinely new voice still mints");
        Assert.Equal(2, decisions.Count(d => !d.IsNew));
    }

    [Fact]
    public void TwoChunkLabelsNeverCollapseOntoTheSameSessionLabelInOneChunk()
    {
        // The opposite error, and the one that reads worst: pyannote already decided these are two
        // different voices *in this chunk*, so merging them puts two people's words under one name.
        // Nearest-centroid on its own would happily do it - both are closest to S1.
        var decisions = LiveSpeakerStitcher.Stitch(
            [Known("S1", 0)], [Incoming("SPEAKER_00", 5), Incoming("SPEAKER_01", 8)], Default);

        Assert.Equal(2, decisions.Select(d => d.SessionLabel).Distinct().Count());
        Assert.Single(decisions, d => d.SessionLabel == "S1");
    }

    [Fact]
    public void TheClosestChunkSpeakerWinsAContestedCentroid()
    {
        // Following from the test above: when two chunk speakers both want S1, the one that is actually
        // closer to it should get it. Handing it to whichever happened to be first would make the result
        // depend on pyannote's arbitrary numbering.
        var decisions = LiveSpeakerStitcher.Stitch(
            [Known("S1", 0)], [Incoming("SPEAKER_00", 12), Incoming("SPEAKER_01", 3)], Default);

        Assert.Equal("S1", decisions.Single(d => d.ChunkLabel == "SPEAKER_01").SessionLabel);
        Assert.True(decisions.Single(d => d.ChunkLabel == "SPEAKER_00").IsNew);
    }

    [Fact]
    public void MintedLabelsDoNotCollideWithLabelsAlreadyInUse()
    {
        // Minting is where a duplicate would silently merge two voices for the rest of the meeting, and
        // the collision is not hypothetical: minting counts up from SPEAKER_00, so the labels it
        // produces are exactly the ones earlier chunks already minted. The known voices here are
        // therefore named in the minting format - an earlier version used "SPEAKER_1", which no amount
        // of counting can ever collide with, and the test passed against a version with no guard at all.
        //
        // Both incoming voices are far from both known ones (90 and 180 degrees), so both must mint.
        var decisions = LiveSpeakerStitcher.Stitch(
            [Known("SPEAKER_00", 0), Known("SPEAKER_01", 0)],
            [Incoming("A", 90), Incoming("B", 180)], Default);

        Assert.All(decisions, d => Assert.True(d.IsNew));
        var all = decisions.Select(d => d.SessionLabel).Concat(["SPEAKER_00", "SPEAKER_01"]).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    // ---- the running centroid ----

    [Fact]
    public void ACentroidIsTheMeanOfWhatHasBeenHeard_Renormalised()
    {
        // It has to stay a unit vector, because it is matched against unit vectors and later ranked in
        // Postgres against enrolled voiceprints that are L2-normalised by the worker. A drifting
        // magnitude would not change cosine distance here, but it would make Speaker.Embedding mean
        // something subtly different from every other embedding in the database.
        var updated = LiveSpeakerStitcher.UpdateCentroid(
            new SessionCentroid("S1", Vector((0, 1f)), 1), Vector((1, 1f)));

        var norm = Math.Sqrt(updated.Centroid.Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, 6);
        // Halfway between the two axes.
        Assert.Equal(updated.Centroid[0], updated.Centroid[1], 6);
        Assert.Equal(2, updated.Samples);
    }

    [Fact]
    public void AddingAVectorMovesTheCentroidTowardIt()
    {
        var before = new SessionCentroid("S1", At(0), 1);
        var after = LiveSpeakerStitcher.UpdateCentroid(before, At(40));

        Assert.True(LiveSpeakerStitcher.CosineDistance(after.Centroid, At(40))
                    < LiveSpeakerStitcher.CosineDistance(before.Centroid, At(40)));
    }

    [Fact]
    public void ACentroidBuiltFromManyChunksIsNotSwungAroundByOneMore()
    {
        // The measured reason, recorded in spec section 6.4: ECAPA on 15-30 s of one voice is noisy, and
        // that noise is the real floor under chunk length. A centroid that let its tenth sample move it
        // as far as its second would inherit every bad chunk in full - so the assertion is about the
        // direction and the diminishing size of the move, never a magic number.
        var young = new SessionCentroid("S1", At(0), 1);
        var old = new SessionCentroid("S1", At(0), 20);

        var youngMove = LiveSpeakerStitcher.CosineDistance(
            LiveSpeakerStitcher.UpdateCentroid(young, At(40)).Centroid, At(0));
        var oldMove = LiveSpeakerStitcher.CosineDistance(
            LiveSpeakerStitcher.UpdateCentroid(old, At(40)).Centroid, At(0));

        Assert.True(oldMove < youngMove,
            $"a well-established centroid moved {oldMove} but a new one moved {youngMove}");
    }

    [Fact]
    public void AnEstablishedCentroidStillMovesAtAll()
    {
        // The opposite failure to the one above, and the one that would make a voice named wrongly early
        // impossible to correct: a centroid that stops moving cannot be improved by later evidence.
        var old = new SessionCentroid("S1", At(0), 50);
        var moved = LiveSpeakerStitcher.UpdateCentroid(old, At(40));

        Assert.True(LiveSpeakerStitcher.CosineDistance(moved.Centroid, At(0)) > 0);
    }
}
