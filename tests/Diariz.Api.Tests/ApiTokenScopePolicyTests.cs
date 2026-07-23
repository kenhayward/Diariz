using Diariz.Api.Auth;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class ApiTokenScopePolicyTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void ReadOnly_blocks_unsafe_verbs(string method) =>
        Assert.True(ApiTokenScopePolicy.BlocksWrite(method, ApiTokenScope.ReadOnly));

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ReadOnly_allows_safe_verbs(string method) =>
        Assert.False(ApiTokenScopePolicy.BlocksWrite(method, ApiTokenScope.ReadOnly));

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public void ReadWrite_allows_everything(string method) =>
        Assert.False(ApiTokenScopePolicy.BlocksWrite(method, ApiTokenScope.ReadWrite));
}
