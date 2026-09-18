using Diariz.Api.Auth;

namespace Diariz.Api.Tests;

public class WorkerSecretTests
{
    [Fact]
    public void Matches_AcceptsTheConfiguredSecret() =>
        Assert.True(WorkerSecret.Matches("a-long-shared-worker-secret", "a-long-shared-worker-secret"));

    [Theory]
    [InlineData("not-the-secret")]
    [InlineData("a-long-shared-worker-secre")]    // a prefix is not a match
    [InlineData("a-long-shared-worker-secrett")]  // nor is a longer value
    [InlineData("")]
    [InlineData(null)]
    public void Matches_RejectsAnythingElse(string? presented) =>
        Assert.False(WorkerSecret.Matches(presented, "a-long-shared-worker-secret"));

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("   ", "   ")]
    public void Matches_NeverAcceptsWhenNoSecretIsConfigured(string? presented, string? configured) =>
        Assert.False(WorkerSecret.Matches(presented, configured));
}
