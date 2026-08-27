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

    private static SpeakerSuggestionsController Build(
        DiarizDbContext db, Guid userId, FakeAudioClipper? clipper = null,
        FakeAudioStorage? storage = null, FakeJobQueue? queue = null) =>
        new(db, new SpeakerAssignment(db, new PeopleDirectory(db)),
            clipper ?? new FakeAudioClipper(), storage ?? new FakeAudioStorage(),
            queue ?? new FakeJobQueue())
        {
            ControllerContext = Http.Context(userId),
        };

    private sealed record Seeded(Guid SpeakerId, Guid PersonId, Guid RecordingId);

    /// <summary>A pending suggestion on an anonymous speaker in a recording owned by <paramref name="userId"/>
    /// (unless <paramref name="ownedByCaller"/> says otherwise).</summary>
    private static Seeded Seed(
        DiarizDbContext db, Guid userId, bool ownedByCaller = true, bool withSuggestion = true,
        bool optedOut = false, bool audioDeleted = false)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice", VoiceprintOptOut = optedOut };
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = ownedByCaller ? userId : Guid.NewGuid(),
            Title = "Standup", BlobKey = "k",
            AudioDeletedAt = audioDeleted ? DateTimeOffset.UtcNow : null,
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

    // ---- Audio is what makes the question answerable ----

    [Fact]
    public async Task Pending_does_not_ask_about_a_recording_whose_audio_is_gone()
    {
        // The only honest way to answer "is this speaker that person?" is to listen. Once the retention
        // sweep has taken the audio there is nothing to listen to, so the row is not a question - it is a
        // permanent occupant of the queue. The 0.257.0 exemption covers audio behind an *enrolled* sample;
        // a pending suggestion is by definition not enrolled, so these accumulate.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Seed(db, userId, audioDeleted: true);

        Assert.Empty(Rows(await Build(db, userId).Pending()));
    }

    // ---- The evidence behind one suggestion ----

    private static IReadOnlyList<AttributionSegmentDto> Segs(
        ActionResult<IReadOnlyList<AttributionSegmentDto>> r) =>
        Assert.IsAssignableFrom<IReadOnlyList<AttributionSegmentDto>>(
            Assert.IsType<OkObjectResult>(r.Result).Value);

    [Fact]
    public async Task Segments_lists_what_the_suggested_speaker_said()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);

        var seg = Assert.Single(Segs(await Build(db, userId).Segments(s.SpeakerId)));

        Assert.Equal("hello", seg.Text);
        Assert.Equal(0, seg.StartMs);
        Assert.Equal(30000, seg.EndMs);
    }

    [Fact]
    public async Task Segments_returns_only_that_speakers_words()
    {
        // Never the recording's transcript. The queue answers one narrow question, and everybody else in
        // the meeting stays out of reach - the same line the assessment endpoints hold.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);
        var trId = db.Transcriptions.Single(t => t.RecordingId == s.RecordingId).Id;
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = trId, SpeakerLabel = "SPEAKER_01",
            StartMs = 30000, EndMs = 40000, Original = "someone else", Ordinal = 1,
        });
        db.SaveChanges();

        var seg = Assert.Single(Segs(await Build(db, userId).Segments(s.SpeakerId)));
        Assert.Equal("hello", seg.Text);
    }

    [Fact]
    public async Task Segments_does_not_open_another_users_recording()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId, ownedByCaller: false);

        Assert.IsType<NotFoundResult>(
            (await Build(db, userId).Segments(s.SpeakerId)).Result);
    }

    [Fact]
    public async Task Segments_refuses_a_speaker_with_nothing_pending()
    {
        // The endpoint exists to serve the queue. Without that bound it would read any of your own
        // speakers' words through a route that carries none of the recording's own checks.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId, withSuggestion: false);

        Assert.IsType<NotFoundResult>(
            (await Build(db, userId).Segments(s.SpeakerId)).Result);
    }

    // ---- Listening ----

    [Fact]
    public async Task Clip_cuts_the_span_asked_for()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);
        var clipper = new FakeAudioClipper();

        var result = await Build(db, userId, clipper).Clip(s.SpeakerId, 1000, 4000);

        Assert.IsType<FileContentResult>(result);
        Assert.Equal((1000, 4000), (clipper.Calls.Single().FromMs, clipper.Calls.Single().ToMs));
    }

    [Fact]
    public async Task Clip_refuses_a_span_outside_the_speakers_own_segments()
    {
        // Arbitrary offsets into a meeting are not on offer. The suggestion names one speaker; the audio it
        // unlocks is that speaker's and no more.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);
        var clipper = new FakeAudioClipper();

        Assert.IsType<NotFoundResult>(await Build(db, userId, clipper).Clip(s.SpeakerId, 40000, 45000));
        Assert.Empty(clipper.Calls);
    }

    [Fact]
    public async Task Clip_refuses_when_the_audio_has_been_deleted()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId, audioDeleted: true);

        Assert.IsType<NotFoundResult>(await Build(db, userId).Clip(s.SpeakerId, 0, 1000));
    }

    [Fact]
    public async Task Clip_does_not_open_another_users_recording()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId, ownedByCaller: false);

        Assert.IsType<NotFoundResult>(await Build(db, userId).Clip(s.SpeakerId, 0, 1000));
    }

    // ---- Accepting only part of a speaker ----
    //
    // A diarization label is not always one human: two people on one microphone, or a crosstalk stretch,
    // land under a single SPEAKER_nn. Saying yes to the voice while excluding the segments that are not
    // them has to reach the voiceprint, or the exclusion is a control that lies - the enrolment would take
    // in exactly the audio the user just said was somebody else.

    [Fact]
    public async Task Accept_trains_from_only_the_segments_that_were_kept()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);
        var queue = new FakeJobQueue();

        await Build(db, userId, queue: queue)
            .Accept(s.SpeakerId, new AcceptSuggestionRequest([new VoiceprintSpan(0, 10_000)]));

        var sample = db.VoiceSamples.Single(v => v.SpeakerId == s.SpeakerId);
        var spans = VoiceprintSpans.Parse(sample.SpansJson);
        Assert.Equal((0L, 10_000L), (Assert.Single(spans).StartMs, Assert.Single(spans).EndMs));
        // Re-embedded from what is left, not left carrying the embedding of the whole speaker.
        Assert.Null(sample.UsedMs);
        Assert.NotNull(sample.RecomputeQueuedAt);
        Assert.Equal(sample.Id, Assert.Single(queue.VoiceprintJobs).VoiceSampleId);
    }

    [Fact]
    public async Task Accept_with_nothing_excluded_still_trains_from_the_whole_speaker()
    {
        // The overwhelmingly common case. Null spans is "the whole speaker" - an empty array would mean
        // "train on nothing" - and it must not queue a re-embed that changes nothing.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);
        var queue = new FakeJobQueue();

        await Build(db, userId, queue: queue).Accept(s.SpeakerId);

        Assert.Null(db.VoiceSamples.Single(v => v.SpeakerId == s.SpeakerId).SpansJson);
        Assert.Empty(queue.VoiceprintJobs);
    }

    [Fact]
    public async Task Accept_with_spans_still_names_the_speaker()
    {
        // Naming and training are separate decisions. Excluding some audio narrows what the voiceprint
        // learns from; it does not make the answer to "is this that person?" any less of a yes.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId);

        await Build(db, userId).Accept(s.SpeakerId, new AcceptSuggestionRequest([new VoiceprintSpan(0, 5_000)]));

        var sp = db.Speakers.Single(x => x.Id == s.SpeakerId);
        Assert.Equal(s.PersonId, sp.PersonId);
        Assert.Equal("Alice", sp.DisplayName);
        Assert.Null(sp.SuggestedPersonId);
    }

    [Fact]
    public async Task Accept_with_spans_names_an_opted_out_person_without_enrolling_them()
    {
        // No sample exists to shape, and none must be created. Saying "that was Alice" is your assertion
        // about the meeting; holding Alice's biometric after she asked you not to is what she opted out of.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var s = Seed(db, userId, optedOut: true);
        var queue = new FakeJobQueue();

        await Build(db, userId, queue: queue)
            .Accept(s.SpeakerId, new AcceptSuggestionRequest([new VoiceprintSpan(0, 5_000)]));

        Assert.Equal(s.PersonId, db.Speakers.Single(x => x.Id == s.SpeakerId).PersonId);
        Assert.Empty(db.VoiceSamples.Where(v => v.SpeakerId == s.SpeakerId));
        Assert.Empty(queue.VoiceprintJobs);
    }
}
