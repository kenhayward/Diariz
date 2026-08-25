using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Pgvector;

namespace Diariz.Api.Tests;

/// <summary>The review queue, and the two decisions it exists to collect.
///
/// <para>Scoped to the caller's <b>own</b> recordings, deliberately. A suggestion asks "is this speaker in
/// this recording that person?", and the people who can answer it were in the meeting. A platform-wide queue
/// would show an administrator who appears in every meeting in the instance, which is the same disclosure the
/// Manage people permission exists to gate on the directory.</para></summary>
public class SpeakerSuggestionsEndpointTests
{
    private static Vector Unit()
    {
        var v = new float[192];
        v[0] = 1f;
        return new Vector(v);
    }

    private static SpeakerSuggestionsController Build(DiarizDbContext db, Guid userId) =>
        new(db, new SpeakerAssignment(db, new PeopleDirectory(db)))
        {
            ControllerContext = Http.Context(userId),
        };

    private sealed record Seeded(Guid SpeakerId, Guid PersonId, Guid RecordingId);

    /// <summary>A pending suggestion on an anonymous speaker in a recording owned by <paramref name="userId"/>
    /// (unless <paramref name="ownedByCaller"/> says otherwise).</summary>
    private static Seeded Seed(
        DiarizDbContext db, Guid userId, bool ownedByCaller = true, bool withSuggestion = true,
        bool optedOut = false)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice", VoiceprintOptOut = optedOut };
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = ownedByCaller ? userId : Guid.NewGuid(),
            Title = "Standup", BlobKey = "k",
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1 };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
            Embedding = Unit(),
            SuggestedPersonId = withSuggestion ? person.Id : null,
            SuggestedDistance = withSuggestion ? 0.35 : null,
            SuggestedAt = withSuggestion ? DateTimeOffset.UtcNow : null,
        };
        db.AddRange(person, rec, tr, speaker, new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 30000, Original = "hello", Ordinal = 0,
        });
        db.SaveChanges();
        return new Seeded(speaker.Id, person.Id, rec.Id);
    }

    private static IReadOnlyList<SpeakerSuggestionDto> Rows(
        ActionResult<IReadOnlyList<SpeakerSuggestionDto>> r) =>
        Assert.IsAssignableFrom<IReadOnlyList<SpeakerSuggestionDto>>(
            Assert.IsType<OkObjectResult>(r.Result).Value);

    [Fact]
    public async Task Pending_lists_a_suggestion_with_what_is_needed_to_judge_it()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);

        var row = Assert.Single(Rows(await Build(db, userId).Pending()));

        Assert.Equal(s.SpeakerId, row.SpeakerId);
        Assert.Equal(s.PersonId, row.PersonId);
        Assert.Equal("Alice", row.PersonName);
        Assert.Equal("Standup", row.RecordingName);
        Assert.Equal("SPEAKER_00", row.SpeakerLabel);
        Assert.Equal(0.35, row.Distance, 3);
        Assert.Equal(30000, row.SpeechMs);
    }

    [Fact]
    public async Task Pending_does_not_show_another_users_recording()
    {
        // The privacy line: a queue that spanned the platform would tell whoever holds it who appears in
        // every meeting in the instance.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Seed(db, userId, ownedByCaller: false);

        Assert.Empty(Rows(await Build(db, userId).Pending()));
    }

    [Fact]
    public async Task Pending_ignores_a_speaker_with_no_suggestion()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Seed(db, userId, withSuggestion: false);

        Assert.Empty(Rows(await Build(db, userId).Pending()));
    }

    [Fact]
    public async Task Accept_names_the_speaker_and_clears_the_suggestion()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);

        Assert.IsType<NoContentResult>(await Build(db, userId).Accept(s.SpeakerId));

        var sp = db.Speakers.Single(x => x.Id == s.SpeakerId);
        Assert.Equal(s.PersonId, sp.PersonId);
        Assert.Equal("Alice", sp.DisplayName);
        Assert.Null(sp.SuggestedPersonId);
        // Confirmed by a human, so not an automatic label - re-identification must never revoke it.
        Assert.False(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task Accept_enrols_the_speaker_into_the_voiceprint()
    {
        // Confirming is how the system learns a condition it was previously unsure about. Under clustering
        // an acoustically distant sample becomes its own template rather than blurring the existing ones.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);

        await Build(db, userId).Accept(s.SpeakerId);

        Assert.Single(db.VoiceSamples.Where(v => v.PersonId == s.PersonId && v.SpeakerId == s.SpeakerId));
    }

    [Fact]
    public async Task Accept_on_an_opted_out_person_names_them_but_enrols_nothing()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId, optedOut: true);

        await Build(db, userId).Accept(s.SpeakerId);

        Assert.Equal(s.PersonId, db.Speakers.Single(x => x.Id == s.SpeakerId).PersonId);
        Assert.Empty(db.VoiceSamples.Where(v => v.PersonId == s.PersonId));
    }

    [Fact]
    public async Task Accept_records_the_decision_with_the_distance_that_was_offered()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);

        await Build(db, userId).Accept(s.SpeakerId);

        var decision = db.SpeakerIdentityDecisions.Single();
        Assert.Equal(IdentityDecisionKind.Accepted, decision.Decision);
        Assert.Equal(0.35, decision.Distance, 3);
        Assert.Equal(userId, decision.DecidedByUserId);
    }

    [Fact]
    public async Task Reject_clears_the_suggestion_and_leaves_the_speaker_anonymous()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);

        Assert.IsType<NoContentResult>(await Build(db, userId).Reject(s.SpeakerId));

        var sp = db.Speakers.Single(x => x.Id == s.SpeakerId);
        Assert.Null(sp.SuggestedPersonId);
        Assert.Null(sp.PersonId);
        Assert.Equal("SPEAKER_00", sp.DisplayName);
    }

    [Fact]
    public async Task Reject_records_a_labelled_negative()
    {
        // The only source of hard negatives the platform has. Every manually linked speaker is a positive,
        // so without these a threshold sweep has nothing to measure false accepts against.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);

        await Build(db, userId).Reject(s.SpeakerId);

        var decision = db.SpeakerIdentityDecisions.Single();
        Assert.Equal(IdentityDecisionKind.Rejected, decision.Decision);
        Assert.Equal(s.PersonId, decision.PersonId);
        Assert.Equal(0.35, decision.Distance, 3);
    }

    [Fact]
    public async Task Accept_on_another_users_recording_is_not_found()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId, ownedByCaller: false);

        Assert.IsType<NotFoundResult>(await Build(db, userId).Accept(s.SpeakerId));
    }

    [Fact]
    public async Task Reject_on_another_users_recording_is_not_found()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId, ownedByCaller: false);

        Assert.IsType<NotFoundResult>(await Build(db, userId).Reject(s.SpeakerId));
    }

    [Fact]
    public async Task Deciding_twice_is_not_an_error()
    {
        // Two browser tabs, or a double click. The second call finds nothing pending, which is the state the
        // caller wanted - answering it with a 500 would be describing the server's surprise, not a problem.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);
        await Build(db, userId).Reject(s.SpeakerId);

        Assert.IsType<NoContentResult>(await Build(db, userId).Reject(s.SpeakerId));
        Assert.Single(db.SpeakerIdentityDecisions);
    }

    [Fact]
    public async Task Deciding_on_an_unknown_speaker_is_not_found()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Seed(db, userId);

        Assert.IsType<NotFoundResult>(await Build(db, userId).Accept(Guid.NewGuid()));
    }
}
