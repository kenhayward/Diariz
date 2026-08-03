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

    /// <summary>The trail lands verbatim in an unbounded text column, and the submitter cannot delete their
    /// own rows - only an admin can. Without a cap, any signed-in user could park Kestrel's whole default
    /// body (~30 MB) per submission, repeatedly. Rejected rather than truncated: half a JSON array is not
    /// JSON, so a silent trim would store something no reader could parse.</summary>
    [Fact]
    public async Task Create_RejectsAnOverlongTrail()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);

        var result = await Build(db, userId).Create(new CreateFeedbackRequest(
            "x", "/", "0.176.0", new string('x', FeedbackController.MaxTrailJson + 1)));

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.Feedback);
    }

    /// <summary>The cap is generous by design - a 30-entry trail is a few kilobytes - so a trail right at
    /// the limit must still be accepted whole. Guards against the cap being tightened to something a real
    /// submission could hit.</summary>
    [Fact]
    public async Task Create_StoresATrailUpToTheCap()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);
        var trail = new string('x', FeedbackController.MaxTrailJson);

        await Build(db, userId).Create(new CreateFeedbackRequest("x", "/", "0.176.0", trail));

        Assert.Equal(trail, (await db.Feedback.SingleAsync()).TrailJson);
    }

    [Fact]
    public async Task List_ReturnsNewestFirst_WithSubmitterEmail()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);
        db.Feedback.AddRange(
            new Feedback { UserId = userId, Description = "older", CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) },
            new Feedback { UserId = userId, Description = "newer", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var result = await Build(db, userId).List();

        var items = Assert.IsType<List<FeedbackDto>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(new[] { "newer", "older" }, items.Select(i => i.Description));
        Assert.Equal("u@e.com", items[0].UserEmail);
    }

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);
        var row = new Feedback { UserId = userId, Description = "x" };
        db.Feedback.Add(row);
        await db.SaveChangesAsync();

        await Build(db, userId).Delete(row.Id);

        Assert.Empty(db.Feedback);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_ForAnUnknownId()
    {
        var db = TestDb.Create();
        var userId = SeedUser(db);

        Assert.IsType<NotFoundResult>(await Build(db, userId).Delete(Guid.NewGuid()));
    }
}
