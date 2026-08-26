using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>The worker's side of the on-demand re-embed landing back in the API.</summary>
public class WorkerVoiceprintCallbackTests
{
    private const string Secret = "shared-secret";

    /// <summary>Records which people were asked to recompute, so the test can tell that the centroid was
    /// rebuilt rather than left averaging the sample's old vector.</summary>
    private sealed class RecordingPeopleDirectory(DiarizDbContext db) : IPeopleDirectory
    {
        private readonly PeopleDirectory _inner = new(db);
        public List<Guid> Recomputed { get; } = [];

        public Task<Person> EnsureForUserAsync(Guid userId, CancellationToken ct = default) =>
            _inner.EnsureForUserAsync(userId, ct);

        public Task SyncFromUserAsync(Guid userId, CancellationToken ct = default) =>
            _inner.SyncFromUserAsync(userId, ct);

        public Task RecomputeVoiceprintAsync(Guid personId, CancellationToken ct = default)
        {
            Recomputed.Add(personId);
            return _inner.RecomputeVoiceprintAsync(personId, ct);
        }

        public Task EraseVoiceprintAsync(Guid personId, CancellationToken ct = default) =>
            _inner.EraseVoiceprintAsync(personId, ct);
    }

    private static (WorkerVoiceprintCallbackController controller, RecordingPeopleDirectory directory) Build(
        DiarizDbContext db, string presentedSecret = Secret)
    {
        var directory = new RecordingPeopleDirectory(db);
        var controller = new WorkerVoiceprintCallbackController(
            db, directory, Options.Create(new WorkerOptions { CallbackSecret = Secret }),
            NullLogger<WorkerVoiceprintCallbackController>.Instance)
        {
            ControllerContext = Http.Context(headers: ("X-Worker-Secret", presentedSecret)),
        };
        return (controller, directory);
    }

    /// <summary>A person with one pending sample: spans chosen, UsedMs cleared by the enqueue, and the
    /// contributing speaker flagged stale by an earlier reassignment.</summary>
    private static async Task<(Guid personId, Guid sampleId, Guid speakerId)> SeedPendingSample(
        DiarizDbContext db, bool speakerStale = true)
    {
        var userId = Guid.NewGuid();
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice", SampleCount = 1 };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "Standup", BlobKey = "k" };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice",
            PersonId = person.Id, EmbeddingStale = speakerStale,
        };
        var sample = new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
            SpansJson = VoiceprintSpans.Serialize([new VoiceprintSpan(1000, 3000)]),
            UsedMs = null,
        };
        db.AddRange(person, rec, speaker, sample);
        await db.SaveChangesAsync();
        return (person.Id, sample.Id, speaker.Id);
    }

    [Fact]
    public async Task Result_WithWrongSecret_IsUnauthorized()
    {
        using var db = TestDb.Create();
        var (_, sampleId, _) = await SeedPendingSample(db);
        var (controller, _) = Build(db, presentedSecret: "not-the-secret");

        Assert.IsType<UnauthorizedResult>(
            await controller.Result(new VoiceprintResult(sampleId, [1f, 0f], 1000, 2000)));
    }

    [Fact]
    public async Task Failure_WithWrongSecret_IsUnauthorized()
    {
        using var db = TestDb.Create();
        var (_, sampleId, _) = await SeedPendingSample(db);
        var (controller, _) = Build(db, presentedSecret: "not-the-secret");

        Assert.IsType<UnauthorizedResult>(await controller.Failure(new VoiceprintFailure(sampleId, "boom")));
    }

    [Fact]
    public async Task Result_RecordsWhatWasUsedSoTheSampleStopsReadingAsPending()
    {
        using var db = TestDb.Create();
        var (_, sampleId, _) = await SeedPendingSample(db);
        var (controller, _) = Build(db);

        Assert.IsType<NoContentResult>(
            await controller.Result(new VoiceprintResult(sampleId, [0.6f, 0.8f], 120000, 200000)));

        var after = db.VoiceSamples.Single();
        Assert.Equal(120000, after.UsedMs);
        Assert.Null(after.RecomputeQueuedAt);
    }

    [Fact]
    public async Task Result_ClearsTheContributingSpeakersStaleFlag()
    {
        using var db = TestDb.Create();
        var (_, sampleId, speakerId) = await SeedPendingSample(db, speakerStale: true);
        var (controller, _) = Build(db);

        await controller.Result(new VoiceprintResult(sampleId, [0.6f, 0.8f], 1000, 1000));

        Assert.False(db.Speakers.Single(s => s.Id == speakerId).EmbeddingStale);
    }

    [Fact]
    public async Task Result_RecomputesThePersonsCentroid()
    {
        // The sample's vector changed, so the average of the samples did too. Without this the person's
        // voiceprint silently keeps averaging the old one and identification does not improve.
        using var db = TestDb.Create();
        var (personId, sampleId, _) = await SeedPendingSample(db);
        var (controller, directory) = Build(db);

        await controller.Result(new VoiceprintResult(sampleId, [0.6f, 0.8f], 1000, 1000));

        Assert.Equal([personId], directory.Recomputed);
    }

    [Fact]
    public async Task Result_ForAnUnknownSample_IsNotFound()
    {
        using var db = TestDb.Create();
        await SeedPendingSample(db);
        var (controller, _) = Build(db);

        Assert.IsType<NotFoundResult>(
            await controller.Result(new VoiceprintResult(Guid.NewGuid(), [1f], 1, 1)));
    }

    [Fact]
    public async Task Failure_LeavesTheSampleUsableRatherThanPendingForever()
    {
        // A failure has to stop the row reading as pending, or a dead job is indistinguishable from a slow
        // one. It used to do that by writing UsedMs = 0, which rendered as "trains on 0:00" - a confident
        // figure for audio that was never measured. Now it says plainly that it failed.
        using var db = TestDb.Create();
        var (_, sampleId, _) = await SeedPendingSample(db);
        var (controller, _) = Build(db);

        Assert.IsType<NoContentResult>(await controller.Failure(new VoiceprintFailure(sampleId, "boom")));

        var after = db.VoiceSamples.Single();
        Assert.Null(after.RecomputeQueuedAt);
        Assert.NotNull(after.RecomputeFailedAt);
    }

    [Fact]
    public async Task Failure_DoesNotRecomputeTheCentroid()
    {
        // Nothing was embedded, so the average did not change. Recomputing would be busywork that also
        // makes a failed job look like a successful one in the logs.
        using var db = TestDb.Create();
        var (_, sampleId, _) = await SeedPendingSample(db);
        var (controller, directory) = Build(db);

        await controller.Failure(new VoiceprintFailure(sampleId, "boom"));

        Assert.Empty(directory.Recomputed);
    }

    [Fact]
    public async Task Failure_KeepsTheSpeakerFlaggedStale()
    {
        // The audio still does not match the stored vector - the recompute that would have fixed it did
        // not happen. Clearing the flag would hide a real problem behind a failed job.
        using var db = TestDb.Create();
        var (_, sampleId, speakerId) = await SeedPendingSample(db, speakerStale: true);
        var (controller, _) = Build(db);

        await controller.Failure(new VoiceprintFailure(sampleId, "boom"));

        Assert.True(db.Speakers.Single(s => s.Id == speakerId).EmbeddingStale);
    }
}
