using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class ActionsIntegrationTests(ContainersFixture fx)
{
    private static ActionsController Build(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task List_And_Complete_AcrossRecordings_RoundTripThroughRealDb()
    {
        Guid userId, a1, a2;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var rec1 = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k1", Title = "Standup", Name = "Daily standup" };
            var rec2 = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k2", Title = "Planning" };
            var act1 = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec1.Id, Text = "Send report", Actor = "Bob", Ordinal = 0 };
            var act2 = new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec2.Id, Text = "Book room", Actor = "Alice", Ordinal = 0 };
            db.AddRange(user, rec1, rec2, act1, act2);
            await db.SaveChangesAsync();
            (userId, a1, a2) = (user.Id, act1.Id, act2.Id);
        }

        // GET /api/actions returns both, each tagged with its source recording's display name.
        await using (var db = fx.CreateDbContext())
        {
            var list = (await Build(db, userId).List()).Value;
            Assert.NotNull(list);
            Assert.Equal(2, list!.Count);
            Assert.Contains(list, d => d.Text == "Send report" && d.RecordingName == "Daily standup");
            Assert.Contains(list, d => d.Text == "Book room" && d.RecordingName == "Planning");
            Assert.All(list, d => Assert.False(d.Completed));
        }

        // Complete one, then un-complete it — verify the timestamp is set then cleared in real Postgres.
        await using (var db = fx.CreateDbContext())
            Assert.IsType<NoContentResult>(await Build(db, userId).Complete(new CompleteActionsRequest([a1], true)));

        await using (var verify = fx.CreateDbContext())
        {
            var done = await verify.RecordingActions.FindAsync(a1);
            Assert.True(done!.Completed);
            Assert.NotNull(done.CompletedAt);
            Assert.False((await verify.RecordingActions.FindAsync(a2))!.Completed);
        }

        await using (var db = fx.CreateDbContext())
            await Build(db, userId).Complete(new CompleteActionsRequest([a1], false));

        await using (var verify = fx.CreateDbContext())
        {
            var reopened = await verify.RecordingActions.FindAsync(a1);
            Assert.False(reopened!.Completed);
            Assert.Null(reopened.CompletedAt);
        }
    }
}
