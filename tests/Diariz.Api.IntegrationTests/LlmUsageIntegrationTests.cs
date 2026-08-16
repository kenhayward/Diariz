using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class LlmUsageIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task LlmCall_RoundTripsThroughPostgres()
    {
        await using var db = fx.CreateDbContext();
        var started = DateTimeOffset.UtcNow.AddSeconds(-3);

        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = LlmCallKind.Summarize,
            UserEmail = "owner@example.com",
            Model = "qwen3-30b",
            Endpoint = "http://lmstudio:1234/v1/chat/completions",
            StartedAt = started,
            CompletedAt = started.AddSeconds(3),
            DurationMs = 3000,
            PromptTokens = 1200,
            CompletionTokens = 340,
            TotalTokens = 1540,
            Streamed = false,
            Success = true,
            StatusCode = 200,
        };
        db.LlmCalls.Add(call);
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        var found = await read.LlmCalls.SingleAsync(c => c.Id == call.Id);
        Assert.Equal(LlmCallKind.Summarize, found.Kind);
        Assert.Equal(1540, found.TotalTokens);
        Assert.Equal(TimeSpan.Zero, found.StartedAt.Offset); // stored as UTC
    }

    [Fact]
    public async Task DeletingTheUser_NullsTheLink_ButKeepsTheRowAndTheEmailSnapshot()
    {
        // The log is an audit trail: a user's history must survive their deletion, which is why
        // UserEmail is a snapshot rather than a join. Bulk delete is the erasure escape hatch.
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = "leaver@example.com" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var call = new LlmCall
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            Sequence = 1,
            Kind = LlmCallKind.Tags,
            UserId = user.Id,
            UserEmail = user.Email!,
            Model = "m",
            Endpoint = "http://x/v1",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 10,
            Success = true,
        };
        db.LlmCalls.Add(call);
        await db.SaveChangesAsync();

        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        var found = await read.LlmCalls.SingleAsync(c => c.Id == call.Id);
        Assert.Null(found.UserId);
        Assert.Equal("leaver@example.com", found.UserEmail);
    }
}
