using Diariz.Api.OpenApi;

namespace Diariz.Api.Tests;

public class OpenApiCurationTests
{
    [Theory]
    [InlineData("api/recordings", true)]
    [InlineData("api/user/api-tokens", true)]
    [InlineData("api/platform/settings", true)]
    [InlineData("api/oauth/connections", false)]  // OAuth plumbing excluded
    [InlineData("api/oauth", false)]
    [InlineData("internal/transcriptions/result", false)]
    [InlineData("connect/register", false)]
    [InlineData(".well-known/oauth-protected-resource", false)]
    [InlineData(null, false)]
    public void ShouldInclude_KeepsUserApiOnly(string? relativePath, bool expected) =>
        Assert.Equal(expected, OpenApiCuration.ShouldInclude(relativePath));
}
