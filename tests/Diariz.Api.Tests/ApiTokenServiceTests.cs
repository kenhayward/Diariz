using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class ApiTokenServiceTests
{
    [Fact]
    public void Generate_ProducesPrefixedToken_WithMatchingHashAndDisplayPrefix()
    {
        var g = new ApiTokenService().Generate();
        Assert.StartsWith("dz_api_", g.Token);
        Assert.Equal(ApiTokenService.Hash(g.Token), g.Hash);
        Assert.Equal(64, g.Hash.Length);                 // lowercase hex SHA-256
        Assert.Equal(g.Token[..13], g.Prefix);           // dz_api_ + 6 chars
    }

    [Fact]
    public void Generate_IsUniquePerCall()
    {
        Assert.NotEqual(new ApiTokenService().Generate().Token, new ApiTokenService().Generate().Token);
    }
}
