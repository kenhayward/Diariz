using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class FeedbackControllerTests
{
    private static FeedbackController Build(DiarizDbContext db, Guid userId) =>
        new(db, new FakeWebhookPublisher()) { ControllerContext = Http.Context(userId) };

    private static Guid SeedUser(DiarizDbContext db)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, Email = "u@e.com", UserName = "u@e.com" });
        db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Create_StoresAgainstTheCallingUser()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);

        await Build(db, userId).Create(new CreateFeedbackRequest(
            "The delete button was enabled", "/recordings/1", "0.176.0", "[]"));

        var row = await db.Feedback.SingleAsync();
        Assert.Equal(userId, row.UserId);
        Assert.Equal("The delete button was enabled", row.Description);
        Assert.Equal("/recordings/1", row.Route);
    }

    [Fact]
    public async Task Create_StoresCreatedAtAsUtc()
    {
        // Npgsql rejects a non-zero-offset DateTimeOffset on a timestamptz column. The in-memory
        // provider does not enforce it, so this only guards the shape; the integration suite proves it.
        var db = TestDb.Create();
        var userId = SeedUser(db);

        await Build(db, userId).Create(new CreateFeedbackRequest("x", "/", "0.176.0", "[]"));

        Assert.Equal(TimeSpan.Zero, (await db.Feedback.SingleAsync()).CreatedAt.Offset);
    }

    [Fact]
    public async Task Create_RejectsAnEmptyDescription()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);

        var result = await Build(db, userId).Create(new CreateFeedbackRequest("   ", "/", "0.176.0", "[]"));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.Feedback);
    }

    [Fact]
    public async Task Create_TruncatesAnOverlongDescription()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);

        await Build(db, userId).Create(new CreateFeedbackRequest(
            new string('x', FeedbackController.MaxDescription + 500), "/", "0.176.0", "[]"));

        Assert.Equal(FeedbackController.MaxDescription, (await db.Feedback.SingleAsync()).Description.Length);
    }
}
