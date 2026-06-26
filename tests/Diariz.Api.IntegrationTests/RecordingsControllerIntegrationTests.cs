using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
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
        return new RecordingsController(db, new FakeAudioStorage(), new FakeJobQueue(), new FakeHubContext(), config)
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
}
