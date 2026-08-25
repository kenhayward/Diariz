using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Pgvector;

namespace Diariz.Api.Tests;

/// <summary>Listing every speaker attributed to a person, and toggling one in or out of training.
///
/// <para>The list is deliberately larger than the person's voice samples: automatic identification links a
/// speaker without creating one, so a sample-only list hid most of what a voiceprint is being used
/// against.</para></summary>
public class PeopleAttributionEndpointTests
{
    private static Vector Unit()
    {
        var v = new float[192];
        v[0] = 1f;
        return new Vector(v);
    }

    private static PeopleController Build(DiarizDbContext db, Guid userId) =>
        new(db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue(),
            new FakeAudioClipper(), new FakeAudioStorage(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PeopleController>.Instance)
        {
            ControllerContext = Http.Context(userId),
        };

    /// <summary>A person with one auto-identified speaker in a recording carrying 12s of their speech.</summary>
    private static (PeopleController controller, Guid personId, Guid speakerId, Guid recordingId) Seed(
        DiarizDbContext db, Guid userId, bool ownedByCaller = true, bool withEmbedding = true)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice" };
        var rec = new Recording
        {
            Id = Guid.NewGuid(),
            UserId = ownedByCaller ? userId : Guid.NewGuid(),
            Title = "Standup",
            BlobKey = "k",
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1 };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(),
            RecordingId = rec.Id,
            Label = "SPEAKER_00",
            DisplayName = "Alice",
            PersonId = person.Id,
            IdentifiedAuto = true,
            Embedding = withEmbedding ? Unit() : null,
        };
        db.AddRange(person, rec, tr, speaker, new Segment
        {
            Id = Guid.NewGuid(),
            TranscriptionId = tr.Id,
            SpeakerLabel = "SPEAKER_00",
            StartMs = 0,
            EndMs = 12000,
            Original = "hello",
            Ordinal = 0,
        });
        db.SaveChanges();
        Perms.Grant(db, userId, PlatformPermission.ManagePeople);
        db.SaveChanges();

        return (Build(db, userId), person.Id, speaker.Id, rec.Id);
    }

    private static IReadOnlyList<PersonAttributionDto> Rows(ActionResult<IReadOnlyList<PersonAttributionDto>> r) =>
        Assert.IsAssignableFrom<IReadOnlyList<PersonAttributionDto>>(
            Assert.IsType<OkObjectResult>(r.Result).Value);

    [Fact]
    public async Task Attributions_lists_an_auto_identified_speaker_that_has_no_sample()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);

        var row = Assert.Single(Rows(await controller.Attributions(personId)));

        Assert.Equal(speakerId, row.SpeakerId);
        Assert.False(row.IsTraining);
        Assert.Equal("auto", row.LinkedBy);
        Assert.True(row.CanAccessRecording);
    }

    [Fact]
    public async Task Attributions_sums_the_speakers_speech_from_the_current_transcription()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, _, _) = Seed(db, userId);

        Assert.Equal(12000, Assert.Single(Rows(await controller.Attributions(personId))).SpeechMs);
    }

    [Fact]
    public async Task Attributions_marks_another_users_recording_inaccessible_without_ManageVoiceprints()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, _, _) = Seed(db, userId, ownedByCaller: false);

        Assert.False(Assert.Single(Rows(await controller.Attributions(personId))).CanAccessRecording);
    }

    [Fact]
    public async Task Attributions_marks_another_users_recording_accessible_with_ManageVoiceprints()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, _, _) = Seed(db, userId, ownedByCaller: false);
        Perms.Grant(db, userId, PlatformPermission.ManageVoiceprints);
        await db.SaveChangesAsync();

        Assert.True(Assert.Single(Rows(await controller.Attributions(personId))).CanAccessRecording);
    }

    [Fact]
    public async Task Attributions_without_ManagePeople_is_forbidden()
    {
        using var db = TestDb.Create();
        var (_, personId, _, _) = Seed(db, Guid.NewGuid());
        var stranger = Build(db, Guid.NewGuid());

        Assert.IsType<ForbidResult>((await stranger.Attributions(personId)).Result);
    }

    [Fact]
    public async Task Attributions_for_an_unknown_person_is_not_found()
    {
        using var db = TestDb.Create();
        var (controller, _, _, _) = Seed(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>((await controller.Attributions(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task SetTraining_on_creates_a_sample_from_the_speakers_existing_embedding()
    {
        // No worker round trip: the speaker's embedding was computed at transcription time, so enrolling a
        // whole speaker is a database write. Only a span subset needs the re-embed job.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);

        Assert.IsType<NoContentResult>(
            await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true)));

        var sample = Assert.Single(db.VoiceSamples.Where(v => v.PersonId == personId));
        Assert.Equal(speakerId, sample.SpeakerId);
        Assert.Null(sample.ExcludedAt);
    }

    [Fact]
    public async Task SetTraining_off_excludes_rather_than_deletes()
    {
        // The row records that a human asserted this speaker was this person. Deleting it loses that, and
        // frees a later re-scan to silently re-add what someone deliberately removed.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(false));

        Assert.NotNull(Assert.Single(db.VoiceSamples.Where(v => v.PersonId == personId)).ExcludedAt);
    }

    [Fact]
    public async Task SetTraining_on_again_reuses_the_excluded_sample()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));
        var sampleId = db.VoiceSamples.Single().Id;
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(false));

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        var sample = Assert.Single(db.VoiceSamples.Where(v => v.PersonId == personId));
        Assert.Equal(sampleId, sample.Id);
        Assert.Null(sample.ExcludedAt);
    }

    [Fact]
    public async Task SetTraining_stores_ExcludedAt_in_utc()
    {
        // Npgsql throws at SaveChanges on a non-zero-offset DateTimeOffset for a timestamptz column, and the
        // in-memory provider will not catch it. Asserting the offset here makes that a unit failure rather
        // than a 500 on real Postgres.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(false));

        Assert.Equal(TimeSpan.Zero, db.VoiceSamples.Single().ExcludedAt!.Value.Offset);
    }

    [Fact]
    public async Task SetTraining_reflects_in_the_attribution_row()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        var row = Assert.Single(Rows(await controller.Attributions(personId)));
        Assert.True(row.IsTraining);
        Assert.NotNull(row.VoiceSampleId);
    }

    [Fact]
    public async Task SetTraining_on_a_speaker_not_attributed_to_this_person_is_not_found()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, _, _) = Seed(db, userId);
        var other = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), Label = "SPEAKER_09", DisplayName = "Bob",
        };
        db.Add(other);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(
            await controller.SetTraining(personId, other.Id, new SetTrainingRequest(true)));
    }

    [Fact]
    public async Task SetTraining_on_an_opted_out_person_is_a_conflict()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        db.People.Single(p => p.Id == personId).VoiceprintOptOut = true;
        await db.SaveChangesAsync();

        Assert.IsType<ConflictObjectResult>(
            await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true)));
    }

    [Fact]
    public async Task SetTraining_on_a_speaker_with_no_embedding_is_a_bad_request()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId, withEmbedding: false);

        Assert.IsType<BadRequestObjectResult>(
            await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true)));
    }

    [Fact]
    public async Task Excluding_a_sample_removes_it_from_the_centroid()
    {
        // Without this the toggle is cosmetic: the row reads "not training" while the vector it contributed
        // is still inside the average.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(false));

        Assert.Equal(0, db.People.Single(p => p.Id == personId).SampleCount);
    }
}
