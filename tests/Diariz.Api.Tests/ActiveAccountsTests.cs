using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class ActiveAccountsTests
{
    [Theory]
    [InlineData(true, UserStatus.Active, true)]
    [InlineData(false, UserStatus.Active, false)]
    [InlineData(true, UserStatus.Invited, false)]
    [InlineData(true, UserStatus.Requested, false)]
    public async Task IsActive_RequiresAnEnabledAccountThatHasFinishedSetup(bool enabled, UserStatus status, bool expected)
    {
        using var db = TestDb.Create();
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, UserName = "a@x.test", Email = "a@x.test", IsEnabled = enabled, Status = status });
        await db.SaveChangesAsync();

        Assert.Equal(expected, await new ActiveAccounts(db).IsActiveAsync(id));
    }

    [Fact]
    public async Task IsActive_IsFalseForAnUnknownAccount()
    {
        using var db = TestDb.Create();
        Assert.False(await new ActiveAccounts(db).IsActiveAsync(Guid.NewGuid()));
    }
}
