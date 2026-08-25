using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Diariz.Api.Tests;

/// <summary>The assessment clip endpoint - the security boundary of the whole voiceprint-assessment feature.
///
/// <para><c>ManageVoiceprints</c> does not grant "audio from other people's recordings". It grants "audio
/// this person actually spoke", and the span check is the only thing enforcing the difference. Without it the
/// permission would let a holder pull arbitrary offsets out of a colleague's confidential meeting.</para></summary>
public class PeopleClipEndpointTests
{
    private static Vector Unit()
    {
        var v = new float[192];
        v[0] = 1f;
        return new Vector(v);
    }

    private sealed record Fixture(
        PeopleController Controller, FakeAudioClipper Clipper, Guid PersonId, Guid SpeakerId, Guid RecordingId);

    /// <summary>A person with one speaker who spoke from 1s to 12s of a recording.</summary>
    private static Fixture Seed(
        DiarizDbContext db,
        Guid userId,
        bool ownedByCaller = true,
        bool audioDeleted = false,
        bool withTranscription = true)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice" };
        var rec = new Recording
        {
            Id = Guid.NewGuid(),
            UserId = ownedByCaller ? userId : Guid.NewGuid(),
            Title = "Standup",
            BlobKey = "audio/standup.webm",
            AudioDeletedAt = audioDeleted ? DateTimeOffset.UtcNow : null,
        };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(),
            RecordingId = rec.Id,
            Label = "SPEAKER_00",
            DisplayName = "Alice",
            PersonId = person.Id,
            Embedding = Unit(),
        };
        db.AddRange(person, rec, speaker);

        if (withTranscription)
        {
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1 };
            db.AddRange(tr, new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = tr.Id,
                SpeakerLabel = "SPEAKER_00",
                StartMs = 1000,
                EndMs = 12000,
                Original = "hello",
                Ordinal = 0,
            }, new Segment
            {
                // Someone else's audio, immediately after. A span here must never be servable as Alice.
                Id = Guid.NewGuid(),
                TranscriptionId = tr.Id,
                SpeakerLabel = "SPEAKER_01",
                StartMs = 12000,
                EndMs = 20000,
                Original = "goodbye",
                Ordinal = 1,
            });
        }

        db.SaveChanges();
        Perms.Grant(db, userId, PlatformPermission.ManagePeople);
        db.SaveChanges();

        var clipper = new FakeAudioClipper();
        var controller = new PeopleController(
            db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue(),
            clipper, new FakeAudioStorage(), NullLogger<PeopleController>.Instance, new PlatformSettingsService(db))
        {
            ControllerContext = Http.Context(userId),
        };
        return new Fixture(controller, clipper, person.Id, speaker.Id, rec.Id);
    }

    [Fact]
    public async Task Clip_serves_wav_for_a_span_the_person_spoke()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());

        var result = await f.Controller.Clip(f.PersonId, f.SpeakerId, 2000, 5000);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("audio/wav", file.ContentType);
    }

    [Fact]
    public async Task Clip_passes_the_requested_span_and_presigned_url_to_the_clipper()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());

        await f.Controller.Clip(f.PersonId, f.SpeakerId, 2000, 5000);

        var call = Assert.Single(f.Clipper.Calls);
        Assert.Equal(2000, call.FromMs);
        Assert.Equal(5000, call.ToMs);
        Assert.Contains("audio/standup.webm", call.Url);
    }

    [Fact]
    public async Task Clip_for_a_span_outside_any_of_that_speakers_segments_is_not_found()
    {
        // The load-bearing guard. 13000-14000 is real audio in this recording - it is just somebody else's.
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(await f.Controller.Clip(f.PersonId, f.SpeakerId, 13000, 14000));
        Assert.Empty(f.Clipper.Calls);
    }

    [Fact]
    public async Task Clip_for_a_span_straddling_the_end_of_a_segment_is_not_found()
    {
        // Overrunning into the next speaker by a second is exactly how a narrow grant becomes a wide one.
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(await f.Controller.Clip(f.PersonId, f.SpeakerId, 11000, 13000));
    }

    [Fact]
    public async Task Clip_from_another_users_recording_without_ManageVoiceprints_is_forbidden()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid(), ownedByCaller: false);

        Assert.IsType<ForbidResult>(await f.Controller.Clip(f.PersonId, f.SpeakerId, 2000, 5000));
        Assert.Empty(f.Clipper.Calls);
    }

    [Fact]
    public async Task Clip_from_another_users_recording_with_ManageVoiceprints_is_served()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var f = Seed(db, userId, ownedByCaller: false);
        Perms.Grant(db, userId, PlatformPermission.ManageVoiceprints);
        await db.SaveChangesAsync();

        Assert.IsType<FileContentResult>(await f.Controller.Clip(f.PersonId, f.SpeakerId, 2000, 5000));
    }

    [Fact]
    public async Task Clip_still_checks_the_span_when_the_caller_holds_ManageVoiceprints()
    {
        // The permission widens which recordings are reachable, never which audio inside one is.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var f = Seed(db, userId, ownedByCaller: false);
        Perms.Grant(db, userId, PlatformPermission.ManageVoiceprints);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await f.Controller.Clip(f.PersonId, f.SpeakerId, 13000, 14000));
    }

    [Fact]
    public async Task Clip_for_a_speaker_not_attributed_to_this_person_is_not_found()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());
        var other = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = f.RecordingId, Label = "SPEAKER_01", DisplayName = "Bob",
        };
        db.Add(other);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await f.Controller.Clip(f.PersonId, other.Id, 13000, 14000));
    }

    [Fact]
    public async Task Clip_without_permission_over_the_person_is_forbidden()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());
        var stranger = new PeopleController(
            db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue(),
            new FakeAudioClipper(), new FakeAudioStorage(), NullLogger<PeopleController>.Instance, new PlatformSettingsService(db))
        {
            ControllerContext = Http.Context(Guid.NewGuid()),
        };

        Assert.IsType<ForbidResult>(await stranger.Clip(f.PersonId, f.SpeakerId, 2000, 5000));
    }

    [Fact]
    public async Task Clip_when_the_audio_has_been_deleted_is_not_found()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid(), audioDeleted: true);

        Assert.IsType<NotFoundResult>(await f.Controller.Clip(f.PersonId, f.SpeakerId, 2000, 5000));
    }

    [Fact]
    public async Task Clip_when_the_recording_has_no_transcription_is_not_found()
    {
        // Nothing establishes which audio belongs to this speaker, so nothing can be served.
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid(), withTranscription: false);

        Assert.IsType<NotFoundResult>(await f.Controller.Clip(f.PersonId, f.SpeakerId, 2000, 5000));
    }

    [Fact]
    public async Task Clip_for_an_unknown_person_is_not_found()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(await f.Controller.Clip(Guid.NewGuid(), f.SpeakerId, 2000, 5000));
    }

    [Fact]
    public async Task Segments_returns_only_that_speakers_segments()
    {
        // Never the whole transcript. An assessor is granted this person's speech, so handing over the other
        // speakers' words alongside it would undo exactly the narrowing the clip endpoint exists to enforce.
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());

        var rows = Assert.IsAssignableFrom<IReadOnlyList<AttributionSegmentDto>>(
            Assert.IsType<OkObjectResult>((await f.Controller.AttributionSegments(f.PersonId, f.SpeakerId)).Result).Value);

        var row = Assert.Single(rows);
        Assert.Equal(1000, row.StartMs);
        Assert.Equal(12000, row.EndMs);
        Assert.Equal("hello", row.Text);
    }

    [Fact]
    public async Task Segments_from_another_users_recording_without_ManageVoiceprints_is_forbidden()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid(), ownedByCaller: false);

        Assert.IsType<ForbidResult>((await f.Controller.AttributionSegments(f.PersonId, f.SpeakerId)).Result);
    }

    [Fact]
    public async Task Segments_from_another_users_recording_with_ManageVoiceprints_is_served()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var f = Seed(db, userId, ownedByCaller: false);
        Perms.Grant(db, userId, PlatformPermission.ManageVoiceprints);
        await db.SaveChangesAsync();

        var rows = Assert.IsAssignableFrom<IReadOnlyList<AttributionSegmentDto>>(
            Assert.IsType<OkObjectResult>((await f.Controller.AttributionSegments(f.PersonId, f.SpeakerId)).Result).Value);

        Assert.Single(rows);
    }

    [Fact]
    public async Task Segments_for_a_speaker_not_attributed_to_this_person_is_not_found()
    {
        using var db = TestDb.Create();
        var f = Seed(db, Guid.NewGuid());
        var other = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = f.RecordingId, Label = "SPEAKER_01", DisplayName = "Bob",
        };
        db.Add(other);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(
            (await f.Controller.AttributionSegments(f.PersonId, other.Id)).Result);
    }
}
