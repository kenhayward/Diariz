using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

public class ChatContextResolverTests
{
    private static ChatContextResolver Build(DiarizDbContext db, int serverDefault = 131072) =>
        new(db, Options.Create(new ChatOptions { ContextLength = serverDefault }));

    [Fact]
    public async Task NoUserSettings_UsesServerDefault()
    {
        using var db = TestDb.Create();
        Assert.Equal(131072, await Build(db).ResolveContextWindowAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task UserOverride_Wins()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings { UserId = userId, ChatContextWindow = 8000 });
        await db.SaveChangesAsync();

        Assert.Equal(8000, await Build(db).ResolveContextWindowAsync(userId));
    }

    [Fact]
    public async Task NullOrNonPositiveOverride_FallsBackToServerDefault()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.UserSettings.Add(new UserSettings { UserId = userId, ChatContextWindow = 0 });
        await db.SaveChangesAsync();

        Assert.Equal(4096, await Build(db, serverDefault: 4096).ResolveContextWindowAsync(userId));
    }
}
