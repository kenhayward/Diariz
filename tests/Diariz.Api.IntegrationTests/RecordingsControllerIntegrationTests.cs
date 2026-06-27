using Diariz.Api.Services;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Options;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class RecordingsControllerIntegrationTests(ContainersFixture fx)
{
    private static RecordingsController Build(Diariz.Domain.DiarizDbContext db, Guid userId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Transcription:DefaultModel"] = "whisperx-large-v3" })
            .Build();
        var resolver = new SummarizationSettingsResolver(
            db, Options.Create(new SummarizationOptions { ApiBase = "http://llm.test/v1" }), new FakeApiKeyProtector());
        return new RecordingsController(db, new FakeAudioStorage(), new FakeJobQueue(), new FakeHubContext(), config,
            resolver, new FakeEmailSender(), new FakeSpeakerIdentifier())
        {
            ControllerContext = Http.Context(userId)
        };
    }

    private async Task<(Guid userId, Guid recId)> SeedRecording()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
        db.Users.Add(user);
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return (user.Id, rec.Id);
    }

    [Fact]
    public async Task RenameSpeaker_CreatesSpeaker_WhenLabelNotYetPresent()
    {
        var (userId, recId) = await SeedRecording();

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(
                await Build(db, userId).RenameSpeaker(recId, new RenameSpeakerRequest("SPEAKER_07", "Bob")));

        await using var verify = fx.CreateDbContext();
        var sp = await verify.Speakers.SingleAsync(s => s.RecordingId == recId);
        Assert.Equal("SPEAKER_07", sp.Label);
        Assert.Equal("Bob", sp.DisplayName);
    }

    [Fact]
    public async Task Delete_CascadesTranscriptionsSegmentsSpeakersAndSummary()
    {
        Guid userId, recId, trId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
            db.AddRange(
                user, rec, tr,
                new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1, Text = "x", Ordinal = 0 },
                new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "A" },
                new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "gpt", Text = "s" });
            await db.SaveChangesAsync();
            (userId, recId, trId) = (user.Id, rec.Id, tr.Id);
        }

        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(await Build(db, userId).Delete(recId));

        await using var verify = fx.CreateDbContext();
        Assert.Null(await verify.Recordings.FindAsync(recId));
        Assert.False(await verify.Transcriptions.AnyAsync(t => t.RecordingId == recId));
        Assert.False(await verify.Segments.AnyAsync(s => s.TranscriptionId == trId));
        Assert.False(await verify.Speakers.AnyAsync(s => s.RecordingId == recId));
        Assert.False(await verify.Summaries.AnyAsync(s => s.TranscriptionId == trId));
    }

    [Fact]
    public async Task Get_ReturnsHighestVersionTranscription_WithSummary()
    {
        Guid userId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k", Name = "Demo" };
            db.AddRange(user, rec);
            foreach (var v in new[] { 1, 2, 3 })
            {
                var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = v };
                db.Add(tr);
                if (v == 3) db.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "gpt", Text = "The summary" });
            }
            await db.SaveChangesAsync();
            (userId, recId) = (user.Id, rec.Id);
        }

        await using var db2 = fx.CreateDbContext();
        var result = await Build(db2, userId).Get(recId);

        var dto = Assert.IsType<RecordingDetailDto>(result.Value);
        Assert.Equal(3, dto.Current!.Version);
        Assert.Equal("Demo", dto.Name);
        Assert.Equal("The summary", dto.Summary!.Text);
    }
}
