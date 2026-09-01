using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Webhooks;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>Shared seeding and wiring for the live-capture tests, so the three files that exercise
/// begin / chunk / finalise / quota do not each carry a copy of the controller's constructor.</summary>
internal static class LiveTestSupport
{
    public static async Task SeedUser(DiarizDbContext db, Guid userId, long? quotaBytes = null)
    {
        var u = new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" };
        if (quotaBytes is not null) u.QuotaBytes = quotaBytes.Value;
        db.Users.Add(u);
        await db.SaveChangesAsync();
    }

    /// <summary>A shared room the user belongs to with no write permission - the 403 case.</summary>
    public static async Task<Guid> SeedSharedRoomWithoutPermission(DiarizDbContext db, Guid userId)
    {
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, userId, RoomPermission.None);
        return roomId;
    }

    public static RecordingsController Build(
        DiarizDbContext db, Guid userId, FakeJobQueue? queue = null, FakeAudioStorage? storage = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Transcription:DefaultModel"] = "whisperx-large-v3",
            })
            .Build();
        var resolver = new LlmSettingsResolver(
            db,
            Options.Create(new LlmDefaultsOptions()),
            Options.Create(new SummarizationOptions { ApiBase = "http://llm.test/v1" }),
            new FakeApiKeyProtector(),
            new ChatModelCatalog(db, Options.Create(new LlmDefaultsOptions())));

        return new RecordingsController(
            db, storage ?? new FakeAudioStorage(), queue ?? new FakeJobQueue(), new FakeHubContext(), config,
            resolver, new FakeEmailSender(), new FakeSpeakerIdentification(new FakeSpeakerIdentifier()),
            new SpeakerAssignment(db, new PeopleDirectory(db)),
            Options.Create(new UploadOptions()), new RoomScope(db), new PeopleDirectory(db),
            new CapturingWebhookPublisher(), Options.Create(new AppPublicOptions()))
        {
            ControllerContext = Http.Context(userId),
        };
    }

    /// <summary>The worker merge callback, wired to the same fakes as the controller under test.</summary>
    public static WorkerMergeCallbackController MergeCallback(
        DiarizDbContext db, FakeAudioStorage storage, FakeJobQueue queue, string secret = "s3cret") =>
        new(db, new FakeHubContext(), storage, queue,
            Options.Create(new WorkerOptions { CallbackSecret = secret }))
        {
            ControllerContext = Http.Context(Guid.NewGuid(), ("X-Worker-Secret", secret)),
        };
}
