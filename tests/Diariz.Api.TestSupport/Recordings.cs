using Diariz.Api.Configuration;
using Diariz.Api.Controllers;
using Diariz.Api.Services.Llm;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Builds a <see cref="RecordingsController"/> with default fakes for every one of its 15
/// constructor dependencies, for tests that only exercise a couple of them and would otherwise have to repeat
/// the whole construction. <c>RecordingsControllerTests.Build</c> already has a richer helper with knobs for
/// calendars/uploads/summarisation, used by hundreds of tests in that file - this is a second, minimal
/// construction site for tests that live outside it (the tag endpoints), not a replacement for it. Do not
/// widen this one with test-specific knobs; add them to the richer helper instead if a tag test ever needs
/// one.</summary>
public static class Recordings
{
    public static RecordingsController Build(DiarizDbContext db, Guid userId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transcription:DefaultModel"] = "whisperx-large-v3" })
            .Build();
        var resolver = new LlmSettingsResolver(
            db, Options.Create(new LlmDefaultsOptions()),
            Options.Create(new SummarizationOptions { ApiBase = "http://llm.test/v1" }), new FakeApiKeyProtector());

        return new RecordingsController(
            db, new FakeAudioStorage(), new FakeJobQueue(), new FakeHubContext(), config,
            resolver, new FakeEmailSender(), new FakeSpeakerIdentifier(),
            Options.Create(new UploadOptions()), new RoomScope(db), new PeopleDirectory(db),
            new CapturingWebhookPublisher(), Options.Create(new AppPublicOptions()), null,
            new CalendarAggregator(new NoGoogleCalendar(), new NoIcsFeeds(), new NoOutlookDevices(), db))
        {
            ControllerContext = Http.Context(userId)
        };
    }
}
