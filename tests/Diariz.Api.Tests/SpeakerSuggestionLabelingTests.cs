using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Pgvector;

namespace Diariz.Api.Tests;

/// <summary>What a <see cref="IdentificationOutcome.Suggest"/> verdict does to a speaker row.
///
/// <para>The load-bearing property is that a suggestion is <b>not</b> an identification. The speaker stays
/// anonymous and unlinked while one is pending, so no transcript, export, summary or search result treats a
/// maybe as a fact.</para></summary>
public class SpeakerSuggestionLabelingTests
{
    private static readonly IdentificationThresholds T = new(0.30, 0.40, 0.05, 3000);
    private static readonly Dictionary<string, long> Speech = new() { ["SPEAKER_00"] = 30_000 };

    private static Speaker Anonymous() => new()
    {
        Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), Label = "SPEAKER_00",
        DisplayName = "SPEAKER_00", Embedding = new Vector(new[] { 0.1f }),
    };

    private static FakeSpeakerIdentifier At(Guid personId, double distance)
    {
        var id = new FakeSpeakerIdentifier();
        id.Nearest(personId, "Alice", distance);
        return id;
    }

    [Fact]
    public async Task A_borderline_match_is_suggested_without_naming_the_speaker()
    {
        var sp = Anonymous();
        var personId = Guid.NewGuid();

        await SpeakerLabeling.ApplyAsync([sp], At(personId, 0.35), T, Speech);

        Assert.Equal(personId, sp.SuggestedPersonId);
        Assert.Equal(0.35, sp.SuggestedDistance!.Value, 3);
        Assert.NotNull(sp.SuggestedAt);
        // The whole point: still anonymous, still unlinked.
        Assert.Null(sp.PersonId);
        Assert.Equal("SPEAKER_00", sp.DisplayName);
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task An_accepted_match_carries_no_suggestion()
    {
        // It was applied, so there is nothing left to ask about. A leftover suggestion would show up in the
        // review queue asking whether a speaker is the person it is already named as.
        var sp = Anonymous();

        await SpeakerLabeling.ApplyAsync([sp], At(Guid.NewGuid(), 0.10), T, Speech);

        Assert.NotNull(sp.PersonId);
        Assert.Null(sp.SuggestedPersonId);
    }

    [Fact]
    public async Task A_distant_match_is_not_suggested()
    {
        var sp = Anonymous();

        await SpeakerLabeling.ApplyAsync([sp], At(Guid.NewGuid(), 0.80), T, Speech);

        Assert.Null(sp.SuggestedPersonId);
    }

    [Fact]
    public async Task A_new_verdict_replaces_a_stale_suggestion()
    {
        // Re-identifying after enrolling someone else must not leave the previous guess sitting there.
        var sp = Anonymous();
        sp.SuggestedPersonId = Guid.NewGuid();
        sp.SuggestedDistance = 0.39;
        sp.SuggestedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var newer = Guid.NewGuid();

        await SpeakerLabeling.ApplyAsync([sp], At(newer, 0.33), T, Speech);

        Assert.Equal(newer, sp.SuggestedPersonId);
        Assert.Equal(0.33, sp.SuggestedDistance!.Value, 3);
    }

    [Fact]
    public async Task A_verdict_of_nothing_clears_a_stale_suggestion()
    {
        // The gallery moved and the previous guess no longer holds. Leaving it would keep asking a question
        // the system itself no longer has a reason to ask.
        var sp = Anonymous();
        sp.SuggestedPersonId = Guid.NewGuid();
        sp.SuggestedDistance = 0.39;
        sp.SuggestedAt = DateTimeOffset.UtcNow.AddDays(-1);

        await SpeakerLabeling.ApplyAsync([sp], new FakeSpeakerIdentifier(), T, Speech);

        Assert.Null(sp.SuggestedPersonId);
        Assert.Null(sp.SuggestedDistance);
        Assert.Null(sp.SuggestedAt);
    }

    [Fact]
    public async Task A_pair_that_was_already_rejected_is_never_suggested_again()
    {
        // Declining a suggestion has to mean it stays declined. Without this, every re-scan would offer the
        // same wrong guess back, and the review queue would be unclearable by construction.
        var sp = Anonymous();
        var personId = Guid.NewGuid();

        await SpeakerLabeling.ApplyAsync(
            [sp], At(personId, 0.35), T, Speech,
            rejected: new HashSet<(Guid, Guid)> { (sp.Id, personId) });

        Assert.Null(sp.SuggestedPersonId);
    }

    [Fact]
    public async Task A_rejection_of_someone_else_does_not_block_this_suggestion()
    {
        var sp = Anonymous();
        var personId = Guid.NewGuid();

        await SpeakerLabeling.ApplyAsync(
            [sp], At(personId, 0.35), T, Speech,
            rejected: new HashSet<(Guid, Guid)> { (sp.Id, Guid.NewGuid()) });

        Assert.Equal(personId, sp.SuggestedPersonId);
    }

    [Fact]
    public async Task A_rejected_pair_can_still_be_accepted_automatically()
    {
        // Rejecting a *suggestion* says the guess was not good enough to accept on that evidence. If the
        // voiceprint later improves to the point of an outright match, that is new evidence, and continuing
        // to withhold it would make one dismissal permanent.
        var sp = Anonymous();
        var personId = Guid.NewGuid();

        await SpeakerLabeling.ApplyAsync(
            [sp], At(personId, 0.10), T, Speech,
            rejected: new HashSet<(Guid, Guid)> { (sp.Id, personId) });

        Assert.Equal(personId, sp.PersonId);
    }

    [Fact]
    public async Task A_manually_named_speaker_is_never_suggested_at()
    {
        var sp = Anonymous();
        sp.DisplayName = "Bob";

        await SpeakerLabeling.ApplyAsync([sp], At(Guid.NewGuid(), 0.35), T, Speech);

        Assert.Null(sp.SuggestedPersonId);
        Assert.Equal("Bob", sp.DisplayName);
    }
}
