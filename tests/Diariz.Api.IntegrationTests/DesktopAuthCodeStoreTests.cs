using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using StackExchange.Redis;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class DesktopAuthCodeStoreTests(ContainersFixture fx)
{
    [Fact]
    public async Task Mint_then_redeem_returns_ticket_exactly_once()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var store = new RedisDesktopAuthCodeStore(redis);
        var uid = Guid.NewGuid();

        var code = await store.MintAsync(uid, "chal-abc", TimeSpan.FromMinutes(2));
        var first = await store.RedeemAsync(code);
        var second = await store.RedeemAsync(code);

        Assert.NotNull(first);
        Assert.Equal(uid, first!.UserId);
        Assert.Equal("chal-abc", first.Challenge);
        Assert.Null(second); // single-use: GETDEL removed it
    }

    [Fact]
    public async Task Redeem_unknown_code_returns_null()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fx.RedisConnectionString);
        var store = new RedisDesktopAuthCodeStore(redis);
        Assert.Null(await store.RedeemAsync("nope"));
    }
}
