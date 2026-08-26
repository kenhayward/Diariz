using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>Whether a re-embed is in flight, and whether the last one failed.
///
/// <para>Both were previously inferred from <c>UsedMs</c> and <c>SpansJson</c>, which cannot express
/// either. Selecting the whole speaker serialises to a <b>null</b> <c>SpansJson</c> - null means "the whole
/// speaker" - so <c>pending</c> evaluated to false for the commonest case and the UI said nothing at all
/// when the button was pressed. Failure was worse: the callback wrote <c>UsedMs = 0</c> purely to stop the
/// row spinning, which reported success with a duration of zero.</para>
///
/// <para>Measured live before the fix: 157 of 159 training samples had a null <c>UsedMs</c>, because a
/// sample enrolled straight from a speaker is never re-embedded - so <c>UsedMs</c> alone cannot mean
/// "pending" either.</para></summary>
public class VoiceprintRecomputeStateTests
{
    private static WorkerVoiceprintCallbackController Callback(Diariz.Domain.DiarizDbContext db) =>
        new(db, new Diariz.Api.Services.PeopleDirectory(db),
            Options.Create(new Diariz.Api.Configuration.WorkerOptions { CallbackSecret = "s" }),
            NullLogger<WorkerVoiceprintCallbackController>.Instance)
        {
            ControllerContext = Http.Context(null, ("X-Worker-Secret", "s")),
        };

    private static async Task<VoiceSample> SeedAsync(Diariz.Domain.DiarizDbContext db)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), BlobKey = "k" };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada",
            PersonId = person.Id,
        };
        var sample = new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
            UsedMs = 4000, RecomputeQueuedAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(person, rec, speaker, sample);
        await db.SaveChangesAsync();
        return sample;
    }

    [Fact]
    public async Task A_finished_recompute_stops_being_pending()
    {
        using var db = TestDb.Create();
        var sample = await SeedAsync(db);

        var result = await Callback(db).Result(new VoiceprintResult(sample.Id, [0.5f], 1234, 2000));

        Assert.IsType<NoContentResult>(result);
        var after = await db.VoiceSamples.SingleAsync(v => v.Id == sample.Id);
        Assert.Null(after.RecomputeQueuedAt);
        Assert.Equal(1234, after.UsedMs);
    }

    [Fact]
    public async Task A_failed_recompute_stops_being_pending_and_says_it_failed()
    {
        using var db = TestDb.Create();
        var sample = await SeedAsync(db);

        var result = await Callback(db).Failure(new VoiceprintFailure(sample.Id, "ffmpeg exploded"));

        Assert.IsType<NoContentResult>(result);
        var after = await db.VoiceSamples.SingleAsync(v => v.Id == sample.Id);
        Assert.Null(after.RecomputeQueuedAt);
        Assert.NotNull(after.RecomputeFailedAt);
    }

    [Fact]
    public async Task A_failed_recompute_keeps_the_duration_it_had()
    {
        // The old callback wrote UsedMs = 0 purely to stop the row spinning, so a failure rendered as
        // "trains on 0:00" - a successful-looking figure for audio that was never re-measured. The vector
        // is untouched on failure, so the duration describing it must be too.
        using var db = TestDb.Create();
        var sample = await SeedAsync(db);

        await Callback(db).Failure(new VoiceprintFailure(sample.Id, "ffmpeg exploded"));

        Assert.Equal(4000, (await db.VoiceSamples.SingleAsync(v => v.Id == sample.Id)).UsedMs);
    }

    [Fact]
    public async Task A_success_clears_an_earlier_failure()
    {
        // Otherwise a row that failed once carries the warning for ever, including after the retry that
        // fixed it.
        using var db = TestDb.Create();
        var sample = await SeedAsync(db);
        sample.RecomputeFailedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();

        await Callback(db).Result(new VoiceprintResult(sample.Id, [0.5f], 10, 10));

        Assert.Null((await db.VoiceSamples.SingleAsync(v => v.Id == sample.Id)).RecomputeFailedAt);
    }
}
