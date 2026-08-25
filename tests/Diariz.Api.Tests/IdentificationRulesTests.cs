using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Accept, suggest or ignore - the whole of identification's policy, with no database in sight.
///
/// <para>Extracted as a pure function precisely so the operating point can be reasoned about and, in a later
/// phase, swept across the stored decision log without touching pgvector.</para></summary>
public class IdentificationRulesTests
{
    private static readonly IdentificationThresholds T =
        new(Accept: 0.40, Suggest: 0.50, Margin: 0.05, MinSpeechMs: 3000);

    private static RankedCandidate C(double d, string name = "Alice") => new(Guid.NewGuid(), name, d);

    [Fact]
    public void Inside_the_threshold_is_accepted()
    {
        var v = IdentificationRules.Decide([C(0.30), C(0.80)], 30_000, T);

        Assert.Equal(IdentificationOutcome.Accept, v.Outcome);
        Assert.Equal(0.30, v.Match!.Distance, 3);
    }

    [Fact]
    public void Between_the_two_thresholds_is_suggested()
    {
        var v = IdentificationRules.Decide([C(0.45), C(0.80)], 30_000, T);

        Assert.Equal(IdentificationOutcome.Suggest, v.Outcome);
        Assert.NotNull(v.Match);
    }

    [Fact]
    public void Beyond_the_band_is_ignored()
    {
        var v = IdentificationRules.Decide([C(0.65), C(0.80)], 30_000, T);

        Assert.Equal(IdentificationOutcome.Ignore, v.Outcome);
        Assert.Null(v.Match);
    }

    [Fact]
    public void Exactly_on_the_threshold_is_accepted()
    {
        // Inclusive, matching how the identifier compared before this existed. An off-by-one here silently
        // moves the operating point away from the one the live distance distribution was measured against.
        Assert.Equal(
            IdentificationOutcome.Accept,
            IdentificationRules.Decide([C(0.40), C(0.90)], 30_000, T).Outcome);
    }

    [Fact]
    public void Exactly_on_the_band_is_suggested()
    {
        Assert.Equal(
            IdentificationOutcome.Suggest,
            IdentificationRules.Decide([C(0.50), C(0.90)], 30_000, T).Outcome);
    }

    [Fact]
    public void A_runner_up_too_close_is_refused()
    {
        // Two similar voices where the nearest is close to a coin-flip. Applying either would be a guess
        // wearing a name, and a wrong name is worse than no name.
        var v = IdentificationRules.Decide([C(0.30, "Alice"), C(0.33, "Bob")], 30_000, T);

        Assert.Equal(IdentificationOutcome.Ignore, v.Outcome);
    }

    [Fact]
    public void The_margin_is_measured_against_the_next_person_not_the_next_template()
    {
        // The load-bearing one. `ranked` carries one entry per person precisely so that two templates of the
        // same human - their office voice and their car voice, which a later phase introduces - can never
        // look like a confusable pair and veto a correct match.
        var alice = Guid.NewGuid();

        var v = IdentificationRules.Decide(
            [new RankedCandidate(alice, "Alice", 0.30), new RankedCandidate(Guid.NewGuid(), "Bob", 0.90)],
            30_000, T);

        Assert.Equal(IdentificationOutcome.Accept, v.Outcome);
        Assert.Equal(alice, v.Match!.PersonId);
    }

    [Fact]
    public void A_lone_candidate_has_no_runner_up_to_beat()
    {
        Assert.Equal(
            IdentificationOutcome.Accept,
            IdentificationRules.Decide([C(0.30)], 30_000, T).Outcome);
    }

    [Fact]
    public void Too_little_speech_is_ignored_however_close_the_match()
    {
        // A 1.5s utterance produces an embedding the model has no business being confident about. Scoring it
        // anyway is how a voiceprint comes to learn from noise.
        Assert.Equal(
            IdentificationOutcome.Ignore,
            IdentificationRules.Decide([C(0.05)], 1_500, T).Outcome);
    }

    [Fact]
    public void Exactly_the_minimum_speech_is_enough()
    {
        Assert.Equal(
            IdentificationOutcome.Accept,
            IdentificationRules.Decide([C(0.05)], 3_000, T).Outcome);
    }

    [Fact]
    public void An_empty_gallery_is_ignored()
    {
        Assert.Equal(IdentificationOutcome.Ignore, IdentificationRules.Decide([], 30_000, T).Outcome);
    }

    [Fact]
    public void The_margin_applies_to_a_suggestion_too()
    {
        // A borderline match that is also ambiguous is worse than either alone - it would put a coin-flip in
        // front of someone as though it were a considered guess, and their answer becomes training data.
        Assert.Equal(
            IdentificationOutcome.Ignore,
            IdentificationRules.Decide([C(0.45, "Alice"), C(0.47, "Bob")], 30_000, T).Outcome);
    }

    [Fact]
    public void Comfortably_more_than_the_margin_apart_is_enough()
    {
        // Deliberately not the exact boundary. The margin is a subtraction of two float64 distances, so
        // 0.35 - 0.30 is 0.049999999999999996 and "exactly the margin" is not a representable case -
        // asserting it would pin an artefact of the representation rather than the rule.
        Assert.Equal(
            IdentificationOutcome.Accept,
            IdentificationRules.Decide([C(0.30, "Alice"), C(0.40, "Bob")], 30_000, T).Outcome);
    }

    [Fact]
    public void From_reads_the_platform_settings()
    {
        var t = IdentificationThresholds.From(new PlatformSettings());

        Assert.Equal(0.30, t.Accept, 3);
        Assert.Equal(0.40, t.Suggest, 3);
        Assert.Equal(0.05, t.Margin, 3);
        Assert.Equal(3000, t.MinSpeechMs);
    }
}
